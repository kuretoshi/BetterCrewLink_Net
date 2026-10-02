namespace TanukiBCL.Client;

// One lease covers setup, running, and teardown. In particular, a new connection
// must not reuse the audio devices while the previous connection is disposing.
internal sealed class ClientSessionCoordinator
{
    private readonly object gate = new();
    private Session? current;

    public Session? Current
    {
        get { lock (gate) return current; }
    }

    public bool TryStart(out Session session)
    {
        lock (gate)
        {
            if (current is not null)
            {
                session = null!;
                return false;
            }
            current = session = new Session(this);
            return true;
        }
    }

    private void Release(Session session)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, session)) current = null;
        }
    }

    internal sealed class Session
    {
        private readonly ClientSessionCoordinator owner;
        private readonly CancellationTokenSource cancellation = new();
        private readonly object cancellationGate = new();
        private readonly List<Exception> errors = [];
        private readonly Stack<Func<ValueTask>> cleanup = new();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int acceptingCallbacks = 1;
        private int started;
        private bool disposed;

        public Session(ClientSessionCoordinator owner)
        {
            this.owner = owner;
            Token = cancellation.Token;
        }

        public CancellationToken Token { get; }
        public Task Completion => completion.Task;
        public bool AcceptsCallbacks => Volatile.Read(ref acceptingCallbacks) != 0 &&
            !Token.IsCancellationRequested && ReferenceEquals(owner.Current, this);

        // Guard at execution time, not enqueue time: a WPF callback may have been
        // queued before shutdown but execute after the next session has started.
        public Action Guard(Action callback) => () =>
        {
            if (AcceptsCallbacks) callback();
        };

        public void RequestStop()
        {
            Interlocked.Exchange(ref acceptingCallbacks, 0);
            lock (cancellationGate)
            {
                if (disposed || cancellation.IsCancellationRequested) return;
                try { cancellation.Cancel(); }
                catch (Exception error) { errors.Add(error); }
            }
        }

        // Register each resource immediately after acquiring it. These callbacks
        // run in reverse order, even when setup or a preceding disposal fails.
        public void AddCleanup(Func<ValueTask> action) => cleanup.Push(action);

        public async Task RunAsync(Func<Task> body, Action<SessionResult> finished)
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                throw new InvalidOperationException("A client session can only run once.");
            try
            {
                await body();
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                lock (cancellationGate) errors.Add(error);
            }
            finally
            {
                RequestStop();
                while (cleanup.TryPop(out var action))
                {
                    try { await action(); }
                    catch (Exception error)
                    {
                        lock (cancellationGate) errors.Add(error);
                    }
                }

                SessionResult result;
                lock (cancellationGate)
                {
                    cancellation.Dispose();
                    disposed = true;
                    result = new SessionResult(errors.ToArray());
                }
                try { finished(result); }
                catch (Exception error)
                {
                    // Event handlers are async-void. No final UI-reporting fault
                    // should escape them or leave a pending settings restart.
                    System.Diagnostics.Trace.TraceError($"Client session completion failed: {error}");
                }
                finally
                {
                    owner.Release(this);
                    completion.TrySetResult();
                }
            }
        }
    }

    internal sealed record SessionResult(IReadOnlyList<Exception> Errors)
    {
        public Exception? Error => Errors.Count switch
        {
            0 => null,
            1 => Errors[0],
            _ => new AggregateException(Errors)
        };
    }
}
