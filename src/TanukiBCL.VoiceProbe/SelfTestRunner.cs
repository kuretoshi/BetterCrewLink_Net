using SIPSorcery.Net;

namespace TanukiBCL.VoiceProbe;

internal static class SelfTestRunner
{
    public static async Task<int> RunAsync(ProbeOptions baseOptions, bool expectPeerQuality = false,
        bool mixedNat = false, bool failOnRecovery = false)
    {
        DtlsRecordHeaderTrace.Verify();
        var tcpOptions = ProbeOptions.Parse(["--self-test", "--nat-fix", "--turn-tcp"]);
        if (!tcpOptions.NatFix || !tcpOptions.TurnTcp ||
            ProbeOptions.Parse(["--self-test", "--nat-fix"]).TurnTcp)
            throw new InvalidOperationException("TURN transport CLI selection did not preserve the UDP default.");
        using (var peerManager = new WebRtcPeerManager("self-test", (_, _) => Task.CompletedTask, false))
        {
            if (peerManager.ShouldDeferIncomingOffer("unseen-peer"))
            {
                throw new InvalidOperationException("An incoming offer was discarded before a local offer existed.");
            }
        }
        if (!WebRtcPeerManager.ShouldRecoverClosedSctp(RTCPeerConnectionState.connected,
                RTCSctpTransportState.Closed, false) ||
            WebRtcPeerManager.ShouldRecoverClosedSctp(RTCPeerConnectionState.connecting,
                RTCSctpTransportState.Closed, false) ||
            WebRtcPeerManager.ShouldRecoverClosedSctp(RTCPeerConnectionState.connected,
                RTCSctpTransportState.Connected, false) ||
            WebRtcPeerManager.ShouldRecoverClosedSctp(RTCPeerConnectionState.connected,
                RTCSctpTransportState.Closed, true))
            throw new InvalidOperationException("SCTP closed-state recovery gate is incorrect.");
        if (ShouldRejectRecoveredSuccess(false, 1) ||
            ShouldRejectRecoveredSuccess(true, 0) ||
            !ShouldRejectRecoveredSuccess(true, 1))
            throw new InvalidOperationException("First-attempt reliability gate is incorrect.");

        var lobby = baseOptions.LobbyCode ?? CreateLobbyCode();
        var timeout = baseOptions.Duration ?? TimeSpan.FromSeconds(30);
        var seed = Random.Shared.Next(100_000, 900_000);

        var firstOptions = baseOptions with
        {
            LobbyCode = lobby,
            PlayerId = 0,
            ClientId = seed,
            IsHost = true,
            Duration = null,
            SelfTest = false,
            LiveAudio = false,
            GameProcessId = null
        };
        var secondOptions = baseOptions with
        {
            LobbyCode = lobby,
            PlayerId = 1,
            ClientId = seed + 1,
            IsHost = false,
            Duration = null,
            SelfTest = false,
            NatFix = mixedNat ? !baseOptions.NatFix : baseOptions.NatFix,
            LiveAudio = false,
            GameProcessId = null
        };

        Console.WriteLine($"P2Pセルフテスト開始: lobby={lobby} timeout={timeout.TotalSeconds:0}s natFix={firstOptions.NatFix}/{secondOptions.NatFix}");
        using var cancellation = new CancellationTokenSource(timeout);
        await using var first = new VoiceServerProbe(firstOptions, "A");
        await using var second = new VoiceServerProbe(secondOptions, "B");
        var hostSettings = new LobbySettings { MaxDistance = 7.4d, ImpostorRadioEnabled = true };
        var settingsVerified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updatedSettings = hostSettings with { MaxDistance = 3.6d, Haunting = true };
        var updateVerified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualityVerified = new TaskCompletionSource<ConnectionQuality>(TaskCreationOptions.RunContinuationsAsynchronously);
        var roundTripVerified = new TaskCompletionSource<ConnectionQuality>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SetOwnLobbySettings(hostSettings);
        second.SetExpectedHostClientId(seed);
        second.LobbySettingsChanged += settings =>
        {
            if (settings == hostSettings)
            {
                settingsVerified.TrySetResult();
            }
            if (settings == updatedSettings)
            {
                updateVerified.TrySetResult();
            }
        };
        second.PeerQualityChanged += (clientId, quality) =>
        {
            if (clientId == seed && quality.JitterMs is not null && quality.LossPercent is not null)
                qualityVerified.TrySetResult(quality);
            if (clientId == seed && quality.RttMs is >= 0d and < 1_000d)
                roundTripVerified.TrySetResult(quality);
        };

        try
        {
            var firstRun = first.RunAsync(cancellation.Token);
            await first.Connected.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);

            // 先行クライアントがロビーに登録されてから2台目を接続する。
            await Task.Delay(500, cancellation.Token);
            var secondRun = second.RunAsync(cancellation.Token);

            await Task.WhenAll(
                first.PeerVerified.WaitAsync(timeout, cancellation.Token),
                second.PeerVerified.WaitAsync(timeout, cancellation.Token),
                first.AudioVerified.WaitAsync(timeout, cancellation.Token),
                second.AudioVerified.WaitAsync(timeout, cancellation.Token),
                settingsVerified.Task.WaitAsync(timeout, cancellation.Token));
            Console.WriteLine("[PASS] Both peers decoded the other's Opus test audio");

            first.SetOwnLobbySettings(updatedSettings);
            await updateVerified.Task.WaitAsync(timeout, cancellation.Token);
            if (second.CurrentLobbySettings != updatedSettings)
            {
                throw new InvalidOperationException("The guest's current-lobby settings do not match the host update.");
            }

            var recoveryCount = first.AutomaticPeerRecoveryCount + second.AutomaticPeerRecoveryCount;
            if (ShouldRejectRecoveredSuccess(failOnRecovery, recoveryCount))
            {
                Console.Error.WriteLine($"[FAIL] P2P接続は回復後に成功しましたが、自動再接続が{recoveryCount}回必要でした。");
                cancellation.Cancel();
                await IgnoreCancellationAsync(firstRun);
                await IgnoreCancellationAsync(secondRun);
                return 1;
            }
            if (recoveryCount > 0)
                Console.WriteLine($"[WARN] P2P接続の成功前に自動再接続が{recoveryCount}回必要でした。");

            if (expectPeerQuality)
            {
                var quality = await qualityVerified.Task.WaitAsync(timeout, cancellation.Token);
                if (!double.IsFinite(quality.JitterMs!.Value) || quality.JitterMs < 0d || quality.JitterMs > 1_000d ||
                    !double.IsFinite(quality.LossPercent!.Value) || quality.LossPercent < 0d || quality.LossPercent > 100d)
                    throw new InvalidOperationException("The reported RTP quality is outside plausible bounds.");
                if (!quality.Direct)
                    throw new InvalidOperationException("The local peer connection did not report a direct host ICE pair.");
                Console.WriteLine($"[PASS] RTP/RTCP受信品質を取得: jitter={quality.JitterMs:0.0} ms loss={quality.LossPercent:0.0}% direct={quality.Direct}");
                var withRoundTrip = await roundTripVerified.Task.WaitAsync(timeout, cancellation.Token);
                Console.WriteLine($"[PASS] RTCP往復時間を取得: rtt={withRoundTrip.RttMs:0.0} ms");
            }

            Console.WriteLine("[PASS] Socket.IO、双方向WebRTCデータチャネル、双方向Opus音声、ホストの3.2.7ロビー設定配信と変更反映を検証しました。");
            cancellation.Cancel();
            await IgnoreCancellationAsync(firstRun);
            await IgnoreCancellationAsync(secondRun);
            return 0;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 制限時間内にP2P接続を確認できませんでした。");
            return 1;
        }
    }

    private static string CreateLobbyCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
    }

    internal static bool ShouldRejectRecoveredSuccess(bool failOnRecovery, int recoveryCount) =>
        failOnRecovery && recoveryCount > 0;

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
