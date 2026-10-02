namespace TanukiBCL.Client;

internal sealed record ClientSettingsChange(ClientSettings Previous, ClientSettings Current);

internal sealed class ClientSettingsTransaction
{
    private readonly ClientSettings target;
    private readonly Action<ClientSettings> persistSettings;
    private ClientSettings persisted;

    public ClientSettingsTransaction(ClientSettings target, Action<ClientSettings> persist)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(persist);
        this.target = target;
        persistSettings = persist;
        persisted = target.Clone();
    }

    public event Action<ClientSettingsChange>? Changed;

    // Callers group dependent settings (for example ServerUrl and ServerUrls).
    // Empty propertyNames applies the entire candidate; otherwise only the
    // named keys are taken from it, preserving newer values in the shared target.
    public bool Apply(ClientSettings candidate, bool persist, params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(propertyNames);
        var next = target.Clone();
        next.CopyFrom(candidate, propertyNames);
        next.Normalize();
        var changed = !target.ContentEquals(next);

        if (persist)
        {
            var toSave = persisted.Clone();
            toSave.CopyFrom(next, propertyNames);
            if (!persisted.ContentEquals(toSave))
            {
                // The delegate receives its own copy: neither a failed save nor
                // a save implementation that normalizes can mutate runtime state.
                persistSettings(toSave.Clone());
                persisted = toSave;
            }
        }

        if (!changed) return false;
        var previous = target.Clone();
        target.CopyFrom(next);
        Changed?.Invoke(new ClientSettingsChange(previous, next.Clone()));
        return true;
    }

    public void Flush()
    {
        var toSave = target.Clone();
        if (persisted.ContentEquals(toSave)) return;
        persistSettings(toSave.Clone());
        // Failed writes leave this baseline untouched, so the next Flush retries.
        persisted = toSave;
    }
}
