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
            File.WriteAllText(descriptor, File.ReadAllText(descriptor).Replace(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "invalid", StringComparison.Ordinal));
            check("NoS voice-role descriptor: invalid token rejected", true,
                NosVoiceRoleSender.ReadDescriptor(descriptor) is null);
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
            using var relay = new NosHostVoiceRelay(_ => hostPath);
            relay.Send(new NosHostVoiceSample(4, 0.5d, true), 123, state);
            var host = Encoding.UTF8.GetString(receiver.Receive(ref endpoint));
            check("NoS mobile host relay: authenticated loopback UDP", true,
                host.StartsWith("TBCLH1|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|4|", StringComparison.Ordinal) &&
                host.EndsWith("|0.5|1", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(voicePath);
            File.Delete(hostPath);
            Directory.Delete(directory);
        }
    }
}
