using System.Text.Json;
using NAudio.Wave;
using SIPSorcery.Net;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class SpatialVoicePolicySelfTest
{
    public static int Run()
    {
        PublicLobbyAnnouncement.Verify();
        var failures = 0;
        var duplicateClientState = new AmongUsState { ClientId = 2 };
        Check("duplicate voice sessions on one game client stay muted", true,
            VoiceServerProbe.IsOwnClientPeer(duplicateClientState, 2) &&
            !VoiceServerProbe.IsOwnClientPeer(duplicateClientState, 3));
        Check("ghost reverb: impulse, tail, bypass, and reset", true,
            GhostReverbSelfTest.Verify());
        var overlayState = new AmongUsState { GameState = GameState.Tasks, Players =
        [
            new Player { Id = 0, ClientId = 1, IsLocal = true },
            new Player { Id = 1, ClientId = 2, InVent = true },
            new Player { Id = 2, ClientId = 3, IsDead = true }
        ] };
        var overlayPeers = new Dictionary<int, OverlayPeerStatus>
        {
            [2] = new(true, true, false), [3] = new(true, true, false)
        };
        var overlayPlayers = OverlaySelection.Select(overlayState, overlayPeers, false, false, true);
        Check("overlay: local radio enabled without forcing VAD", true,
            OverlaySelection.Select(overlayState, overlayPeers, false, false, false, true)
                .Single(entry => entry.Player.IsLocal) is { UsingRadio: true, VoiceActive: false });
        Check("overlay: local radio release clears badge", false,
            OverlaySelection.Select(overlayState, overlayPeers, true, false, false, false)
                .Single(entry => entry.Player.IsLocal).UsingRadio);
        Check("overlay: radio alone does not bypass compact VAD filter", false,
            OverlaySelection.Select(overlayState, overlayPeers, false, false, true, true)
                .Any(entry => entry.Player.IsLocal));
        overlayPeers[2] = new(true, true, true);
        Check("overlay: simultaneous radio users retain independent state", true,
            OverlaySelection.Select(overlayState, overlayPeers, true, false, false, true)
                .Count(entry => entry.UsingRadio) == 2);
        overlayPeers[2] = new(true, true, false);
        Check("overlay: compact selects active vent voice without speaking ring", true,
            overlayPlayers.Count == 1 && overlayPlayers[0] is { VoiceActive: true, Talking: false } &&
            overlayPlayers[0].Player.ClientId == 2);
        overlayState.Players[0].IsDead = true;
        Check("overlay: ghosts see other ghost participants", true,
            OverlaySelection.Select(overlayState, overlayPeers, false, false, true)
                .Any(player => player.Player.ClientId == 3));
        overlayState.Players[0].IsDead = false;
        Check("overlay: unrevealed task death remains visible to living players", true,
            OverlaySelection.Select(overlayState, overlayPeers, false, false, false, false,
                new Dictionary<int, bool> { [3] = false }).Any(player => player.Player.ClientId == 3));
        Check("overlay: discussion-revealed death is hidden from living players", false,
            OverlaySelection.Select(overlayState, overlayPeers, false, false, false, false,
                new Dictionary<int, bool> { [3] = true }).Any(player => player.Player.ClientId == 3));
        var radioLocal = new Player { Id = 0, ClientId = 1, IsLocal = true, IsImpostor = true };
        var radioRemote = new Player { Id = 1, ClientId = 2, IsImpostor = true };
        var radioState = new AmongUsState { GameState = GameState.Tasks,
            Players = [radioLocal, radioRemote] };
        var radioSettings = new LobbySettings { ImpostorRadioEnabled = true };
        bool Visible(bool active) => RadioVisibilityPolicy.IsVisible(radioState, radioSettings, 2,
            active, _ => false, (_, _) => false);
        Check("radio badge: visible partner remains marked without audio-mix input", true, Visible(true));
        Check("radio badge: inactive partner is unmarked", false, Visible(false));
        radioLocal.IsImpostor = false;
        Check("radio badge: crewmate cannot see impostor channel", false, Visible(true));
        radioLocal.IsImpostor = true;
        radioRemote.Disconnected = true;
        Check("radio badge: disconnected sender is unmarked", false, Visible(true));
        radioRemote.Disconnected = false;
        radioState.Mod = AmongUsModType.SuperNewRoles;
        radioLocal.IsImpostor = radioRemote.IsImpostor = false;
        radioLocal.SnrRole = new SnrRoleData(7, "Jackal", null, null, null, null);
        radioRemote.SnrRole = new SnrRoleData(9, "Sidekick", null, null, null, null);
        radioSettings = new LobbySettings { JackalRadioEnabled = true };
        Check("radio badge: SNR jackal partners", true, Visible(true));
        radioSettings = radioSettings with { ImpostorRadioOnlyMode = true };
        Check("radio badge: radio-only mode excludes jackal channel", false, Visible(true));
        radioState.Mod = AmongUsModType.NebulaOnTheShip;
        radioLocal.SnrRole = radioRemote.SnrRole = null;
        radioLocal.IsImpostor = true;
        radioSettings = new LobbySettings { JackalRadioEnabled = true, ImpostorRadioEnabled = true };
        Check("radio badge: NoS jackal receiver mask", true,
            RadioVisibilityPolicy.IsVisible(radioState, radioSettings, 2, true,
                player => player.ClientId == 2,
                (sender, listener) => sender.ClientId == 2 && listener.ClientId == 1));
        Check("radio badge: NoS jackal receiver outside mask", false,
            RadioVisibilityPolicy.IsVisible(radioState, radioSettings, 2, true,
                player => player.ClientId == 2, (_, _) => false));
        var obsState = new AmongUsState
        {
            Mod = AmongUsModType.NebulaOnTheShip,
            GameState = GameState.Tasks,
            OldMeetingHud = true,
            PlayerColors = [new PlayerColorPair { Main = 0x00332211, Shadow = 0x00665544 }],
            Players =
            [
                new Player { Id = 0, ClientId = 1, IsLocal = true, ColorId = 0 },
                new Player { Id = 1, ClientId = 2, Name = "Ghost", IsDead = true, ColorId = 0,
                    PetId = 42, Bugged = true,
                    NosPlayer = new NosPlayerData { ColorR = 1, ColorG = 0.5, ColorB = 0 } }
            ]
        };
        var obs = ObsOverlayWire.Build(obsState,
            new Dictionary<int, ObsPeerState> { [2] = new(true, true, true) }, true, true);
        Check("OBS: v3.2.7 state and NoS color", true,
            obs.GetProperty("overlayState").GetProperty("gameState").GetInt32() == (int)GameState.Tasks &&
            obs.GetProperty("mod").GetString() == "NoS" &&
            obs.GetProperty("oldMeetingHud").GetBoolean() &&
            !obs.GetProperty("overlayState").GetProperty("players")[1].TryGetProperty("realColor", out _) &&
            obs.GetProperty("overlayState").GetProperty("players")[1]
                .GetProperty("nosColor").GetString() == "#ff8000");
        Check("OBS: peer activity, death and radio", true,
            obs.GetProperty("otherTalking").GetProperty("2").GetBoolean() &&
            obs.GetProperty("otherDead").GetProperty("2").GetBoolean() &&
            obs.GetProperty("overlayState").GetProperty("players")[1]
                .GetProperty("connected").GetBoolean() &&
            obs.GetProperty("overlayState").GetProperty("players")[1]
                .GetProperty("petId").GetInt32() == 42 &&
            obs.GetProperty("overlayState").GetProperty("players")[1]
                .GetProperty("bugged").GetBoolean() &&
            obs.GetProperty("overlayState").GetProperty("players")[0]
                .GetProperty("usingRadio").GetBoolean());
        obsState.GameState = GameState.Lobby;
        obsState.Players[1].NosLobbyColor = "#20ff00";
        var lobbyObs = ObsOverlayWire.Build(obsState, new Dictionary<int, ObsPeerState>(), false, false);
        Check("OBS: NoS lobby color overrides snapshot", true,
            lobbyObs.GetProperty("overlayState").GetProperty("players")[1].GetProperty("nosColor").GetString() == "#20ff00");
        obsState.Players[1].NosLobbyColor = null;
        obsState.Players[1].NosPlayer = null;
        var missingObs = ObsOverlayWire.Build(obsState, new Dictionary<int, ObsPeerState>(), false, false);
        Check("OBS: missing NoS color is omitted", true,
            !missingObs.GetProperty("overlayState").GetProperty("players")[1].TryGetProperty("nosColor", out _));
        var vanillaObs = ObsOverlayWire.Build(new AmongUsState
        {
            GameState = GameState.Lobby,
            Players = [new Player { ClientId = 1, ColorId = 0 }]
        }, new Dictionary<int, ObsPeerState>(), false, false);
        Check("OBS: vanilla player uses default palette", true,
            vanillaObs.GetProperty("mod").GetString() == "NONE" &&
            !vanillaObs.GetProperty("overlayState").GetProperty("players")[0]
                .TryGetProperty("nosColor", out _) &&
            vanillaObs.GetProperty("overlayState").GetProperty("players")[0]
                .GetProperty("realColor")[0].GetString() == "#C51111");
        var spatialState = new AmongUsState { GameState = GameState.Tasks };
        var spatialMe = new Player();
        var spatialOther = new Player { X = 2d };
        var spatialOn = SpatialVoicePolicy.Calculate(spatialState, spatialMe, spatialOther,
            new SpatialVoiceSettings(MaxDistance: 5d));
        var spatialOff = SpatialVoicePolicy.Calculate(spatialState, spatialMe, spatialOther,
            new SpatialVoiceSettings(MaxDistance: 5d, SpatialAudio: false));
        Check("spatial off: in-range voice is centered without attenuation", true,
            spatialOn.Gain < 1d && spatialOn.Pan > 0d &&
            Math.Abs(spatialOff.Gain - 1d) < 0.0001d && spatialOff.Pan == 0d);
        spatialOther.X = 5d;
        Check("spatial off: boundary remains audible", true,
            SpatialVoicePolicy.Calculate(spatialState, spatialMe, spatialOther,
                new SpatialVoiceSettings(MaxDistance: 5d, SpatialAudio: false)).Audible);
        spatialOther.X = 5.01d;
        Check("spatial off: outside range is still blocked", false,
            SpatialVoicePolicy.Calculate(spatialState, spatialMe, spatialOther,
                new SpatialVoiceSettings(MaxDistance: 5d, SpatialAudio: false)).Audible);
        var lobbyState = new AmongUsState { GameState = GameState.Lobby };
        spatialOther.X = 2d;
        var lobbyNear = SpatialVoicePolicy.Calculate(lobbyState, spatialMe, spatialOther,
            new SpatialVoiceSettings(MaxDistance: 5d));
        Check("lobby: released panner attenuates and positions nearby players", true,
            lobbyNear is { Audible: true, Pan: > 0d, Gain: > 0d and < 1d });
        var lobbyCentered = SpatialVoicePolicy.Calculate(lobbyState, spatialMe, spatialOther,
            new SpatialVoiceSettings(MaxDistance: 5d, SpatialAudio: false));
        Check("lobby: disabling spatial audio centers without attenuation", true,
            lobbyCentered is { Audible: true, Pan: 0d, Gain: 1d });
        spatialOther.X = 6d;
        Check("lobby: out-of-range player stays silent even without spatial audio", false,
            SpatialVoicePolicy.Calculate(lobbyState, spatialMe, spatialOther,
                new SpatialVoiceSettings(MaxDistance: 5d, SpatialAudio: false,
                    HearThroughCameras: true)).Audible);
        Check("lobby: dead-only setting silences living players", false,
            SpatialVoicePolicy.Calculate(lobbyState, spatialMe, new Player(),
                new SpatialVoiceSettings(DeadOnly: true)).Audible);
        var snrReader = SnrLiveRoleReaderSelfTest.Verify();
        Check("SNR live reader: role and modifier names", true, snrReader.Role);
        Check("SNR live reader: Jumbo size", true, snrReader.Jumbo);
        Check("SNR live reader: torn role sample rejected", true, snrReader.TornSampleRejected);
        var tohReader = TohLiveRoleReaderSelfTest.Verify();
        Check("TOH live reader: Opportunist and CanKill", true, tohReader.Role);
        Check("TOH live reader: active IKiller", true, tohReader.Killer);
        Check("TOH live reader: torn role sample rejected", true, tohReader.TornSampleRejected);
        var tohWirePlayer = new Player { Id = 5, ClientId = 3,
            TohRole = new TohRoleData(20, "Jackal", true, true) };
        using (var tohWire = JsonDocument.Parse(TohRoleWire.Serialize("ABCD", tohWirePlayer)))
        {
            Check("TOH wire: absent Opportunist flag stays omitted", true,
                !tohWire.RootElement.GetProperty("role").TryGetProperty("opportunistCanKill", out _));
            Check("TOH wire: role round-trips", true,
                TohRoleWire.TryRead(tohWire.RootElement, out var parsed) && parsed == tohWirePlayer.TohRole);
        }
        tohWirePlayer.TohRole = null;
        using (var tohWire = JsonDocument.Parse(TohRoleWire.Serialize("ABCD", tohWirePlayer)))
            Check("TOH wire: unavailable role is explicit null", true,
                tohWire.RootElement.GetProperty("role").ValueKind == JsonValueKind.Null);
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
        var nosNeutralKiller = new Player { NosPlayer = new NosPlayerData
            { IsNeutral = true, IsKiller = true, IsImpostor = false } };
        Check("NoS haunting: neutral killer hears ghosts when enabled", true,
            SpatialVoicePolicy.Calculate(nosTasks, nosNeutralKiller,
                new Player { IsDead = true },
                new SpatialVoiceSettings(NosNeutralKillerHaunting: true)).Audible);
        Check("NoS haunting: neutral non-killer cannot hear ghosts", false,
            SpatialVoicePolicy.Calculate(nosTasks, new Player { NosPlayer = new NosPlayerData
                { IsNeutral = true, IsKiller = false } }, new Player { IsDead = true },
                new SpatialVoiceSettings(NosNeutralKillerHaunting: true)).Audible);
        var noSRadioOnlyHaunting = new SpatialVoiceSettings(
            NosNeutralKillerHaunting: true, ImpostorRadioOnlyMode: true,
            MeetingGhostOnly: true);
        Check("NoS radio-only: neutral killer still hears ghost", true,
            SpatialVoicePolicy.Calculate(nosTasks, nosNeutralKiller,
                new Player { IsDead = true }, noSRadioOnlyHaunting).Audible);
        Check("NoS radio-only: neutral killer does not hear living proximity", false,
            SpatialVoicePolicy.Calculate(nosTasks, nosNeutralKiller,
                new Player(), noSRadioOnlyHaunting).Audible);
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
        var snrTasks = new AmongUsState { Mod = AmongUsModType.SuperNewRoles,
            GameState = GameState.Tasks };
        var snrJackal = new Player { SnrRole = new SnrRoleData(7, "Jackal", null, null,
            null, null) };
        var snrSidekick = new Player { X = 20d,
            SnrRole = new SnrRoleData(9, "Sidekick", null, null, null, null) };
        Check("SNR radio: Jackal hears distant Sidekick", true,
            SpatialVoicePolicy.Calculate(snrTasks, snrJackal, snrSidekick,
                new SpatialVoiceSettings(JackalRadioEnabled: true), true).Audible);
        Check("SNR radio: crewmate cannot hear Sidekick", false,
            SpatialVoicePolicy.Calculate(snrTasks, new Player(), snrSidekick,
                new SpatialVoiceSettings(JackalRadioEnabled: true), true).Audible);
        Check("SNR radio: ghost cannot hear Jackal channel", false,
            SpatialVoicePolicy.Calculate(snrTasks, new Player { IsDead = true,
                SnrRole = snrJackal.SnrRole }, snrSidekick,
                new SpatialVoiceSettings(JackalRadioEnabled: true), true).Audible);
        Check("SNR vent: Jackal hears Sidekick inside vent when enabled", true,
            SpatialVoicePolicy.Calculate(snrTasks, new Player { InVent = true,
                SnrRole = snrJackal.SnrRole }, new Player { InVent = true,
                SnrRole = snrSidekick.SnrRole },
                new SpatialVoiceSettings(SidekickTalkInVents: true)).Audible);
        Check("SNR vent: Jackal cannot hear Sidekick inside vent when disabled", false,
            SpatialVoicePolicy.Calculate(snrTasks, new Player { InVent = true,
                SnrRole = snrJackal.SnrRole }, new Player { InVent = true,
                SnrRole = snrSidekick.SnrRole }, new SpatialVoiceSettings()).Audible);
        Check("SNR haunting: Jackal hears ghost when enabled", true,
            SpatialVoicePolicy.Calculate(snrTasks, snrJackal, new Player { IsDead = true },
                new SpatialVoiceSettings(JackalHaunting: true)).Audible);
        var tohTasks = new AmongUsState { Mod = AmongUsModType.TownOfHostForE,
            GameState = GameState.Tasks };
        var tohKiller = new Player { TohRole = new TohRoleData(19, "Opportunist", true,
            true, true) };
        Check("TOH haunting: active IKiller hears ghost when enabled", true,
            SpatialVoicePolicy.Calculate(tohTasks, tohKiller, new Player { IsDead = true },
                new SpatialVoiceSettings(TohNeutralKillerHaunting: true)).Audible);
        Check("TOH haunting: role name alone does not grant hearing", false,
            SpatialVoicePolicy.Calculate(tohTasks, new Player { TohRole = tohKiller.TohRole with
                { IsKiller = null } }, new Player { IsDead = true },
                new SpatialVoiceSettings(TohNeutralKillerHaunting: true)).Audible);
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
        var disguisedSpeaker = new Player
        {
            Name = "base", AppearanceName = "disguised", IsImpostor = true
        };
        var disguiseListener = new Player { IsImpostor = true };
        var disguiseTasks = new AmongUsState
        {
            GameState = GameState.Tasks, Players = [disguiseListener, disguisedSpeaker]
        };
        Check("disguise: changed appearance name enables effect", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings(), 100, true, false)?.Mode == NosSizeEffectMode.Disguise);
        Check("disguise: zero local strength disables effect", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings(), 0, true, false) is null);
        Check("disguise: lobby switch disables effect", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings { VoiceEffectEnabled = false }, 100, true, false) is null);
        Check("disguise: impostor radio bypasses effect", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings { ImpostorRadioEnabled = true }, 100, true, true) is null);
        disguiseListener.IsDead = true;
        Check("disguise: ghosts hear unmodified voice", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings(), 100, true, false) is null);
        disguiseListener.IsDead = false;
        disguisedSpeaker.NosPlayer = new NosPlayerData { BodyRateX = 1d, BodyRateY = 0.5d };
        disguiseTasks.Mod = AmongUsModType.NebulaOnTheShip;
        Check("disguise: NoS size takes priority", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings(), 100, true, false)?.Mode == NosSizeEffectMode.Squash);
        disguisedSpeaker.NosPlayer.BodyRateY = 0d;
        disguisedSpeaker.NosPlayer.BodyRateX = 0.8d;
        Check("disguise: zero NoS body height blocks fallback", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings(), 100, true, false) is null);
        disguiseTasks.Map = MapType.Airship;
        disguiseTasks.AirshipMeetingByOutfit = true;
        disguisedSpeaker.NosPlayer.BodyRateY = 0.5d;
        Check("disguise: Airship meeting fallback suppresses all effects", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings(), 100, true, false) is null);
        disguiseTasks.AirshipMeetingByOutfit = false;
        disguiseTasks.Mod = AmongUsModType.SuperNewRoles;
        disguisedSpeaker.SnrRole = new SnrRoleData(7, "Jackal", 4, "JumboModifier",
            null, null, JumboCurrentSize: 2.5d, JumboMaxSize: 5d);
        var snrJumbo = VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener,
            disguisedSpeaker, new LobbySettings { SnrJumboVoice = true,
                VoiceEffectEnabled = false }, 0, true, false);
        Check("SNR Jumbo: live size takes priority over disguise toggle", true,
            snrJumbo is { Mode: NosSizeEffectMode.Jumbo, Strength: 0.5d });
        disguisedSpeaker.SnrRole = disguisedSpeaker.SnrRole with { JumboMaxSize = null };
        Check("SNR Jumbo: missing size blocks disguise fallback", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings { SnrJumboVoice = true }, 100, true, false) is null);
        disguisedSpeaker.SnrRole = disguisedSpeaker.SnrRole with { JumboMaxSize = 5d };
        Check("SNR Jumbo: inaudible speaker gets no effect", true,
            VoiceDisguiseEffectPolicy.Select(disguiseTasks, disguiseListener, disguisedSpeaker,
                new LobbySettings { SnrJumboVoice = true }, 100, false, false) is null);
        var disguiseAudio = CreateSizeEffectAudio(new NosSizeVoiceEffect(NosSizeEffectMode.Disguise, 1d, 1d), 850d);
        Check("disguise DSP: pitch-up path shifts center-band voice", true,
            ToneAmplitude(disguiseAudio, 1700d) > ToneAmplitude(disguiseAudio, 850d));
        Check("dummy speaker: never audible", false,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player { IsDummy = true },
                new SpatialVoiceSettings()).Audible);

        var radioOnlyPolicy = new SpatialVoiceSettings(ImpostorRadioEnabled: true,
            ImpostorRadioOnlyMode: true, MeetingGhostOnly: true);
        var tasks = new AmongUsState { GameState = GameState.Tasks };
        var visionTasks = new AmongUsState { GameState = GameState.Tasks, LightRadius = 2d };
        var visionPolicy = new SpatialVoiceSettings(MaxDistance: 5.32d, VisionHearing: true);
        Check("vision hearing: crew range follows light radius + 0.5", false,
            SpatialVoicePolicy.Calculate(visionTasks, new Player(), new Player { X = 3d },
                visionPolicy).Audible);
        Check("vision hearing: impostor keeps configured range", true,
            SpatialVoicePolicy.Calculate(visionTasks, new Player { IsImpostor = true },
                new Player { X = 3d }, visionPolicy).Audible);
        Check("vision hearing: disabling restores configured range", true,
            SpatialVoicePolicy.Calculate(visionTasks, new Player(), new Player { X = 3d },
                visionPolicy with { VisionHearing = false }).Audible);
        var meetingOutfit = new Player { CurrentOutfit = 1, ColorId = 0, AppearanceColorId = 0 };
        var otherMeetingOutfit = new Player { CurrentOutfit = 1, ColorId = 1, AppearanceColorId = 1,
            X = 30d };
        Check("Airship outfit fallback: two matching active outfits", true,
            AirshipMeetingRules.IsMeetingByOutfit(GameState.Tasks, MapType.Airship,
                [meetingOutfit, otherMeetingOutfit]));
        otherMeetingOutfit.AppearanceColorId = 2;
        Check("Airship outfit fallback: a real disguise excludes fallback", false,
            AirshipMeetingRules.IsMeetingByOutfit(GameState.Tasks, MapType.Airship,
                [meetingOutfit, otherMeetingOutfit]));
        otherMeetingOutfit.AppearanceColorId = 1;
        Check("Airship outfit fallback: invalid third player is ignored", true,
            AirshipMeetingRules.IsMeetingByOutfit(GameState.Tasks, MapType.Airship,
                [meetingOutfit, otherMeetingOutfit, new Player { Bugged = true, CurrentOutfit = 2 }]));
        Check("Airship outfit fallback: invalid player cannot satisfy minimum", false,
            AirshipMeetingRules.IsMeetingByOutfit(GameState.Tasks, MapType.Airship,
                [meetingOutfit, new Player { Bugged = true, CurrentOutfit = 1, AppearanceColorId = 0 }]));
        var airshipOutfitMeeting = new AmongUsState
        {
            GameState = GameState.Tasks, Map = MapType.Airship, AirshipMeetingByOutfit = true
        };
        var airshipFallbackMix = SpatialVoicePolicy.Calculate(airshipOutfitMeeting,
            meetingOutfit, otherMeetingOutfit,
            new SpatialVoiceSettings(MaxDistance: 5d, WallsBlockAudio: true, HearThroughCameras: true));
        Check("Airship outfit fallback: distance and walls bypassed", true,
            airshipFallbackMix.Audible && airshipFallbackMix.Gain == 1d && airshipFallbackMix.Pan == 0d);
        Check("Airship meeting fallback: living player cannot hear a haunting ghost", false,
            SpatialVoicePolicy.Calculate(airshipOutfitMeeting,
                new Player { IsImpostor = true }, new Player { IsDead = true },
                new SpatialVoiceSettings(Haunting: true)).Audible);
        var spawnWindow = new AirshipSpawnFallback();
        var spawnClock = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        spawnWindow.Update(new AmongUsState
            { GameState = GameState.Discussion, Map = MapType.Airship }, spawnClock);
        var airshipAfterMeeting = new AmongUsState
            { GameState = GameState.Tasks, OldGameState = GameState.Discussion, Map = MapType.Airship };
        spawnWindow.Update(airshipAfterMeeting, spawnClock);
        Check("Airship spawn: 15-second window begins after discussion", true,
            spawnWindow.IsActive(airshipAfterMeeting, spawnClock.AddMilliseconds(14_999)) &&
            !spawnWindow.IsActive(airshipAfterMeeting, spawnClock.AddSeconds(15)));
        spawnWindow.Update(new AmongUsState
            { GameState = GameState.Tasks, OldGameState = GameState.Tasks, Map = MapType.Airship },
            spawnClock.AddSeconds(10));
        Check("Airship spawn: later task snapshots do not extend the window", false,
            spawnWindow.IsActive(airshipAfterMeeting, spawnClock.AddSeconds(15)));
        var spawnMix = SpatialVoicePolicy.Calculate(airshipAfterMeeting,
            new Player(), new Player { X = 20d }, new SpatialVoiceSettings(MaxDistance: 5d),
            airshipSpawnFallback: true);
        Check("Airship spawn: living player hears distant voice centered", true,
            spawnMix is { Audible: true, Pan: 0d, Gain: 1d });
        var spawnCameraState = new AmongUsState
        {
            GameState = GameState.Tasks, Map = MapType.Airship, CurrentCamera = CameraLocation.East
        };
        var spawnCameraMix = SpatialVoicePolicy.Calculate(spawnCameraState,
            new Player(), new Player { X = -8.2872d, Y = 0.0527d },
            new SpatialVoiceSettings(HearThroughCameras: true), airshipSpawnFallback: true);
        Check("Airship spawn: camera reception does not replace the centered fallback", true,
            spawnCameraMix is { Audible: true, Pan: 0d, Gain: 1d, CameraMuffled: false });
        Check("Airship spawn: ghost listener still obeys distance", false,
            SpatialVoicePolicy.Calculate(airshipAfterMeeting,
                new Player { IsDead = true }, new Player { X = 20d },
                new SpatialVoiceSettings(MaxDistance: 5d), airshipSpawnFallback: true).Audible);
        var spawnDoorState = new AmongUsState
        {
            GameState = GameState.Tasks, Map = MapType.Airship, ClosedDoors = [20]
        };
        Check("Airship spawn: closed doors still block the fallback", false,
            SpatialVoicePolicy.Calculate(spawnDoorState,
                new Player { X = 32.5d, Y = -4.1d }, new Player { X = 32.5d, Y = -4.5d },
                new SpatialVoiceSettings(WallsBlockAudio: true), airshipSpawnFallback: true).Audible);
        spawnWindow.Update(new AmongUsState
            { GameState = GameState.Lobby, Map = MapType.Airship }, spawnClock.AddSeconds(1));
        Check("Airship spawn: leaving tasks clears the window", false,
            spawnWindow.IsActive(airshipAfterMeeting, spawnClock.AddSeconds(1)));
        Check("vision hearing: pan uses effective range", true,
            Math.Abs(SpatialVoicePolicy.Calculate(visionTasks, new Player(),
                new Player { X = 1d }, visionPolicy).Pan - 0.4d) < 0.001d);
        Check("vision hearing: invalid short range follows 3.2.7 floor", false,
            SpatialVoicePolicy.Calculate(new AmongUsState { GameState = GameState.Tasks, LightRadius = 0d },
                new Player(), new Player { X = 1.2d }, visionPolicy).Audible);
        Check("vision hearing: lobby also uses the effective range", false,
            SpatialVoicePolicy.Calculate(new AmongUsState { GameState = GameState.Lobby, LightRadius = 0d },
                new Player(), new Player { X = 3d }, visionPolicy).Audible);
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
        Check("ghost reverb: haunting during tasks", true,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsDead = true }, ghostVolumePolicy).Reverb);
        Check("ghost reverb: disabled in meeting", false,
            SpatialVoicePolicy.Calculate(discussion, new Player { IsImpostor = true },
                new Player { IsDead = true }, ghostVolumePolicy).Reverb);
        Check("ghost reverb: ghost listener does not receive it", false,
            SpatialVoicePolicy.Calculate(tasks, ghostListener,
                new Player { IsDead = true }, ghostVolumePolicy).Reverb);

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
        RtpAudioJitterEstimator.Verify();
        var inboundReport = WebRtcPeerManager.FromReceptionReport(
            new ReceptionReportSample(1, 64, 8, 1200, 8_925_869, 0, 0), observedJitterMs: 1.5d);
        CheckGain("quality: observed RTP jitter", 1.5d, inboundReport.JitterMs ?? -1d);
        CheckGain("quality: RTCP interval loss percent", 25d, inboundReport.LossPercent ?? -1d);
        Check("quality: RTCP does not fabricate RTT", true, inboundReport.RttMs is null);
        var hostCandidate = new RTCIceCandidate(RTCIceProtocol.udp,
            System.Net.IPAddress.Loopback, 5000, RTCIceCandidateType.host);
        var relayCandidate = new RTCIceCandidate(RTCIceProtocol.udp,
            System.Net.IPAddress.Loopback, 5001, RTCIceCandidateType.relay);
        Check("quality: no nominated ICE pair is not direct", true,
            !WebRtcPeerManager.IsDirectHostPair(null));
        Check("quality: nominated host pair is direct", true,
            WebRtcPeerManager.IsDirectHostPair(new ChecklistEntry(hostCandidate, hostCandidate, true)));
        Check("quality: nominated relay pair is not direct", true,
            !WebRtcPeerManager.IsDirectHostPair(new ChecklistEntry(hostCandidate, relayCandidate, true)));
        Check("quality: direct path is included in report", true,
            WebRtcPeerManager.FromReceptionReport(
                new ReceptionReportSample(1, 0, 0, 0, 0, 0, 0), 1.5d, direct: true).Direct);
        var roundTrip = new RtcpRoundTripEstimator();
        var sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
        const uint lastSenderReport = 123_456;
        roundTrip.ObserveSent(new RTCPSenderReport(7, (ulong)lastSenderReport << 16, 0, 0, 0, []), sentAt);
        roundTrip.ObserveReceived(new ReceptionReportSample(8, 0, 0, 0, 0,
            lastSenderReport, 1_311), sentAt + System.Diagnostics.Stopwatch.Frequency / 10);
        Check("quality: mismatched sender SSRC does not fabricate RTT", true, roundTrip.RttMs is null);
        roundTrip.ObserveReceived(new ReceptionReportSample(7, 0, 0, 0, 0,
            lastSenderReport, 1_311), sentAt + System.Diagnostics.Stopwatch.Frequency / 10);
        Check("quality: RTCP RTT excludes remote report delay", true,
            roundTrip.RttMs is >= 79d and <= 81d);
        CheckGain("quality: RTT survives report merge", roundTrip.RttMs ?? -1d,
            WebRtcPeerManager.FromReceptionReport(
                new ReceptionReportSample(1, 0, 0, 0, 0, 0, 0), 1.5d, rttMs: roundTrip.RttMs).RttMs ?? -1d);
        const string iceTransactionId = "ice-rtt-0001";
        var icePair = new ChecklistEntry(hostCandidate, hostCandidate, true)
        {
            RequestTransactionID = iceTransactionId
        };
        var otherIcePair = new ChecklistEntry(hostCandidate, hostCandidate, true)
        {
            RequestTransactionID = iceTransactionId
        };
        var iceRequest = new STUNMessage(STUNMessageTypesEnum.BindingRequest);
        iceRequest.Header.TransactionId = System.Text.Encoding.ASCII.GetBytes(iceTransactionId);
        var iceResponse = new STUNMessage(STUNMessageTypesEnum.BindingSuccessResponse);
        iceResponse.Header.TransactionId = System.Text.Encoding.ASCII.GetBytes(iceTransactionId);
        var iceRoundTrip = new IceRoundTripEstimator();
        iceRoundTrip.ObserveSent(iceRequest, icePair, sentAt);
        Check("quality: ICE response must match nominated transaction", true,
            iceRoundTrip.GetRttMs(icePair) is null &&
            iceRoundTrip.GetRttMs(otherIcePair) is null);
        icePair.RequestTransactionID = "next-check-id";
        iceRoundTrip.ObserveReceived(iceResponse, icePair, sentAt + System.Diagnostics.Stopwatch.Frequency / 100);
        Check("quality: nominated ICE binding round trip", true,
            iceRoundTrip.GetRttMs(icePair) is >= 9d and <= 11d &&
            iceRoundTrip.GetRttMs(otherIcePair) is null);
        icePair.RequestTransactionID = iceTransactionId;
        var relayedIceRequest = new STUNMessage(STUNMessageTypesEnum.SendIndication);
        relayedIceRequest.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Data,
            iceRequest.ToByteBuffer(null, false)));
        iceRoundTrip.ObserveSent(relayedIceRequest, icePair, sentAt);
        iceRoundTrip.ObserveReceived(iceResponse, icePair, sentAt + System.Diagnostics.Stopwatch.Frequency / 50);
        Check("quality: TURN SendIndication carries the same ICE binding request", true,
            iceRoundTrip.GetRttMs(icePair) is >= 19d and <= 21d);

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
        Check("spatial off: wall still blocks audio", false,
            SpatialVoicePolicy.Calculate(fungleTasks, fungleListener, fungleSpeaker,
                new SpatialVoiceSettings(WallsBlockAudio: true, SpatialAudio: false)).Audible);
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
            Check("lobby wire: released publicLobby_mods default", true,
                document.RootElement.GetProperty("publicLobby_mods").GetString() == "NONE");
        }
        Check("lobby wire: round trip", true,
            JsonSerializer.Deserialize<LobbySettings>(wire, LobbySettings.WireJsonOptions) == lobbySettings);
        var importedMods = JsonSerializer.Deserialize<LobbySettings>(
            "{\"publicLobby_mods\":\"TOH4E\"}", LobbySettings.WireJsonOptions);
        Check("lobby wire: preserves imported publicLobby_mods", true,
            importedMods?.PublicLobbyMods == "TOH4E");

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
            ? "[PASS] 3.2.7 radio, voice effects, Airship fallback, listener-volume, vent, camera and wall policy"
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

    private static float[] CreateSizeEffectAudio(NosSizeVoiceEffect effect, double frequency = 440d)
    {
        var provider = new NosSizeVoiceSampleProvider(new TestSineSource(frequency));
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
