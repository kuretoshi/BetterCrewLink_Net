using System.Text.Json;
using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class SpatialVoicePolicySelfTest
{
    public static int Run()
    {
        var failures = 0;
        foreach (var gameState in new[] { GameState.Tasks, GameState.Discussion })
        {
            var state = new AmongUsState { GameState = gameState };
            var settings = new SpatialVoiceSettings(ImpostorRadioEnabled: true);
            var sender = new Player { IsImpostor = true, X = 20 };

            Check($"{gameState}: impostor receives radio", true,
                SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true }, sender, settings, true).Audible);
            var radioMix = SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true }, sender, settings, true);
            Check($"{gameState}: radio high-pass", true, radioMix.RadioHighPass);
            Check($"{gameState}: radio echo", true, radioMix.RadioEcho);
            Check($"{gameState}: crewmate cannot receive radio", false,
                SpatialVoicePolicy.Calculate(state, new Player(), sender, settings, true).Audible);
            Check($"{gameState}: ghost receives radio", true,
                SpatialVoicePolicy.Calculate(state, new Player { IsDead = true }, sender, settings, true).Audible);
            Check($"{gameState}: radio stops when disabled", false,
                SpatialVoicePolicy.Calculate(state, new Player(), sender,
                    settings with { ImpostorRadioEnabled = false }, true).Audible);
            Check($"{gameState}: living cannot hear dead radio sender", false,
                SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true },
                    sender.WithDead(), settings, true).Audible);
        }

        var discussion = new AmongUsState { GameState = GameState.Discussion };
        Check("meeting: normal living conversation remains audible", true,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player(), new SpatialVoiceSettings()).Audible);
        Check("meeting: normal ghost voice remains private", false,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player { IsDead = true },
                new SpatialVoiceSettings()).Audible);

        var nosTasks = new AmongUsState
        {
            Mod = AmongUsModType.NebulaOnTheShip,
            GameState = GameState.Tasks,
            NosLocalMicPosition = new VoicePosition(20d, 0d)
        };
        var nosSpeaker = new Player
        {
            X = 50d,
            NosPlayer = new NosPlayerData { SpeakerPositionX = 21d, SpeakerPositionY = 0d }
        };
        var nosPolicy = new SpatialVoiceSettings(NosVoicePositions: true);
        Check("NoS: published microphone and speaker positions determine proximity", true,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), nosSpeaker, nosPolicy).Audible);
        Check("NoS: disabled position option uses vanilla coordinates", false,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), nosSpeaker,
                nosPolicy with { NosVoicePositions = false }).Audible);
        Check("NoS: published position places speaker out of range", false,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(),
                new Player { X = 1d, NosPlayer = new NosPlayerData { SpeakerPositionX = 30d } },
                nosPolicy).Audible);
        Check("NoS: non-NoS game ignores published positions", false,
            SpatialVoicePolicy.Calculate(new AmongUsState { GameState = GameState.Tasks },
                new Player(), nosSpeaker, nosPolicy).Audible);
        var jammedSpeaker = new Player { NosPlayer = new NosPlayerData { IsJammed = true } };
        var jammedListener = new Player { NosPlayer = new NosPlayerData { IsJammed = true } };
        Check("NoS: jammed speaker is silent in meeting", false,
            SpatialVoicePolicy.Calculate(new AmongUsState
                { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Discussion },
                new Player(), jammedSpeaker, nosPolicy).Audible);
        Check("NoS: jammed listener hears nothing in lobby", false,
            SpatialVoicePolicy.Calculate(new AmongUsState
                { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Lobby },
                jammedListener, new Player(), nosPolicy).Audible);
        Check("NoS: disabled fixer block restores speech", true,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), jammedSpeaker,
                nosPolicy with { NosVoicePositions = false, NosFixerJammingVoiceBlock = false }).Audible);
        Check("NoS: non-NoS game ignores fixer flags", true,
            SpatialVoicePolicy.Calculate(new AmongUsState { GameState = GameState.Tasks },
                new Player(), jammedSpeaker, nosPolicy).Audible);
        var nosRadioPolicy = new SpatialVoiceSettings(JackalRadioEnabled: true);
        var distantNosSender = new Player { X = 20d };
        Check("NoS radio: masked recipient hears distant sender", true,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), distantNosSender,
                nosRadioPolicy, true, true).Audible);
        Check("NoS radio: unmasked recipient cannot hear sender", false,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), distantNosSender,
                nosRadioPolicy, true, false).Audible);
        Check("NoS radio: meeting recipient hears sender", true,
            SpatialVoicePolicy.Calculate(new AmongUsState
                { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Discussion },
                new Player(), distantNosSender, nosRadioPolicy, true, true).Audible);
        Check("NoS radio: ghost in mask hears sender", true,
            SpatialVoicePolicy.Calculate(nosTasks, new Player { IsDead = true }, distantNosSender,
                nosRadioPolicy, true, true).Audible);
        Check("NoS radio: disabled Jackal option blocks channel", false,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), distantNosSender,
                nosRadioPolicy with { JackalRadioEnabled = false }, true, true).Audible);
        Check("NoS radio: impostor-radio-only blocks Jackal channel", false,
            SpatialVoicePolicy.Calculate(nosTasks, new Player(), distantNosSender,
                nosRadioPolicy with { ImpostorRadioOnlyMode = true }, true, true).Audible);
        Check("NoS radio: non-NoS game ignores recipient mask", false,
            SpatialVoicePolicy.Calculate(new AmongUsState { GameState = GameState.Tasks },
                new Player(), distantNosSender, nosRadioPolicy, true, true).Audible);
        var nosJackalChannels = new List<NosRadioData>
        {
            new(0, -1, "impostor"),
            new(1, unchecked((int)0x80000005), "jackal")
        };
        Check("NoS radio mask: kind 1 channel is present", true,
            NosRadioRules.HasJackalChannel(nosJackalChannels));
        Check("NoS radio mask: player 0 included", true,
            NosRadioRules.CanHearJackalChannel(nosJackalChannels, 0));
        Check("NoS radio mask: player 1 excluded", false,
            NosRadioRules.CanHearJackalChannel(nosJackalChannels, 1));
        Check("NoS radio mask: signed high bit includes player 31", true,
            NosRadioRules.CanHearJackalChannel(nosJackalChannels, 31));
        Check("NoS radio mask: player 32 invalid", false,
            NosRadioRules.CanHearJackalChannel(nosJackalChannels, 32));
        Check("NoS radio mask: impostor channel cannot impersonate Jackal", false,
            NosRadioRules.CanHearJackalChannel([new NosRadioData(0, -1, "impostor")], 1));

        var sizedSpeaker = new Player
        {
            NosPlayer = new NosPlayerData { BodyRateX = 0.8d, BodyRateY = 0.5d }
        };
        var sizeEffect = NosSizeVoiceEffectPolicy.Select(nosTasks, sizedSpeaker,
            new LobbySettings(), audible: true);
        Check("NoS size: small body selects direct pitch up", true,
            sizeEffect?.Mode == NosSizeEffectMode.PitchUp &&
            Math.Abs(sizeEffect.Strength - Math.Log(2d) / Math.Log(10d)) < 0.0001d);
        sizedSpeaker.NosPlayer!.BodyRateX = 1d;
        Check("NoS size: narrow body selects squash", true,
            NosSizeVoiceEffectPolicy.Select(nosTasks, sizedSpeaker, new LobbySettings(), true)?.Mode ==
            NosSizeEffectMode.Squash);
        sizedSpeaker.NosPlayer.BodyRateY = 2d;
        Check("NoS size: large body selects jumbo", true,
            NosSizeVoiceEffectPolicy.Select(nosTasks, sizedSpeaker, new LobbySettings(), true)?.Mode ==
            NosSizeEffectMode.Jumbo);
        sizedSpeaker.NosPlayer.BodyRateX = 0.7d;
        sizedSpeaker.NosPlayer.BodyRateY = 1d;
        Check("NoS size: neutral height retains tone-rate shelf", true,
            NosSizeVoiceEffectPolicy.Select(nosTasks, sizedSpeaker, new LobbySettings(), true)?.Mode ==
            NosSizeEffectMode.ToneOnly);
        Check("NoS size: lobby switch disables effect", true,
            NosSizeVoiceEffectPolicy.Select(nosTasks, sizedSpeaker,
                new LobbySettings { NosSizeVoiceEffect = false }, true) is null);
        Check("NoS size: meeting disables effect", true,
            NosSizeVoiceEffectPolicy.Select(new AmongUsState
                { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Discussion },
                sizedSpeaker, new LobbySettings(), true) is null);
        Check("NoS size: dead sender disables effect", true,
            NosSizeVoiceEffectPolicy.Select(nosTasks,
                new Player { IsDead = true, NosPlayer = sizedSpeaker.NosPlayer }, new LobbySettings(), true) is null);
        Check("NoS size: muted sender disables effect", true,
            NosSizeVoiceEffectPolicy.Select(nosTasks, sizedSpeaker, new LobbySettings(), false) is null);
        var pitchUpAudio = CreateSizeEffectAudio(new NosSizeVoiceEffect(NosSizeEffectMode.PitchUp, 0.3d, 1d));
        var pitchDownAudio = CreateSizeEffectAudio(new NosSizeVoiceEffect(NosSizeEffectMode.Jumbo, 0.4d, 1d));
        Check("NoS DSP: direct pitch raises 440 Hz tone", true,
            ToneAmplitude(pitchUpAudio, 572d) > ToneAmplitude(pitchUpAudio, 440d) * 1.5d);
        Check("NoS DSP: jumbo lowers 440 Hz tone", true,
            ToneAmplitude(pitchDownAudio, 334.4d) > ToneAmplitude(pitchDownAudio, 440d) * 1.5d);
        var squashAudio = CreateSizeEffectAudio(new NosSizeVoiceEffect(NosSizeEffectMode.Squash,
            0.5d, 1d, 0.5d));
        Check("NoS DSP: squash attenuates voice", true,
            Rms(squashAudio) < 0.15d);
        Check("dummy speaker: never audible", false,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player { IsDummy = true },
                new SpatialVoiceSettings()).Audible);

        var radioOnlyPolicy = new SpatialVoiceSettings(ImpostorRadioEnabled: true,
            ImpostorRadioOnlyMode: true, MeetingGhostOnly: true);
        var tasks = new AmongUsState { GameState = GameState.Tasks };
        Check("radio-only: proximity silenced", false,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsImpostor = true }, radioOnlyPolicy).Audible);
        Check("radio-only: impostor radio audible", true,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsImpostor = true }, radioOnlyPolicy, true).Audible);
        Check("radio-only: crew cannot hear radio", false,
            SpatialVoicePolicy.Calculate(tasks, new Player(),
                new Player { IsImpostor = true }, radioOnlyPolicy, true).Audible);

        var commsTasks = new AmongUsState { GameState = GameState.Tasks, CommsSabotaged = true };
        var commsPolicy = new SpatialVoiceSettings(CommsSabotage: true, ImpostorRadioEnabled: true);
        Check("comms: living crew cannot hear proximity", false,
            SpatialVoicePolicy.Calculate(commsTasks, new Player(), new Player(), commsPolicy).Audible);
        Check("comms: impostor still hears proximity", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player { IsImpostor = true },
                new Player(), commsPolicy).Audible);
        Check("comms: ghost still hears proximity", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player { IsDead = true },
                new Player(), commsPolicy).Audible);
        Check("comms: radio overrides block for eligible receiver", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player { IsImpostor = true },
                new Player { IsImpostor = true }, commsPolicy, true).Audible);
        Check("comms: disabled option leaves crew audible", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player(), new Player(),
                commsPolicy with { CommsSabotage = false }).Audible);

        var ghostListener = new Player { IsDead = true };
        var livingSpeaker = new Player();
        var ghostVolumePolicy = new SpatialVoiceSettings(CrewVolumeAsGhost: 0.35d,
            GhostVolumeAsImpostor: 0.1d, Haunting: true, ImpostorRadioEnabled: true);
        CheckGain("ghost volume: tasks proximity", 0.35d,
            SpatialVoicePolicy.Calculate(tasks, ghostListener, livingSpeaker, ghostVolumePolicy).Gain);
        CheckGain("ghost volume: meeting", 0.35d,
            SpatialVoicePolicy.Calculate(discussion, ghostListener, livingSpeaker, ghostVolumePolicy).Gain);
        CheckGain("ghost volume: radio", 0.35d,
            SpatialVoicePolicy.Calculate(tasks, ghostListener,
                new Player { IsImpostor = true, X = 20 }, ghostVolumePolicy, true).Gain);
        CheckGain("impostor hearing ghost: tasks", 0.1d,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsDead = true }, ghostVolumePolicy).Gain);

        var configuredPlayer = new Player { PlayerConfigId = 123, NameHash = 456 };
        var playerConfigs = new Dictionary<int, PlayerAudioConfig>
        {
            [123] = new(0.4d),
            [456] = new(0.7d)
        };
        var normalMix = new PeerVoiceMix(0.8d, 0d, 0d, "proximity");
        CheckGain("player config: uid takes precedence over name", 0.32d,
            PlayerAudioConfig.For(configuredPlayer, playerConfigs).Apply(normalMix).Gain);
        playerConfigs.Remove(123);
        CheckGain("player config: name-hash fallback", 0.56d,
            PlayerAudioConfig.For(configuredPlayer, playerConfigs).Apply(normalMix).Gain);
        CheckGain("player config: default volume", 0.8d,
            PlayerAudioConfig.For(configuredPlayer, new Dictionary<int, PlayerAudioConfig>()).Apply(normalMix).Gain);
        CheckGain("player config: muted", 0d,
            new PlayerAudioConfig(2d, true).Apply(normalMix).Gain);
        CheckGain("player config: zero volume", 0d,
            new PlayerAudioConfig(0d).Apply(normalMix).Gain);
        CheckGain("player config: 200 percent", 1.6d,
            new PlayerAudioConfig(2d).Apply(normalMix).Gain);
        CheckGain("player config: preserve stored fractional volume", 0.117d,
            new PlayerAudioConfig(0.117d).Normalize().Volume);
        CheckGain("player config: clamp invalid volume", 2d,
            new PlayerAudioConfig(5d).Normalize().Volume);

        Check("quality: unmeasured", true, new ConnectionQuality().Bars == 0);
        Check("quality: good server ping", true, new ConnectionQuality(ServerPingMs: 25d).Bars == 3);
        Check("quality: moderate server ping", true, new ConnectionQuality(ServerPingMs: 180d).Bars == 2);
        Check("quality: unstable server ping", true, new ConnectionQuality(ServerPingMs: 320d).Bars == 1);
        Check("quality: jitter can lower bars", true,
            new ConnectionQuality(JitterMs: 35d, ServerPingMs: 25d).Bars == 2);
        Check("quality: server ping takes precedence over peer RTT", true,
            new ConnectionQuality(RttMs: 400d, ServerPingMs: 25d).Bars == 3);

        Check("mod: Nebula plugin", true,
            AmongUsModDetector.Detect(@"C:\Games\Among Us\Among Us.exe", [], ["NebulaLoader.dll"])
                .Id == AmongUsModType.NebulaOnTheShip);
        Check("mod: loaded SNR overrides plugin", true,
            AmongUsModDetector.Detect(@"C:\Games\Among Us\Among Us.exe", ["SuperNewRoles.dll"],
                ["NebulaLoader.dll"]).Id == AmongUsModType.SuperNewRoles);
        Check("mod: TOH4E path", true,
            AmongUsModDetector.Detect(@"C:\Games\TOH4E_EM\Among Us.exe", [], ["NebulaLoader.dll"])
                .Id == AmongUsModType.TownOfHostForE);
        Check("mod: vanilla", true,
            AmongUsModDetector.Detect(@"C:\Games\Among Us\Among Us.exe", [], [])
                .Id == AmongUsModType.None);

        Check("appearance: shifted color is visible change", true,
            new Player { ColorId = 1, CurrentOutfit = 1, AppearanceColorId = 2 }.HasVisibleAppearanceChanged());
        Check("appearance: empty cosmetic aliases do not change look", true,
            !new Player { ColorId = 1, CurrentOutfit = 1, AppearanceColorId = 1,
                HatId = "hat_NoHat", SkinId = "skin_None", VisorId = "visor_EmptyVisor" }
                .HasVisibleAppearanceChanged());
        Check("appearance: inactive outfit is not hidden", true,
            !new Player { ColorId = 1, CurrentOutfit = 0, AppearanceColorId = 2 }.HasVisibleAppearanceChanged());

        var ventPolicy = new SpatialVoiceSettings(HearImpostorsInVents: true,
            ImpostorsHearImpostorsInVents: true, ImpostorRadioEnabled: true);
        var ventSpeaker = new Player { IsImpostor = true, InVent = true };
        var impostorListener = new Player { IsImpostor = true };
        var ventMix = SpatialVoicePolicy.Calculate(tasks, impostorListener, ventSpeaker, ventPolicy);
        Check("vent: proximity voice is muffled", true, ventMix.Muffled);
        CheckGain("vent: unmodified gain is halved", 0.5d, ventMix.Gain);
        Check("vent: meeting voice is not muffled", false,
            SpatialVoicePolicy.Calculate(discussion, impostorListener, ventSpeaker, ventPolicy).Muffled);
        var ventRadioMix = SpatialVoicePolicy.Calculate(tasks, impostorListener, ventSpeaker, ventPolicy, true);
        Check("vent: radio high-pass replaced by low-pass", true,
            ventRadioMix.Muffled && !ventRadioMix.RadioHighPass && ventRadioMix.RadioEcho);
        CheckGain("vent: radio gain is halved", 0.5d, ventRadioMix.Gain);
        var lowTone = MeasureFilteredRms(500d);
        var highTone = MeasureFilteredRms(8_000d);
        Check("vent DSP: low frequency remains audible", true, lowTone > 0.1d);
        Check("vent DSP: high frequency attenuated", true, highTone < lowTone * 0.25d);
        var radioLowTone = MeasureRadioFilteredRms(200d);
        var radioHighTone = MeasureRadioFilteredRms(4_000d);
        Check("radio DSP: low frequency attenuated", true, radioLowTone < radioHighTone * 0.25d);
        Check("radio DSP: high frequency remains audible", true, radioHighTone > 0.1d);
        CheckGain("Web Audio Q: vent 20 dB", 10d, WebAudioBiquadQ.ToLinear(20f));
        CheckGain("Web Audio Q: radio 10 dB", Math.Sqrt(10d), WebAudioBiquadQ.ToLinear(10f));
        CheckGain("Web Audio Q: camera -15 dB", Math.Pow(10d, -0.75d), WebAudioBiquadQ.ToLinear(-15f));
        var echo = new RadioEchoSampleProvider(new TestImpulseStereoSource()) { Enabled = true };
        var echoSamples = new float[17_282];
        echo.Read(echoSamples, 0, echoSamples.Length);
        CheckGain("radio echo DSP: dry signal", 0.92d, echoSamples[0]);
        CheckGain("radio echo DSP: first 90 ms reflection", 0.2d, echoSamples[8_640]);
        CheckGain("radio echo DSP: second reflection", 0.024d, echoSamples[17_280]);
        CheckGain("radio echo DSP: channels remain separate", 0d, echoSamples[8_641]);

        var cameraPolicy = new SpatialVoiceSettings(HearThroughCameras: true);
        var airshipCamera = new AmongUsState
        {
            GameState = GameState.Tasks, Map = MapType.Airship, CurrentCamera = CameraLocation.East
        };
        var cameraSpeaker = new Player { X = -8.2872d, Y = 0.0527d };
        var cameraMix = SpatialVoicePolicy.Calculate(airshipCamera, new Player(), cameraSpeaker, cameraPolicy);
        Check("camera: distant speaker at selected camera is audible", true, cameraMix.Audible);
        Check("camera: 2.3 kHz muffle selected", true, cameraMix.CameraMuffled && !cameraMix.Muffled);
        CheckGain("camera: unmodified gain becomes 0.8", 0.8d, cameraMix.Gain);
        Check("camera: disabled lobby option keeps speaker out of range", false,
            SpatialVoicePolicy.Calculate(airshipCamera, new Player(), cameraSpeaker,
                cameraPolicy with { HearThroughCameras = false }).Audible);
        Check("camera: nearby speaker uses proximity audio", false,
            SpatialVoicePolicy.Calculate(airshipCamera, new Player(), new Player { X = 1d },
                cameraPolicy).CameraMuffled);
        Check("camera: invalid map does not create a phantom camera", false,
            SpatialVoicePolicy.Calculate(new AmongUsState
                { GameState = GameState.Tasks, Map = MapType.Fungle, CurrentCamera = CameraLocation.East }, new Player(),
                cameraSpeaker, cameraPolicy).Audible);
        var skeldCamera = new AmongUsState
        {
            GameState = GameState.Tasks, Map = MapType.TheSkeld, CurrentCamera = CameraLocation.Skeld
        };
        Check("camera: Skeld surveillance uses closest camera", true,
            SpatialVoicePolicy.Calculate(skeldCamera, new Player(),
                new Player { X = 13.2417d, Y = -4.348d }, cameraPolicy).CameraMuffled);
        var cameraLowTone = MeasureCameraFilteredRms(500d);
        var cameraHighTone = MeasureCameraFilteredRms(8_000d);
        Check("camera DSP: high frequency attenuated", true, cameraHighTone < cameraLowTone * 0.25d);

        var fungleListener = new Player { X = 12d, Y = -15d };
        var fungleSpeaker = new Player { X = 15d, Y = -15d };
        var fungleTasks = new AmongUsState { GameState = GameState.Tasks, Map = MapType.Fungle };
        Check("wall map: Fungle path blocks line of sight", true,
            WallCollision.Intersects(fungleListener, fungleSpeaker, MapType.Fungle, []));
        Check("wall policy: living listener blocked", false,
            SpatialVoicePolicy.Calculate(fungleTasks, fungleListener, fungleSpeaker,
                new SpatialVoiceSettings(WallsBlockAudio: true)).Audible);
        Check("wall policy: disabled option preserves proximity", true,
            SpatialVoicePolicy.Calculate(fungleTasks, fungleListener, fungleSpeaker,
                new SpatialVoiceSettings()).Audible);
        Check("wall policy: dead listener ignores walls", true,
            SpatialVoicePolicy.Calculate(fungleTasks, new Player { X = 12d, Y = -15d, IsDead = true },
                fungleSpeaker, new SpatialVoiceSettings(WallsBlockAudio: true)).Audible);
        Check("wall policy: meetings ignore walls", true,
            SpatialVoicePolicy.Calculate(new AmongUsState { GameState = GameState.Discussion, Map = MapType.Fungle },
                fungleListener, fungleSpeaker, new SpatialVoiceSettings(WallsBlockAudio: true)).Audible);
        Check("wall policy: radio overrides walls", true,
            SpatialVoicePolicy.Calculate(fungleTasks,
                new Player { X = 12d, Y = -15d, IsImpostor = true },
                new Player { X = 15d, Y = -15d, IsImpostor = true },
                new SpatialVoiceSettings(WallsBlockAudio: true, ImpostorRadioEnabled: true), true).Audible);
        var skeldDoorLeft = new Player { X = 4.5d, Y = 1.5d };
        var skeldDoorRight = new Player { X = 5.5d, Y = 1.5d };
        Check("closed door: open Skeld gap", false,
            WallCollision.Intersects(skeldDoorLeft, skeldDoorRight, MapType.TheSkeld, []));
        Check("closed door: Skeld door ID 0", true,
            WallCollision.Intersects(skeldDoorLeft, skeldDoorRight, MapType.TheSkeld, [0]));
        Check("closed door: mirrored April Skeld", true,
            WallCollision.Intersects(new Player { X = -4.5d, Y = 1.5d },
                new Player { X = -5.5d, Y = 1.5d }, MapType.TheSkeldApril, [0]));
        var airshipDoorTop = new Player { X = 32.5d, Y = -4.1d };
        var airshipDoorBottom = new Player { X = 32.5d, Y = -4.5d };
        Check("closed door: open Airship gap", false,
            WallCollision.Intersects(airshipDoorTop, airshipDoorBottom, MapType.Airship, []));
        Check("closed door: Airship door ID 20", true,
            WallCollision.Intersects(airshipDoorTop, airshipDoorBottom, MapType.Airship, [20]));

        var lobbySettings = new LobbySettings
        {
            MaxDistance = 7.4d,
            JackalRadioEnabled = true,
            NosSizeVoiceEffect = false,
            NosFixerJammingVoiceBlock = false,
            ImpostorRadioEnabled = true,
            PublicLobbyOn = true,
            PublicLobbyTitle = "互換テスト"
        };
        var wire = lobbySettings.ToWireJson();
        using (var document = JsonDocument.Parse(wire))
        {
            Check("lobby wire: publicLobby_on", true,
                document.RootElement.TryGetProperty("publicLobby_on", out _));
            Check("lobby wire: jackalRadioEnabled", true,
                document.RootElement.TryGetProperty("jackalRadioEnabled", out _));
            Check("lobby wire: nosFixerJammingVoiceBlock", true,
                document.RootElement.TryGetProperty("nosFixerJammingVoiceBlock", out _));
        }
        Check("lobby wire: round trip", true,
            JsonSerializer.Deserialize<LobbySettings>(wire, LobbySettings.WireJsonOptions) == lobbySettings);

        var beforeRadioOnly = lobbySettings with
        {
            ImpostorRadioEnabled = false,
            HearImpostorsInVents = true,
            DeadOnly = true,
            MeetingGhostOnly = false,
            JackalRadioEnabled = true
        };
        var radioOnly = beforeRadioOnly.EnableImpostorRadioOnlyMode();
        Check("radio-only preset: enabled", true,
            radioOnly.ImpostorRadioOnlyMode && radioOnly.ImpostorRadioEnabled &&
            radioOnly.MeetingGhostOnly && !radioOnly.DeadOnly &&
            !radioOnly.HearImpostorsInVents && !radioOnly.JackalRadioEnabled);
        var editedRadioOnly = radioOnly with { Haunting = true };
        Check("radio-only preset: restore forced fields, retain free fields", true,
            editedRadioOnly.DisableImpostorRadioOnlyMode(beforeRadioOnly) ==
            (beforeRadioOnly with { Haunting = true }));

        Console.WriteLine(failures == 0
            ? "[PASS] 3.2.7 radio, NoS size, listener-volume, vent, camera and wall audio policy"
            : $"[FAIL] 3.2.7 radio, NoS size, listener-volume, vent, camera and wall audio policy: {failures} cases failed");
        return failures == 0 ? 0 : 1;

        void Check(string name, bool expected, bool actual)
        {
            if (expected == actual) return;
            failures++;
            Console.Error.WriteLine($"[FAIL] {name}: expected={expected} actual={actual}");
        }

        void CheckGain(string name, double expected, double actual)
        {
            if (Math.Abs(expected - actual) < 0.0001d) return;
            failures++;
            Console.Error.WriteLine($"[FAIL] {name}: expected={expected:0.###} actual={actual:0.###}");
        }
    }

    private static Player WithDead(this Player source) => new()
    {
        IsImpostor = source.IsImpostor,
        IsDead = true,
        X = source.X,
        Y = source.Y
    };

    private static double MeasureFilteredRms(double frequency)
    {
        var filter = new VentMuffleSampleProvider(new TestSineSource(frequency)) { Enabled = true };
        var samples = new float[4_800];
        filter.Read(samples, 0, samples.Length);
        var steadySamples = samples.AsSpan(2_400);
        var energy = 0d;
        foreach (var sample in steadySamples) energy += sample * sample;
        return Math.Sqrt(energy / steadySamples.Length);
    }

    private static float[] CreateSizeEffectAudio(NosSizeVoiceEffect effect)
    {
        var provider = new NosSizeVoiceSampleProvider(new TestSineSource(440d));
        provider.SetEffect(effect);
        var samples = new float[24_000];
        provider.Read(samples, 0, samples.Length);
        return samples[9_600..];
    }

    private static double ToneAmplitude(float[] samples, double frequency)
    {
        var cosine = 0d;
        var sine = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2d * Math.PI * frequency * i / 48_000d;
            cosine += samples[i] * Math.Cos(phase);
            sine += samples[i] * Math.Sin(phase);
        }
        return Math.Sqrt(cosine * cosine + sine * sine) * 2d / samples.Length;
    }

    private static double Rms(float[] samples) =>
        Math.Sqrt(samples.Sum(value => (double)value * value) / samples.Length);

    private static double MeasureRadioFilteredRms(double frequency)
    {
        var filter = new RadioHighPassSampleProvider(new TestSineSource(frequency)) { Enabled = true };
        var samples = new float[4_800];
        filter.Read(samples, 0, samples.Length);
        var steadySamples = samples.AsSpan(2_400);
        var energy = 0d;
        foreach (var sample in steadySamples) energy += sample * sample;
        return Math.Sqrt(energy / steadySamples.Length);
    }

    private static double MeasureCameraFilteredRms(double frequency)
    {
        var filter = new CameraMuffleSampleProvider(new TestSineSource(frequency)) { Enabled = true };
        var samples = new float[4_800];
        filter.Read(samples, 0, samples.Length);
        var steadySamples = samples.AsSpan(2_400);
        var energy = 0d;
        foreach (var sample in steadySamples) energy += sample * sample;
        return Math.Sqrt(energy / steadySamples.Length);
    }

    private sealed class TestImpulseStereoSource : ISampleProvider
    {
        private bool sent;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            if (!sent && count > 0)
            {
                buffer[offset] = 1f;
                sent = true;
            }
            return count;
        }
    }

    private sealed class TestSineSource(double frequency) : ISampleProvider
    {
        private long position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var index = 0; index < count; index++)
            {
                buffer[offset + index] = 0.25f * (float)Math.Sin(2d * Math.PI * frequency * position++ / 48_000d);
            }
            return count;
        }
    }
}
