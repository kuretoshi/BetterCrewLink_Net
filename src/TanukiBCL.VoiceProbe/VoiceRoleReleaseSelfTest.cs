using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave.SampleProviders;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class VoiceRoleReleaseSelfTest
{
    public static void Verify(Action<string, bool, bool> check)
    {
        VerifySpatial(check);
        VerifyFiles(check);
        VerifyHostAdmission(check);
        VerifyLoopback(check);
        VerifyDeathEcho(check);
        VerifyMegaphoneRecording(check);
        VerifyMegaphoneBuffering(check);
        DyingVoiceSelfTest.Verify(check);
        InterviewerSelfTest.Verify(check);
        VoiceJammingSelfTest.Verify(check);
        HeroCostumeVoiceSelfTest.Verify(check);
        VerifyNosRadioGap(check);
    }

    private static void VerifyNosRadioGap(Action<string, bool, bool> check)
    {
        var options = ProbeOptions.Parse(["--server", "http://127.0.0.1:1", "--lobby", "ABCDEF",
            "--client-id", "19", "--player-id", "2", "--host"]);
        var probe = new VoiceServerProbe(options, "nos-radio-gap-test");
        try
        {
            var local = new Player { Id = 2, ClientId = 19, IsLocal = true, IsImpostor = true };
            var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip,
                GameState = GameState.Tasks, LobbyCode = "ABCDEF", ClientId = 19, HostId = 19,
                IsHost = true, Players = [local],
                NosRadios = [new NosRadioData(NosRadioRules.ImpostorKind, 1 << 2, "")] };
            probe.SetOwnLobbySettings(new LobbySettings { ImpostorRadioEnabled = true });
            probe.ApplyGameState(state);
            probe.SetNosRadioPressed(NosRadioRules.ImpostorKind, true);
            check("NoS radio: initial hold transmits", true,
                probe.CurrentNosRadioKind == NosRadioRules.ImpostorKind && !probe.IsNosRadioPending);
            state.NosRadios = [];
            probe.ApplyGameState(state);
            check("NoS radio: temporary gap suspends and masks", true,
                probe.CurrentNosRadioKind is null && probe.IsNosRadioPending);
            state.NosRadios = [new NosRadioData(NosRadioRules.ImpostorKind, 1 << 2, "")];
            probe.ApplyGameState(state);
            check("NoS radio: held key resumes after data returns", true,
                probe.CurrentNosRadioKind == NosRadioRules.ImpostorKind && !probe.IsNosRadioPending);
            probe.SetNosRadioPressed(NosRadioRules.ImpostorKind, false);
            check("NoS radio: release clears transmission and pending", true,
                probe.CurrentNosRadioKind is null && !probe.IsNosRadioPending);
            state.Players = null!;
            state.GameState = GameState.Menu;
            probe.ApplyGameState(state);
            check("Among Us exit: missing player list becomes an empty roster", true,
                state.Players.Count == 0 && probe.CurrentNosRadioKind is null);
        }
        finally { probe.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void VerifySpatial(Action<string, bool, bool> check)
    {
        var listener = new Player { Id = 0, X = 0d, Y = 0d };
        var speaker = new Player { Id = 1, X = 8d, Y = 0d };
        var state = new AmongUsState
        {
            Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Tasks,
            NosMegaphones = [new NosMegaphoneState { PlayerId = 1, Energy = 0.5d, Range = 10d }],
            NosPortableMegaphones = [new NosPortableMegaphoneState { PlayerId = 1, ExtraRange = 5d }]
        };
        var mix = SpatialVoicePolicy.Calculate(state, listener, speaker,
            new SpatialVoiceSettings(MaxDistance: 5d));
        check("NoS megaphone: expanded range and edge fade", true,
            mix.Reason == "nos-megaphone" && mix.Gain == 1d);
        var effect = VoiceDisguiseEffectPolicy.Select(state, listener, speaker,
            new LobbySettings(), 100, true, false);
        check("NoS megaphone: charging voice filter", true,
            effect is { Mode: NosSizeEffectMode.SourceFilter, Squash: 0.65d, MegaphoneEnergy: 0.5d });
        state.NosMegaphones[0].VoiceEffect = false;
        check("NoS megaphone: add-on effect OFF is respected", true,
            VoiceDisguiseEffectPolicy.Select(state, listener, speaker,
                new LobbySettings(), 100, true, false) is null);
        state.CommsSabotaged = true;
        check("NoS megaphone: comms sabotage still blocks crew", false,
            SpatialVoicePolicy.Calculate(state, listener, speaker,
                new SpatialVoiceSettings(MaxDistance: 5d, CommsSabotage: true)).Audible);
        state.CommsSabotaged = false;
        state.NosMegaphones.Clear();
        check("NoS portable megaphone: ordinary proximity expands", true,
            SpatialVoicePolicy.Calculate(state, listener, speaker,
                new SpatialVoiceSettings(MaxDistance: 5d)).Audible);
        check("NoS megaphone: finite edge and outside", true,
            NosMegaphoneVoice.Gain(10d, 9d, 0d) == 0.5d &&
            NosMegaphoneVoice.Gain(10d, 11d, 0d) == 0d);
    }

    private static void VerifyFiles(Action<string, bool, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TanukiBCL-Net-voice-role-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var megaphone = Path.Combine(directory, "megaphone.json");
        var portable = Path.Combine(directory, "portable.json");
        var bibiri = Path.Combine(directory, "bibiri.json");
        var descriptor = Path.Combine(directory, "descriptor.json");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            File.WriteAllText(megaphone,
                $"{{\"protocol\":1,\"pid\":123,\"unixMs\":{now},\"players\":[{{\"playerId\":4,\"energy\":0.5,\"range\":12,\"voiceEffect\":false}}]}}");
            File.WriteAllText(portable,
                $"{{\"protocol\":1,\"pid\":123,\"unixMs\":{now},\"players\":[{{\"playerId\":4,\"extraRange\":5}}]}}");
            File.WriteAllText(bibiri,
                $"{{\"protocol\":1,\"pid\":123,\"players\":[{{\"playerId\":4,\"unixMs\":{now}}}]}}");
            File.WriteAllText(descriptor,
                "{\"protocol\":1,\"role\":\"tanukiVoiceRoles\",\"playerId\":4,\"pid\":123,\"port\":12345,\"token\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}");
            check("NoS voice state: megaphone, portable, Bibiri files", true,
                NosVoiceRoleStateReader.ReadMegaphones(123, now, megaphone).Single().Range == 12d &&
                NosVoiceRoleStateReader.ReadPortable(123, now, portable).Single().ExtraRange == 5d &&
                NosVoiceRoleStateReader.ReadBibiri(123, now, bibiri).Single().PlayerId == 4);
            check("NoS voice state: stale and wrong PID rejected", true,
                NosVoiceRoleStateReader.ReadMegaphones(123, now + 501, megaphone).Count == 0 &&
                NosVoiceRoleStateReader.ReadMegaphones(124, now, megaphone).Count == 0);
            check("NoS Bibiri: old death event expires", true,
                NosVoiceRoleStateReader.ReadBibiri(123, now + 3001, bibiri).Count == 0);
            check("NoS voice-role descriptor: token and role validated", true,
                NosVoiceRoleSender.ReadDescriptor(descriptor) is { PlayerId: 4, Port: 12345 });
            var discovered = NosVoiceRoleSender.ReadDescriptor(descriptor);
            check("NoS voice-role discovery: fresh matching player skips file fallback", true,
                NosVoiceRoleSender.IsCurrentDiscovery(discovered, now, now + 999, 123, 4));
            check("NoS voice-role discovery: stale, wrong player or clock rollback uses file fallback", true,
                !NosVoiceRoleSender.IsCurrentDiscovery(discovered, now, now + 1000, 123, 4) &&
                !NosVoiceRoleSender.IsCurrentDiscovery(discovered, now, now + 500, 123, 5) &&
                !NosVoiceRoleSender.IsCurrentDiscovery(discovered, now, now + 500, 124, 4) &&
                !NosVoiceRoleSender.IsCurrentDiscovery(discovered, now, now - 1, 123, 4));
            using var cachedSender = new NosVoiceRoleSender(descriptor);
            var originalJson = File.ReadAllText(descriptor);
            var originalWriteTime = File.GetLastWriteTimeUtc(descriptor);
            var firstRead = cachedSender.ReadDescriptorCached(now);
            check("NoS voice-role descriptor: unchanged file reuses parsed data", true,
                firstRead is not null && ReferenceEquals(firstRead,
                    cachedSender.ReadDescriptorCached(now + 250)));
            File.WriteAllText(descriptor, originalJson.Replace(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "invalid", StringComparison.Ordinal));
            var changedWriteTime = originalWriteTime.AddSeconds(1);
            File.SetLastWriteTimeUtc(descriptor, changedWriteTime);
            check("NoS voice-role descriptor: invalid token rejected", true,
                NosVoiceRoleSender.ReadDescriptor(descriptor) is null &&
                cachedSender.ReadDescriptorCached(now + 500) is null);
            File.WriteAllText(descriptor, originalJson);
            File.SetLastWriteTimeUtc(descriptor, changedWriteTime);
            check("NoS voice-role descriptor: unchanged timestamp forces periodic refresh", true,
                cachedSender.ReadDescriptorCached(now + 750) is null &&
                cachedSender.ReadDescriptorCached(now + 5500) is { PlayerId: 4 });
            File.Delete(descriptor);
            check("NoS voice-role descriptor: deletion clears fallback", true,
                cachedSender.ReadDescriptorCached(now + 5750) is null);
        }
        finally
        {
            foreach (var file in new[] { megaphone, portable, bibiri, descriptor }) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static void VerifyHostAdmission(Action<string, bool, bool> check)
    {
        var state = new AmongUsState
        {
            IsHost = true, Mod = AmongUsModType.NebulaOnTheShip,
            GameState = GameState.Tasks, LobbyCode = "ABCDEF",
            Players = [new Player { Id = 4, ClientId = 7 }]
        };
        using var client = JsonDocument.Parse("{\"playerId\":4,\"clientId\":7}");
        using var data = JsonDocument.Parse("{\"lobbyCode\":\"ABCDEF\",\"playerId\":4,\"clientId\":7," +
            "\"rms\":0.5,\"active\":true,\"session\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"sequence\":1}");
        var admission = new NosHostVoiceAdmission();
        check("NoS mobile host relay: authenticated sample accepted", true,
            admission.TryAccept("peer-a", client.RootElement, data.RootElement, state, 7,
                out var sample, 1000) && sample is { PlayerId: 4, Rms: 0.5d, Active: true });
        check("NoS mobile host relay: replay and false sender rejected", true,
            !admission.TryAccept("peer-a", client.RootElement, data.RootElement, state, 7,
                out _, 1200) &&
            !admission.TryAccept("peer-b", client.RootElement, data.RootElement, state, 8,
                out _, 1200));
        state.Players = null!;
        check("NoS mobile host relay: missing shutdown roster rejected", true,
            !admission.TryAccept("peer-c", client.RootElement, data.RootElement, state, 7,
                out var missingSample, 1400) && missingSample is null);
    }

    private static void VerifyDeathEcho(Action<string, bool, bool> check)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var generator = new SignalGenerator(48_000, 2)
        {
            Type = SignalGeneratorType.Sin, Frequency = 440d, Gain = 0.4d
        };
        var volume = new VolumeSampleProvider(generator);
        var echo = new NosDeathEchoSampleProvider(volume);
        var buffer = new float[48_000];
        echo.Update(true, false, null, now);
        echo.Read(buffer, 0, buffer.Length);
        volume.Volume = 0f;
        echo.Update(true, true, now, now);
        Array.Clear(buffer);
        echo.Read(buffer, 0, buffer.Length);
        check("NoS Bibiri: recent death retains a live decaying tail", true,
            buffer.Any(value => Math.Abs(value) > 0.001f));
        echo.Stop();
        Array.Clear(buffer);
        echo.Read(buffer, 0, buffer.Length);
        check("NoS Bibiri: mute stops the tail", true,
            buffer.All(value => Math.Abs(value) < 0.0001f));
    }

    private static void VerifyMegaphoneRecording(Action<string, bool, bool> check)
    {
        var recording = new NosMegaphoneRecording();
        var state = new NosMegaphoneState
        {
            PlayerId = 1, Energy = 0.5d, Range = 10d,
            ShotId = 12, Phase = "charging", VoiceEffect = false
        };
        recording.Update(state, true, 1d);
        var input = new short[960 * 2];
        Array.Fill(input, (short)16384);
        recording.RecordStereoPcm16(input);
        check("NoS megaphone: decoded charge is buffered in memory", true,
            recording.RecordedSamples == 960);
        state.Phase = "firing";
        recording.Update(state, true, 1d);
        var output = new float[960 * 2];
        recording.Read(output, 0, output.Length);
        check("NoS megaphone: firing plays dry audio at full distance", true,
            Math.Abs(output[0] - 0.35f) < 0.001f &&
            Math.Abs(output[1] - 0.35f) < 0.001f);
        recording.Update(state, true, 1d);
        Array.Clear(output);
        recording.Read(output, 0, output.Length);
        check("NoS megaphone: one shot replays only once", true,
            output.All(value => Math.Abs(value) < 0.0001f));

        state.ShotId = 13;
        state.Phase = "charging";
        recording.Update(state, true, 1d);
        recording.RecordStereoPcm16(input);
        state.Phase = "firing";
        recording.Update(state, true, 0d);
        Array.Clear(output);
        recording.Read(output, 0, output.Length);
        recording.Update(state, true, 1d);
        Array.Clear(output);
        recording.Read(output, 0, output.Length);
        check("NoS megaphone: muted replay still advances", true,
            output.All(value => Math.Abs(value) < 0.0001f));

        state.ShotId = 14;
        state.Phase = "charging";
        recording.Update(state, true, 1d);
        recording.RecordStereoPcm16(input);
        recording.Update(state, false, 1d);
        check("NoS megaphone: meeting or disconnection cancels charge", true,
            recording.RecordedSamples == 0 && !recording.IsPlaying);
    }

    private static void VerifyMegaphoneBuffering(Action<string, bool, bool> check)
    {
        var recording = new NosMegaphoneRecording();
        var state = new NosMegaphoneState
        {
            PlayerId = 1, ShotId = 15, Phase = "charging", VoiceEffect = false
        };
        recording.Update(state, true, 1d);
        check("NoS megaphone: charging does not preallocate ten seconds", true,
            recording.BufferedCapacitySamples == 0);

        var frame = new short[960 * 2];
        Array.Fill(frame, (short)16384);
        for (var index = 0; index < 10; index++) recording.RecordStereoPcm16(frame);
        check("NoS megaphone: short charge keeps only one chunk", true,
            recording.RecordedSamples == 9_600 && recording.BufferedCapacitySamples == 9_600);
        Array.Fill(frame, (short)-16384);
        recording.RecordStereoPcm16(frame);
        check("NoS megaphone: next chunk is allocated only when needed", true,
            recording.BufferedCapacitySamples == 19_200);

        state.Phase = "firing";
        recording.Update(state, true, 1d);
        var output = new float[960 * 2];
        for (var index = 0; index < 10; index++) recording.Read(output, 0, output.Length);
        recording.Read(output, 0, output.Length);
        check("NoS megaphone: playback crosses chunk boundary without changing audio", true,
            Math.Abs(output[0] + 0.35f) < 0.001f && recording.BufferedCapacitySamples == 0);

        state.ShotId = 16;
        state.Phase = "charging";
        recording.Update(state, true, 1d);
        var monoFrame = new byte[960 * sizeof(short)];
        for (var index = 1; index < monoFrame.Length; index += 2) monoFrame[index] = 0x40;
        for (var index = 0; index < 501; index++) recording.RecordMonoPcm16(monoFrame);
        check("NoS megaphone: chunked recording retains the ten-second cap", true,
            recording.RecordedSamples == 480_000 && recording.BufferedCapacitySamples == 480_000);
        state.Phase = "firing";
        recording.Update(state, true, 1d);
        for (var index = 0; index < 500; index++) recording.Read(output, 0, output.Length);
        check("NoS megaphone: final chunk replays and releases at ten seconds", true,
            Math.Abs(output[0] - 0.35f) < 0.001f && recording.BufferedCapacitySamples == 0);
        recording.Cancel();
        check("NoS megaphone: cancel releases all recording chunks", true,
            recording.BufferedCapacitySamples == 0);
    }

    private static void VerifyLoopback(Action<string, bool, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TanukiBCL-Net-udp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var voicePath = Path.Combine(directory, "voice-role.json");
        var hostPath = Path.Combine(directory, "host-voice.json");
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Client.ReceiveTimeout = 2000;
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var state = new AmongUsState
        {
            IsHost = true, Mod = AmongUsModType.NebulaOnTheShip,
            GameState = GameState.Tasks, Players = [new Player { Id = 4, ClientId = 7, IsLocal = true }]
        };
        try
        {
            File.WriteAllText(voicePath, "{\"protocol\":1,\"role\":\"tanukiVoiceRoles\"," +
                $"\"playerId\":4,\"pid\":123,\"port\":{port},\"token\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}");
            File.WriteAllText(hostPath, "{\"protocol\":1," +
                $"\"pid\":123,\"port\":{port},\"token\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}");
            using var sender = new NosVoiceRoleSender(voicePath);
            sender.Send(0.25d, true, 123, state);
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var local = Encoding.UTF8.GetString(receiver.Receive(ref endpoint));
            check("NoS voice role: local UDP carries authenticated RMS", true,
                endpoint.Address.Equals(IPAddress.Loopback) &&
                local.StartsWith("TBCL1|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|", StringComparison.Ordinal) &&
                local.EndsWith("|0.25|1", StringComparison.Ordinal));
            var missingPlayers = new AmongUsState
            {
                IsHost = true, Mod = AmongUsModType.NebulaOnTheShip,
                GameState = GameState.Tasks, Players = null!
            };
            sender.Send(0.25d, true, 123, missingPlayers);
            check("NoS voice role: missing shutdown roster sends no UDP", true,
                receiver.Available == 0);
            using var relay = new NosHostVoiceRelay(_ => hostPath);
            relay.Send(new NosHostVoiceSample(4, 0.5d, true), 123, state);
            var host = Encoding.UTF8.GetString(receiver.Receive(ref endpoint));
            check("NoS mobile host relay: authenticated loopback UDP", true,
                host.StartsWith("TBCLH1|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|4|", StringComparison.Ordinal) &&
                host.EndsWith("|0.5|1", StringComparison.Ordinal));
            relay.Send(new NosHostVoiceSample(4, 0.5d, true), 123, missingPlayers);
            check("NoS mobile host relay: missing shutdown roster sends no UDP", true,
                receiver.Available == 0);
        }
        finally
        {
            File.Delete(voicePath);
            File.Delete(hostPath);
            Directory.Delete(directory);
        }
    }
}
