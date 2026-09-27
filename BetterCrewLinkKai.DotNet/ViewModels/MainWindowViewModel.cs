using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterCrewLinkKai.DotNet.Models;
using BetterCrewLinkKai.DotNet.Services;

namespace BetterCrewLinkKai.DotNet.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly SettingsService settingsService;
    private readonly GameLaunchService gameLaunchService;
    private readonly VoiceSessionService voiceSessionService;
    private readonly AudioDeviceService audioDeviceService;
    private readonly VoiceCaptureService voiceCaptureService;
    private readonly VoicePlaybackService voicePlaybackService;
    private readonly CosmeticCatalogService cosmeticCatalogService;
    private readonly AvatarImageService avatarImageService;
    private readonly HotkeyService hotkeyService;
    private readonly ObsOverlayService obsOverlayService = new();
    private readonly AmongUsProcessService amongUsProcessService;
    private readonly AmongUsMemoryReaderService amongUsMemoryReaderService;
    private AppSettings settings = new();
    private string lobbyCode = string.Empty;
    private string statusMessage = "Among Us を起動してください。";
    private string diagnosticMessage = string.Empty;
    private string voiceDebugLog = string.Empty;
    private bool isBusy;
    private bool isSettingsOpen;
    private bool isLaunchPlatformMenuOpen;
    private bool isGameOpen;
    private bool isInGameSession;
    private string waitingTitle = "Among Usの待機中";
    private string installedModLabel = "None";
    private string currentLobbyCode = "MENU";
    private string localPlayerName = "ROBBER";
    private Brush localPlayerBrush = new SolidColorBrush(Color.FromRgb(0x50, 0xEF, 0x39));
    private Brush localPlayerShadowBrush = new SolidColorBrush(Color.FromRgb(0x15, 0xA7, 0x42));
    private ImageSource? localAvatarImage;
    private string localHatFrontImage = string.Empty;
    private string localHatBackImage = string.Empty;
    private string localSkinImage = string.Empty;
    private string localVisorImage = string.Empty;
    private Thickness localHatMargin;
    private Thickness localSkinMargin;
    private Thickness localVisorMargin;
    private double localHatWidth;
    private double localSkinWidth;
    private double localVisorWidth;
    private AmongUsModType currentMod = AmongUsModType.None;
    private double microphoneLevel;
    private bool isMicrophoneMonitorRunning;
    private bool isVoiceEffectTestRunning;
    private int capturedVoiceFrames;
    private int transmittedVoiceFrames;
    private int queuedVoiceFrames;
    private bool isMuted;
    private bool isDeafened;
    private bool suppressLocalMuteStateNotification;
    private bool isPushToTalkHeld;
    private bool isImpostorRadioHeld;
    private bool lastTalkingState;
    private bool localIsTalking;
    private AmongUsState? lastAmongUsState;
    private readonly Dictionary<int, DateTimeOffset> remoteTalkingUntil = [];
    private readonly HashSet<int> remoteAudioConnectedPlayerKeys = [];
    private bool isVoiceServerSettingsOpen;
    private string voiceServerUrl = "https://bettercrewl.ink";
    private string selectedVoiceServerUrl = "https://bettercrewl.ink";
    private bool isVoiceServerUrlValid = true;
    private CancellationTokenSource? pendingSettingsSave;
    private bool hasLoaded;
    private const double RemoteTalkingLevelThreshold = 3.0d;

    public MainWindowViewModel()
        : this(new SettingsService(), new GameLaunchService(), new VoiceSessionService(), new AudioDeviceService(), new VoiceCaptureService(), new VoicePlaybackService(), new CosmeticCatalogService(), new AvatarImageService(), new HotkeyService(), new AmongUsProcessService(), new AmongUsMemoryReaderService())
    {
    }

    public MainWindowViewModel(
        SettingsService settingsService,
        GameLaunchService gameLaunchService,
        VoiceSessionService voiceSessionService,
        AudioDeviceService audioDeviceService,
        VoiceCaptureService voiceCaptureService,
        VoicePlaybackService voicePlaybackService,
        CosmeticCatalogService cosmeticCatalogService,
        AvatarImageService avatarImageService,
        HotkeyService hotkeyService,
        AmongUsProcessService amongUsProcessService,
        AmongUsMemoryReaderService amongUsMemoryReaderService)
    {
        this.settingsService = settingsService;
        this.gameLaunchService = gameLaunchService;
        this.voiceSessionService = voiceSessionService;
        this.audioDeviceService = audioDeviceService;
        this.voiceCaptureService = voiceCaptureService;
        this.voicePlaybackService = voicePlaybackService;
        this.cosmeticCatalogService = cosmeticCatalogService;
        this.avatarImageService = avatarImageService;
        this.hotkeyService = hotkeyService;
        this.amongUsProcessService = amongUsProcessService;
        this.amongUsMemoryReaderService = amongUsMemoryReaderService;
        this.amongUsProcessService.ProcessChanged += OnAmongUsProcessChanged;
        this.amongUsMemoryReaderService.StateChanged += OnAmongUsStateChanged;
        this.amongUsMemoryReaderService.Error += OnAmongUsMemoryError;
        this.amongUsMemoryReaderService.DiagnosticChanged += OnAmongUsDiagnosticChanged;
        this.audioDeviceService.MicrophoneLevelChanged += OnMicrophoneLevelChanged;
        this.voiceCaptureService.FrameCaptured += OnVoiceFrameCaptured;
        this.voiceSessionService.StateChanged += OnVoiceSessionStateChanged;
        this.voiceSessionService.Error += OnVoiceSessionError;
        this.voiceSessionService.DebugLogChanged += OnVoiceDebugLogChanged;
        this.voiceSessionService.PeerDataPayloadQueued += OnPeerDataPayloadQueued;
        this.voiceSessionService.PeerAudioFrameQueued += OnPeerAudioFrameQueued;
        this.voiceSessionService.PeerAudioFrameReceived += OnPeerAudioFrameReceived;
        this.hotkeyService.HotkeyChanged += OnHotkeyChanged;
        Platforms = new ObservableCollection<GamePlatform>(
            Enum.GetValues<GamePlatform>().Where(static platform => platform != GamePlatform.Custom));
        PushToTalkModes = new ObservableCollection<PushToTalkMode>(Enum.GetValues<PushToTalkMode>());
        Microphones = [];
        Speakers = [];
        OtherPlayers = [];
        VoiceServerUrls = [];
        LanguageOptions = new ObservableCollection<OptionViewModel>(
        [
            new("ja", "日本語"),
            new("en", "English"),
            new("es", "Español"),
            new("pt_BR", "Português"),
            new("fr", "Français"),
            new("de", "Deutsch"),
            new("ru", "Русский"),
            new("zh_CN", "简体中文"),
            new("ko", "한국어")
        ]);
        OverlayPositionOptions = new ObservableCollection<OptionViewModel>(
        [
            new("hidden", "非表示"),
            new("top", "中央上"),
            new("bottom-left", "左下"),
            new("right", "右"),
            new("right-box", "背景付き右"),
            new("left", "左"),
            new("left-box", "背景付き左")
        ]);
        ModOptions = new ObservableCollection<OptionViewModel>(
            AmongUsMod.KnownMods.Select(static mod => new OptionViewModel(ModIdToProtocol(mod.Id), mod.Label)));

        LoadCommand = new RelayCommand(_ => LoadAsync());
        SaveCommand = new RelayCommand(_ => SaveAsync());
        LaunchGameCommand = new RelayCommand(_ => LaunchGameAsync());
        ToggleConnectionCommand = new RelayCommand(_ => ToggleConnectionAsync());
        RefreshAudioDevicesCommand = new RelayCommand(_ =>
        {
            RefreshAudioDevices();
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            return Task.CompletedTask;
        });
        TestSpeakerCommand = new RelayCommand(_ => TestSpeakerAsync());
        TestVoiceEffectCommand = new RelayCommand(_ =>
        {
            ToggleVoiceEffectTest();
            return Task.CompletedTask;
        });
        ToggleMuteCommand = new RelayCommand(_ =>
        {
            ToggleMute();
            return Task.CompletedTask;
        });
        ToggleDeafenCommand = new RelayCommand(_ =>
        {
            ToggleDeafen();
            return Task.CompletedTask;
        });
        ToggleSettingsCommand = new RelayCommand(_ =>
        {
            if (IsSettingsOpen)
            {
                IsSettingsOpen = false;
                return SaveAsync();
            }

            IsSettingsOpen = true;
            return Task.CompletedTask;
        });
        ToggleLaunchPlatformMenuCommand = new RelayCommand(_ =>
        {
            IsLaunchPlatformMenuOpen = !IsLaunchPlatformMenuOpen;
            return Task.CompletedTask;
        });
        SelectLaunchPlatformCommand = new RelayCommand(parameter =>
        {
            if (parameter is GamePlatform platform)
            {
                SelectedPlatform = platform;
                IsLaunchPlatformMenuOpen = false;
            }

            return Task.CompletedTask;
        });
        BrowseCustomLaunchPathCommand = new RelayCommand(_ =>
        {
            BrowseCustomLaunchPath();
            return Task.CompletedTask;
        });
        OpenVoiceServerSettingsCommand = new RelayCommand(_ =>
        {
            OpenVoiceServerSettings();
            return Task.CompletedTask;
        });
        CloseVoiceServerSettingsCommand = new RelayCommand(_ =>
        {
            IsVoiceServerSettingsOpen = false;
            return Task.CompletedTask;
        });
        SaveVoiceServerSettingsCommand = new RelayCommand(_ => SaveVoiceServerSettingsAsync());
        ResetVoiceServerCommand = new RelayCommand(_ => ResetVoiceServerAsync());
        RemoveVoiceServerCommand = new RelayCommand(_ => RemoveVoiceServerAsync());
        CopyObsOverlayUrlCommand = new RelayCommand(_ =>
        {
            Clipboard.SetText(ObsOverlayUrl);
            StatusMessage = "OBSオーバーレイURLをコピーしました。";
            return Task.CompletedTask;
        });
        RegenerateObsSecretCommand = new RelayCommand(_ => RegenerateObsSecretAsync());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettingsAsync());
        OpenGitHubCommand = new RelayCommand(_ =>
        {
            OpenExternal("https://github.com/kuretoshi/BetterCrewLink/tree/voice_fixed");
            return Task.CompletedTask;
        });
        OpenDiscordCommand = new RelayCommand(_ =>
        {
            OpenExternal("https://discord.gg/jEyDrpBsmJ");
            return Task.CompletedTask;
        });
        CloseCommand = new RelayCommand(async _ =>
        {
            pendingSettingsSave?.Cancel();
            await settingsService.SaveAsync(Settings);
            obsOverlayService.Stop();
            Application.Current.Shutdown();
        });
    }

    public AppSettings Settings
    {
        get => settings;
        private set
        {
            if (SetProperty(ref settings, value))
            {
                OnPropertyChanged(nameof(SelectedPlatform));
                OnPropertyChanged(nameof(LaunchPlatformLabel));
                OnPropertyChanged(nameof(IsCustomLaunchPlatformSelected));
                OnPropertyChanged(nameof(CustomLaunchPath));
                OnPropertyChanged(nameof(SelectedPushToTalkMode));
                OnPropertyChanged(nameof(SelectedMicrophone));
                OnPropertyChanged(nameof(SelectedSpeaker));
                OnPropertyChanged(nameof(PushToTalkShortcut));
                OnPropertyChanged(nameof(MuteShortcut));
                OnPropertyChanged(nameof(ImpostorRadioShortcut));
                OnPropertyChanged(nameof(DeafenShortcut));
                OnPropertyChanged(nameof(ObsOverlayUrl));
                OnPropertyChanged(nameof(ObsOverlayStatus));
                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(CrewVolumeAsGhost));
                OnPropertyChanged(nameof(GhostVolumeAsImpostor));
                OnPropertyChanged(nameof(VoiceEffectStrength));
                OnPropertyChanged(nameof(MicrophoneGainEnabled));
                OnPropertyChanged(nameof(MicrophoneGain));
                OnPropertyChanged(nameof(MicSensitivityEnabled));
                OnPropertyChanged(nameof(MicSensitivity));
                OnPropertyChanged(nameof(VadEnabled));
                OnPropertyChanged(nameof(EchoCancellation));
                OnPropertyChanged(nameof(NoiseSuppression));
                OnPropertyChanged(nameof(EnableSpatialAudio));
                OnPropertyChanged(nameof(NatFix));
                OnPropertyChanged(nameof(MobileHost));
                OnPropertyChanged(nameof(AlwaysOnTop));
                OnPropertyChanged(nameof(EnableOverlay));
                OnPropertyChanged(nameof(CompactOverlay));
                OnPropertyChanged(nameof(MeetingOverlay));
                OnPropertyChanged(nameof(OverlayPosition));
                NotifyOverlayLayoutChanged();
                OnPropertyChanged(nameof(HardwareAcceleration));
                OnPropertyChanged(nameof(OldSampleDebug));
                OnPropertyChanged(nameof(Language));
                OnPropertyChanged(nameof(HideCode));
                OnPropertyChanged(nameof(ShowLobbyCode));
                OnPropertyChanged(nameof(ObsOverlay));
                OnPropertyChanged(nameof(DisplayLobbyCode));
                OnPropertyChanged(nameof(ObsOverlayUrl));
                OnPropertyChanged(nameof(ObsOverlayStatus));
            }
        }
    }

    public ObservableCollection<GamePlatform> Platforms { get; }

    public ObservableCollection<PushToTalkMode> PushToTalkModes { get; }

    public ObservableCollection<AudioDeviceInfo> Microphones { get; }

    public ObservableCollection<AudioDeviceInfo> Speakers { get; }

    public ObservableCollection<PlayerTileViewModel> OtherPlayers { get; }

    public ObservableCollection<MeetingOverlayPlayerViewModel> MeetingOverlayPlayers { get; } = [];

    public ObservableCollection<string> VoiceServerUrls { get; }

    public ObservableCollection<OptionViewModel> LanguageOptions { get; }

    public ObservableCollection<OptionViewModel> OverlayPositionOptions { get; }

    public ObservableCollection<OptionViewModel> ModOptions { get; }

    public GamePlatform SelectedPlatform
    {
        get => Settings.LaunchPlatform;
        set
        {
            if (Settings.LaunchPlatform != value)
            {
                Settings.LaunchPlatform = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LaunchPlatformLabel));
                OnPropertyChanged(nameof(IsCustomLaunchPlatformSelected));
                QueueSettingsSave();
            }
        }
    }

    public string LaunchPlatformLabel => Settings.LaunchPlatform switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.Epic => "Epic",
        GamePlatform.Microsoft => "その他",
        GamePlatform.Custom => "その他",
        _ => Settings.LaunchPlatform.ToString()
    };

    public bool IsCustomLaunchPlatformSelected => Settings.LaunchPlatform == GamePlatform.Custom;

    public string CustomLaunchPath
    {
        get => GetCustomLaunchPlatform().RunPath;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            var custom = GetCustomLaunchPlatform();
            if (string.Equals(custom.RunPath, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            custom.RunPath = next;
            custom.Execute = string.IsNullOrWhiteSpace(next) ? [] : [next];
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public string SelectedMicrophone
    {
        get => Settings.Microphone;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Default" : value;
            if (Settings.Microphone == next)
            {
                return;
            }

            Settings.Microphone = next;
            OnPropertyChanged();
            if (!RestartVoiceEffectTestIfRunning())
            {
                RestartMicrophoneMonitor();
                RestartVoiceCapture();
            }

            QueueSettingsSave();
        }
    }

    public string SelectedSpeaker
    {
        get => Settings.Speaker;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Default" : value;
            if (Settings.Speaker == next)
            {
                return;
            }

            Settings.Speaker = next;
            OnPropertyChanged();
            voicePlaybackService.SetSpeaker(Settings.Speaker);
            RestartVoiceEffectTestIfRunning();
            QueueSettingsSave();
        }
    }

    public PushToTalkMode SelectedPushToTalkMode
    {
        get => Settings.PushToTalkMode;
        set
        {
            if (Settings.PushToTalkMode != value)
            {
                Settings.PushToTalkMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTransmitting));
                _ = RefreshVadAsync();
                QueueSettingsSave();
            }
        }
    }

    public string LobbyCode
    {
        get => lobbyCode;
        set => SetProperty(ref lobbyCode, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string DiagnosticMessage
    {
        get => diagnosticMessage;
        private set => SetProperty(ref diagnosticMessage, value);
    }

    public string VoiceDebugLog
    {
        get => voiceDebugLog;
        private set => SetProperty(ref voiceDebugLog, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public bool IsConnected => voiceSessionService.State == VoiceSessionState.Connected;

    public string ConnectionButtonText => IsConnected ? "切断" : "接続";

    public bool IsGameOpen
    {
        get => isGameOpen;
        private set
        {
            if (SetProperty(ref isGameOpen, value))
            {
                OnPropertyChanged(nameof(ShowWaitingView));
                OnPropertyChanged(nameof(ShowMenuView));
                OnPropertyChanged(nameof(ShowVoiceView));
            }
        }
    }

    public bool IsInGameSession
    {
        get => isInGameSession;
        private set
        {
            if (SetProperty(ref isInGameSession, value))
            {
                OnPropertyChanged(nameof(ShowWaitingView));
                OnPropertyChanged(nameof(ShowMenuView));
                OnPropertyChanged(nameof(ShowVoiceView));
            }
        }
    }

    public bool ShowWaitingView => !IsGameOpen;

    public bool ShowMenuView => IsGameOpen && !IsInGameSession;

    public bool ShowVoiceView => IsGameOpen && IsInGameSession;

    public bool IsDiscussionView => lastAmongUsState?.GameState == GameState.Discussion;

    public bool CanChangeLobbySettings =>
        lastAmongUsState?.GameState == GameState.Lobby &&
        (lastAmongUsState.IsHost || voiceSessionService.HostLobbySettings is null);

    public string LobbySettingsModeText => CanChangeLobbySettings
        ? "このロビーの設定を変更できます。"
        : "ロビーでのみ変更できます！";

    private LobbySettings EffectiveLobbySettings => !CanChangeLobbySettings && voiceSessionService.HostLobbySettings is not null
        ? voiceSessionService.HostLobbySettings
        : Settings.LocalLobbySettings;

    public double EffectiveMaxDistance
    {
        get => EffectiveLobbySettings.MaxDistance;
        set
        {
            if (!CanChangeLobbySettings)
            {
                return;
            }

            Settings.LocalLobbySettings.MaxDistance = value;
            OnEffectiveLobbySettingsChanged();
            RefreshLobbySettingsFromSettings();
            QueueSettingsSave();
        }
    }

    public bool EffectiveWallsBlockAudio
    {
        get => EffectiveLobbySettings.WallsBlockAudio;
        set => SetEffectiveLobbyBool(value, static settings => settings.WallsBlockAudio, static (settings, next) => settings.WallsBlockAudio = next, nameof(EffectiveWallsBlockAudio));
    }

    public bool EffectiveVisionHearing
    {
        get => EffectiveLobbySettings.VisionHearing;
        set => SetEffectiveLobbyBool(value, static settings => settings.VisionHearing, static (settings, next) => settings.VisionHearing = next, nameof(EffectiveVisionHearing));
    }

    public bool EffectiveHaunting
    {
        get => EffectiveLobbySettings.Haunting;
        set => SetEffectiveLobbyBool(value, static settings => settings.Haunting, static (settings, next) => settings.Haunting = next, nameof(EffectiveHaunting));
    }

    public bool EffectiveThirdPartyHaunting
    {
        get => EffectiveLobbySettings.ThirdPartyHaunting;
        set => SetEffectiveLobbyBool(value, static settings => settings.ThirdPartyHaunting, static (settings, next) => settings.ThirdPartyHaunting = next, nameof(EffectiveThirdPartyHaunting));
    }

    public bool EffectiveVoiceEffectEnabled
    {
        get => EffectiveLobbySettings.VoiceEffectEnabled;
        set => SetEffectiveLobbyBool(value, static settings => settings.VoiceEffectEnabled, static (settings, next) => settings.VoiceEffectEnabled = next, nameof(EffectiveVoiceEffectEnabled));
    }

    public bool EffectiveHearImpostorsInVents
    {
        get => EffectiveLobbySettings.HearImpostorsInVents;
        set => SetEffectiveLobbyBool(value, static settings => settings.HearImpostorsInVents, static (settings, next) => settings.HearImpostorsInVents = next, nameof(EffectiveHearImpostorsInVents));
    }

    public bool EffectiveImpostorsHearImpostorsInVent
    {
        get => EffectiveLobbySettings.ImpostorsHearImpostorsInVent;
        set => SetEffectiveLobbyBool(value, static settings => settings.ImpostorsHearImpostorsInVent, static (settings, next) => settings.ImpostorsHearImpostorsInVent = next, nameof(EffectiveImpostorsHearImpostorsInVent));
    }

    public bool EffectiveCommsSabotage
    {
        get => EffectiveLobbySettings.CommsSabotage;
        set => SetEffectiveLobbyBool(value, static settings => settings.CommsSabotage, static (settings, next) => settings.CommsSabotage = next, nameof(EffectiveCommsSabotage));
    }

    public bool EffectiveHearThroughCameras
    {
        get => EffectiveLobbySettings.HearThroughCameras;
        set => SetEffectiveLobbyBool(value, static settings => settings.HearThroughCameras, static (settings, next) => settings.HearThroughCameras = next, nameof(EffectiveHearThroughCameras));
    }

    public bool EffectiveImpostorRadioEnabled
    {
        get => EffectiveLobbySettings.ImpostorRadioEnabled;
        set => SetEffectiveLobbyBool(value, static settings => settings.ImpostorRadioEnabled, static (settings, next) => settings.ImpostorRadioEnabled = next, nameof(EffectiveImpostorRadioEnabled));
    }

    public bool EffectiveDeadOnly
    {
        get => EffectiveLobbySettings.DeadOnly;
        set => SetEffectiveLobbyBool(value, static settings => settings.DeadOnly, static (settings, next) => settings.DeadOnly = next, nameof(EffectiveDeadOnly));
    }

    public bool EffectiveMeetingGhostOnly
    {
        get => EffectiveLobbySettings.MeetingGhostOnly;
        set => SetEffectiveLobbyBool(value, static settings => settings.MeetingGhostOnly, static (settings, next) => settings.MeetingGhostOnly = next, nameof(EffectiveMeetingGhostOnly));
    }

    public string CurrentLobbyCode
    {
        get => currentLobbyCode;
        private set
        {
            if (SetProperty(ref currentLobbyCode, value))
            {
                OnPropertyChanged(nameof(DisplayLobbyCode));
            }
        }
    }

    public string DisplayLobbyCode => Settings.HideCode ? MaskLobbyCode(CurrentLobbyCode) : CurrentLobbyCode;

    public string LocalPlayerName
    {
        get => localPlayerName;
        private set => SetProperty(ref localPlayerName, value);
    }

    public Brush LocalPlayerBrush
    {
        get => localPlayerBrush;
        private set => SetProperty(ref localPlayerBrush, value);
    }

    public Brush LocalPlayerShadowBrush
    {
        get => localPlayerShadowBrush;
        private set => SetProperty(ref localPlayerShadowBrush, value);
    }

    public ImageSource? LocalAvatarImage
    {
        get => localAvatarImage;
        private set => SetProperty(ref localAvatarImage, value);
    }

    public string LocalHatFrontImage
    {
        get => localHatFrontImage;
        private set => SetProperty(ref localHatFrontImage, value);
    }

    public string LocalHatBackImage
    {
        get => localHatBackImage;
        private set => SetProperty(ref localHatBackImage, value);
    }

    public string LocalSkinImage
    {
        get => localSkinImage;
        private set => SetProperty(ref localSkinImage, value);
    }

    public string LocalVisorImage
    {
        get => localVisorImage;
        private set => SetProperty(ref localVisorImage, value);
    }

    public Thickness LocalHatMargin
    {
        get => localHatMargin;
        private set => SetProperty(ref localHatMargin, value);
    }

    public Thickness LocalSkinMargin
    {
        get => localSkinMargin;
        private set => SetProperty(ref localSkinMargin, value);
    }

    public Thickness LocalVisorMargin
    {
        get => localVisorMargin;
        private set => SetProperty(ref localVisorMargin, value);
    }

    public double LocalHatWidth
    {
        get => localHatWidth;
        private set => SetProperty(ref localHatWidth, value);
    }

    public double LocalSkinWidth
    {
        get => localSkinWidth;
        private set => SetProperty(ref localSkinWidth, value);
    }

    public double LocalVisorWidth
    {
        get => localVisorWidth;
        private set => SetProperty(ref localVisorWidth, value);
    }

    public double MicrophoneLevel
    {
        get => microphoneLevel;
        private set => SetProperty(ref microphoneLevel, value);
    }

    public bool IsMicrophoneMonitorRunning
    {
        get => isMicrophoneMonitorRunning;
        private set => SetProperty(ref isMicrophoneMonitorRunning, value);
    }

    public bool IsVoiceEffectTestRunning
    {
        get => isVoiceEffectTestRunning;
        private set
        {
            if (SetProperty(ref isVoiceEffectTestRunning, value))
            {
                OnPropertyChanged(nameof(VoiceEffectTestButtonText));
            }
        }
    }

    public string VoiceEffectTestButtonText => IsVoiceEffectTestRunning ? "ボイスエフェクト停止" : "ボイスエフェクトテスト";

    public double MasterVolume
    {
        get => Settings.MasterVolume;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.MasterVolume - next) < 0.001)
            {
                return;
            }

            Settings.MasterVolume = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            RestartVoiceEffectTestIfRunning();
            QueueSettingsSave();
        }
    }

    public double CrewVolumeAsGhost
    {
        get => Settings.CrewVolumeAsGhost;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.CrewVolumeAsGhost - next) < 0.001)
            {
                return;
            }

            Settings.CrewVolumeAsGhost = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public double GhostVolumeAsImpostor
    {
        get => Settings.GhostVolumeAsImpostor;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.GhostVolumeAsImpostor - next) < 0.001)
            {
                return;
            }

            Settings.GhostVolumeAsImpostor = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public double VoiceEffectStrength
    {
        get => Settings.VoiceEffectStrength;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.VoiceEffectStrength - next) < 0.001)
            {
                return;
            }

            Settings.VoiceEffectStrength = next;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            RestartVoiceEffectTestIfRunning();
            QueueSettingsSave();
        }
    }

    public bool MicrophoneGainEnabled
    {
        get => Settings.MicrophoneGainEnabled;
        set
        {
            if (Settings.MicrophoneGainEnabled == value)
            {
                return;
            }

            Settings.MicrophoneGainEnabled = value;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public double MicrophoneGain
    {
        get => Settings.MicrophoneGain;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 200d));
            if (Math.Abs(Settings.MicrophoneGain - next) < 0.001)
            {
                return;
            }

            Settings.MicrophoneGain = next;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public bool MicSensitivityEnabled
    {
        get => Settings.MicSensitivityEnabled;
        set
        {
            if (Settings.MicSensitivityEnabled == value)
            {
                return;
            }

            Settings.MicSensitivityEnabled = value;
            OnPropertyChanged();
            _ = RefreshVadAsync(force: true);
            QueueSettingsSave();
        }
    }

    public double MicSensitivity
    {
        get => Settings.MicSensitivity;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0d, 1d), 3);
            if (Math.Abs(Settings.MicSensitivity - next) < 0.0001)
            {
                return;
            }

            Settings.MicSensitivity = next;
            OnPropertyChanged();
            _ = RefreshVadAsync(force: true);
            QueueSettingsSave();
        }
    }

    public bool VadEnabled
    {
        get => Settings.VadEnabled;
        set
        {
            if (Settings.VadEnabled == value)
            {
                return;
            }

            Settings.VadEnabled = value;
            OnPropertyChanged();
            _ = RefreshVadAsync(force: true);
            QueueSettingsSave();
        }
    }

    public bool EchoCancellation
    {
        get => Settings.EchoCancellation;
        set
        {
            if (Settings.EchoCancellation == value)
            {
                return;
            }

            Settings.EchoCancellation = value;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public bool NoiseSuppression
    {
        get => Settings.NoiseSuppression;
        set
        {
            if (Settings.NoiseSuppression == value)
            {
                return;
            }

            Settings.NoiseSuppression = value;
            OnPropertyChanged();
            RestartVoiceCapture();
            QueueSettingsSave();
        }
    }

    public bool EnableSpatialAudio
    {
        get => Settings.EnableSpatialAudio;
        set
        {
            if (Settings.EnableSpatialAudio == value)
            {
                return;
            }

            Settings.EnableSpatialAudio = value;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public bool NatFix
    {
        get => Settings.NatFix;
        set
        {
            if (Settings.NatFix == value)
            {
                return;
            }

            Settings.NatFix = value;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            OnPropertyChanged(nameof(PeerConnectionStatus));
            QueueSettingsSave();
        }
    }

    public bool MobileHost
    {
        get => Settings.MobileHost;
        set
        {
            if (Settings.MobileHost == value)
            {
                return;
            }

            Settings.MobileHost = value;
            OnPropertyChanged();
            RefreshVoiceMixFromSettings();
            QueueSettingsSave();
        }
    }

    public bool AlwaysOnTop
    {
        get => Settings.AlwaysOnTop;
        set
        {
            if (Settings.AlwaysOnTop == value)
            {
                return;
            }

            Settings.AlwaysOnTop = value;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool EnableOverlay
    {
        get => Settings.EnableOverlay;
        set
        {
            if (Settings.EnableOverlay == value)
            {
                return;
            }

            Settings.EnableOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool CompactOverlay
    {
        get => Settings.CompactOverlay;
        set
        {
            if (Settings.CompactOverlay == value)
            {
                return;
            }

            Settings.CompactOverlay = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLocalVisibleInGameOverlay));
            foreach (var player in OtherPlayers)
            {
                player.CompactOverlay = value;
            }

            OnPropertyChanged(nameof(IsOverlayPanelBackgroundVisible));
            OnPropertyChanged(nameof(OverlayPanelBackground));
            OnPropertyChanged(nameof(OverlayPanelPadding));
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool MeetingOverlay
    {
        get => Settings.MeetingOverlay;
        set
        {
            if (Settings.MeetingOverlay == value)
            {
                return;
            }

            Settings.MeetingOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public string OverlayPosition
    {
        get => NormalizeOverlayPosition(Settings.OverlayPosition);
        set
        {
            var next = NormalizeOverlayPosition(value);
            if (string.Equals(Settings.OverlayPosition, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.OverlayPosition = next;
            OnPropertyChanged();
            NotifyOverlayLayoutChanged();
            QueueSettingsSave();
        }
    }

    public bool IsOverlayLeft => IsLeftOverlayPosition(Settings.OverlayPosition);

    public bool IsOverlayIconOnly => IsIconOnlyOverlayPosition(Settings.OverlayPosition);

    public bool IsOverlayHorizontal => IsHorizontalOverlayPosition(Settings.OverlayPosition);

    public bool HasOverlayBackground => HasBackgroundOverlayPosition(Settings.OverlayPosition);

    public HorizontalAlignment OverlayItemsAlignment => NormalizeOverlayPosition(Settings.OverlayPosition) switch
    {
        "top" => HorizontalAlignment.Center,
        "right" or "right-box" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left
    };

    public int OverlayNameColumn => IsOverlayLeft || IsOverlayIconOnly ? 1 : 0;

    public int OverlayAvatarColumn => IsOverlayLeft || IsOverlayIconOnly ? 0 : 1;

    public GridLength OverlayFirstColumnWidth => IsOverlayIconOnly
        ? new GridLength(76)
        : IsOverlayLeft ? new GridLength(76) : new GridLength(1, GridUnitType.Star);

    public GridLength OverlaySecondColumnWidth => IsOverlayIconOnly
        ? new GridLength(0)
        : IsOverlayLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(76);

    public HorizontalAlignment OverlayNameAlignment => IsOverlayLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    public System.Windows.Controls.Orientation OverlayItemsOrientation => IsOverlayHorizontal
        ? System.Windows.Controls.Orientation.Horizontal
        : System.Windows.Controls.Orientation.Vertical;

    public Visibility OverlayNameVisibility => IsOverlayIconOnly ? Visibility.Collapsed : Visibility.Visible;

    public Visibility OverlaySideNameVisibility => NormalizeOverlayPosition(Settings.OverlayPosition) is "right" or "left"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsOverlayPanelBackgroundVisible =>
        HasOverlayBackground &&
        !(Settings.CompactOverlay && NormalizeOverlayPosition(Settings.OverlayPosition) == "top") &&
        (!Settings.CompactOverlay || IsLocalVisibleInGameOverlay || OtherPlayers.Any(static player => player.IsVisibleInGameOverlay));

    public Brush OverlayPanelBackground => IsOverlayPanelBackgroundVisible
        ? new SolidColorBrush(Color.FromArgb(0x78, 0x12, 0x0F, 0x18))
        : Brushes.Transparent;

    public Thickness OverlayPanelPadding => IsOverlayPanelBackgroundVisible ? new Thickness(8, 8, 8, 0) : new Thickness(0);

    public Thickness OverlayLocalItemMargin => IsOverlayHorizontal ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 0, 8);

    public Thickness OverlayPlayerItemMargin => IsOverlayHorizontal ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 0, 8);

    public bool IsLocalVisibleInGameOverlay => !Settings.CompactOverlay || LocalIsTalking;

    private void NotifyOverlayLayoutChanged()
    {
        OnPropertyChanged(nameof(IsOverlayLeft));
        OnPropertyChanged(nameof(IsOverlayIconOnly));
        OnPropertyChanged(nameof(IsOverlayHorizontal));
        OnPropertyChanged(nameof(HasOverlayBackground));
        OnPropertyChanged(nameof(OverlayItemsAlignment));
        OnPropertyChanged(nameof(OverlayNameColumn));
        OnPropertyChanged(nameof(OverlayAvatarColumn));
        OnPropertyChanged(nameof(OverlayFirstColumnWidth));
        OnPropertyChanged(nameof(OverlaySecondColumnWidth));
        OnPropertyChanged(nameof(OverlayNameAlignment));
        OnPropertyChanged(nameof(OverlayItemsOrientation));
        OnPropertyChanged(nameof(OverlayNameVisibility));
        OnPropertyChanged(nameof(OverlaySideNameVisibility));
        OnPropertyChanged(nameof(IsOverlayPanelBackgroundVisible));
        OnPropertyChanged(nameof(OverlayPanelBackground));
        OnPropertyChanged(nameof(OverlayPanelPadding));
        OnPropertyChanged(nameof(OverlayLocalItemMargin));
        OnPropertyChanged(nameof(OverlayPlayerItemMargin));
    }

    private static bool IsLeftOverlayPosition(string? overlayPosition)
    {
        var position = overlayPosition?.Trim() ?? string.Empty;
        return position.Contains("left", StringComparison.OrdinalIgnoreCase) ||
               position.Contains("左", StringComparison.Ordinal);
    }

    private static bool IsHorizontalOverlayPosition(string? overlayPosition)
    {
        var normalized = NormalizeOverlayPosition(overlayPosition);
        return normalized is "top" or "bottom-left";
    }

    private static bool IsIconOnlyOverlayPosition(string? overlayPosition)
    {
        var normalized = NormalizeOverlayPosition(overlayPosition);
        return normalized is "top" or "bottom-left" or "right-box" or "left-box";
    }

    private static bool HasBackgroundOverlayPosition(string? overlayPosition)
    {
        var normalized = NormalizeOverlayPosition(overlayPosition);
        return normalized is "top" or "bottom-left" or "right-box" or "left-box";
    }

    public static string NormalizeOverlayPosition(string? overlayPosition)
    {
        var position = overlayPosition?.Trim().ToLowerInvariant() ?? string.Empty;
        return position switch
        {
            "hidden" or "none" or "off" or "非表示" => "hidden",
            "top" or "center-top" or "top-center" or "中央上" => "top",
            "bottom-left" or "left-bottom" or "左下" => "bottom-left",
            "right-box" or "background-right" or "背景付き右" => "right-box",
            "left-box" or "background-left" or "背景付き左" => "left-box",
            "left" or "top-left" or "左" or "左上" => "left",
            _ => "right"
        };
    }

    public bool HardwareAcceleration
    {
        get => Settings.HardwareAcceleration;
        set
        {
            if (Settings.HardwareAcceleration == value)
            {
                return;
            }

            Settings.HardwareAcceleration = value;
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool OldSampleDebug
    {
        get => Settings.OldSampleDebug;
        set
        {
            if (Settings.OldSampleDebug == value)
            {
                return;
            }

            Settings.OldSampleDebug = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VoiceCaptureStatus));
            QueueSettingsSave();
        }
    }

    public string Language
    {
        get => Settings.Language;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "ja" : value;
            if (string.Equals(Settings.Language, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.Language = next;
            ApplyLanguageDefaults();
            OnPropertyChanged();
            QueueSettingsSave();
        }
    }

    public bool HideCode
    {
        get => Settings.HideCode;
        set
        {
            if (Settings.HideCode == value)
            {
                return;
            }

            Settings.HideCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLobbyCode));
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool ShowLobbyCode
    {
        get => !Settings.HideCode;
        set
        {
            var hideCode = !value;
            if (Settings.HideCode == hideCode)
            {
                return;
            }

            Settings.HideCode = hideCode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HideCode));
            OnPropertyChanged(nameof(DisplayLobbyCode));
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public bool ObsOverlay
    {
        get => Settings.ObsOverlay;
        set
        {
            if (Settings.ObsOverlay == value)
            {
                return;
            }

            Settings.ObsOverlay = value;
            OnPropertyChanged();
            RefreshObsOverlayService();
            QueueSettingsSave();
        }
    }

    public string VoiceCaptureStatus
    {
        get
        {
            var status = $"voice frames: {capturedVoiceFrames}, tx: {transmittedVoiceFrames}, queued: {queuedVoiceFrames}";
            if (!Settings.OldSampleDebug || voiceCaptureService.WaveFormat is null)
            {
                return status;
            }

            var format = voiceCaptureService.WaveFormat;
            return $"{status}, mic: {format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch/{format.Encoding}";
        }
    }

    public string PeerConnectionStatus => $"peers: {voiceSessionService.PeerConnections.Count}, NAT fix: {(voiceSessionService.UseNatFix ? "on" : "off")}, ICE: {voiceSessionService.IceServers.Count}";

    public string ObsOverlayStatus => Settings.ObsOverlay
        ? $"OBS overlay: {ObsOverlayUrl}"
        : "OBS overlay: disabled";

    public string ObsOverlayUrl =>
        $"{ObsOverlayService.Url}?secret={Uri.EscapeDataString(Settings.ObsSecret ?? string.Empty)}";

    public bool IsMuted
    {
        get => isMuted;
        private set
        {
            if (SetProperty(ref isMuted, value))
            {
                OnPropertyChanged(nameof(IsTransmitting));
                OnPropertyChanged(nameof(IsMuteBadgeVisible));
                OnPropertyChanged(nameof(IsDeafenBadgeVisible));
                if (!suppressLocalMuteStateNotification)
                {
                    voiceSessionService.SetLocalMuteState(IsMuted, IsDeafened);
                }
                RefreshObsOverlayService();
            }
        }
    }

    public bool IsDeafened
    {
        get => isDeafened;
        private set
        {
            if (SetProperty(ref isDeafened, value))
            {
                voicePlaybackService.SetDeafened(value);
                OnPropertyChanged(nameof(IsTransmitting));
                OnPropertyChanged(nameof(IsMuteBadgeVisible));
                OnPropertyChanged(nameof(IsDeafenBadgeVisible));
                if (!suppressLocalMuteStateNotification)
                {
                    voiceSessionService.SetLocalMuteState(IsMuted, IsDeafened);
                }
                RefreshObsOverlayService();
            }
        }
    }

    public bool IsPushToTalkHeld
    {
        get => isPushToTalkHeld;
        private set
        {
            if (SetProperty(ref isPushToTalkHeld, value))
            {
                OnPropertyChanged(nameof(IsTransmitting));
            }
        }
    }

    public bool IsImpostorRadioHeld
    {
        get => isImpostorRadioHeld;
        private set => SetProperty(ref isImpostorRadioHeld, value);
    }

    public bool IsTransmitting => !IsMuted && !IsDeafened && Settings.PushToTalkMode switch
    {
        PushToTalkMode.PushToTalk => IsPushToTalkHeld,
        PushToTalkMode.PushToMute => !IsPushToTalkHeld,
        _ => true
    };

    public bool IsMuteBadgeVisible => IsMuted && !IsDeafened;

    public bool IsDeafenBadgeVisible => IsDeafened;

    public bool LocalIsTalking
    {
        get => localIsTalking;
        private set
        {
            if (SetProperty(ref localIsTalking, value))
            {
                OnPropertyChanged(nameof(LocalRingBrush));
                OnPropertyChanged(nameof(LocalRingThickness));
                OnPropertyChanged(nameof(LocalRingGlowOpacity));
                OnPropertyChanged(nameof(IsLocalVisibleInGameOverlay));
                OnPropertyChanged(nameof(IsOverlayPanelBackgroundVisible));
                OnPropertyChanged(nameof(OverlayPanelBackground));
                OnPropertyChanged(nameof(OverlayPanelPadding));
                var localClientId = lastAmongUsState?.Players.FirstOrDefault(static player => player.IsLocal)?.ClientId;
                if (localClientId is not null)
                {
                    var meetingPlayer = MeetingOverlayPlayers.FirstOrDefault(player => player.ClientId == localClientId.Value);
                    if (meetingPlayer is not null)
                    {
                        meetingPlayer.IsTalking = value;
                    }
                }

                RefreshObsOverlayService();
            }
        }
    }

    public Brush LocalRingBrush => LocalIsTalking
        ? new SolidColorBrush(Color.FromRgb(0x32, 0xFF, 0x7E))
        : new SolidColorBrush(Color.FromRgb(0x3C, 0x37, 0x46));

    public double LocalRingThickness => LocalIsTalking ? 3.5 : 1.3;

    public double LocalRingGlowOpacity => LocalIsTalking ? 0.9 : 0;

    public string WaitingTitle
    {
        get => waitingTitle;
        private set => SetProperty(ref waitingTitle, value);
    }

    public string InstalledModLabel
    {
        get => installedModLabel;
        private set => SetProperty(ref installedModLabel, value);
    }

    public bool IsSettingsOpen
    {
        get => isSettingsOpen;
        set => SetProperty(ref isSettingsOpen, value);
    }

    public bool IsLaunchPlatformMenuOpen
    {
        get => isLaunchPlatformMenuOpen;
        set => SetProperty(ref isLaunchPlatformMenuOpen, value);
    }

    public bool IsVoiceServerSettingsOpen
    {
        get => isVoiceServerSettingsOpen;
        private set => SetProperty(ref isVoiceServerSettingsOpen, value);
    }

    public string VoiceServerUrl
    {
        get => voiceServerUrl;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref voiceServerUrl, next))
            {
                IsVoiceServerUrlValid = ValidateServerUrl(next);
                OnPropertyChanged(nameof(CanRemoveVoiceServer));
            }
        }
    }

    public string SelectedVoiceServerUrl
    {
        get => selectedVoiceServerUrl;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref selectedVoiceServerUrl, next) && !string.IsNullOrWhiteSpace(next))
            {
                VoiceServerUrl = next;
            }
        }
    }

    public bool IsVoiceServerUrlValid
    {
        get => isVoiceServerUrlValid;
        private set => SetProperty(ref isVoiceServerUrlValid, value);
    }

    public bool CanRemoveVoiceServer => VoiceServerUrls.Count > 1 && VoiceServerUrls.Contains(NormalizeServerUrl(VoiceServerUrl));

    public string PushToTalkShortcut
    {
        get => Settings.PushToTalkShortcut;
        set => UpdateShortcut(nameof(AppSettings.PushToTalkShortcut), value);
    }

    public string MuteShortcut
    {
        get => Settings.MuteShortcut;
        set => UpdateShortcut(nameof(AppSettings.MuteShortcut), value);
    }

    public string DeafenShortcut
    {
        get => Settings.DeafenShortcut;
        set => UpdateShortcut(nameof(AppSettings.DeafenShortcut), value);
    }

    public string ImpostorRadioShortcut
    {
        get => Settings.ImpostorRadioShortcut;
        set => UpdateShortcut(nameof(AppSettings.ImpostorRadioShortcut), value);
    }

    public void UpdateShortcut(string settingName, string shortcut)
    {
        var normalizedShortcut = NormalizeShortcutValue(shortcut);
        var currentShortcut = settingName switch
        {
            nameof(AppSettings.PushToTalkShortcut) => Settings.PushToTalkShortcut,
            nameof(AppSettings.MuteShortcut) => Settings.MuteShortcut,
            nameof(AppSettings.DeafenShortcut) => Settings.DeafenShortcut,
            nameof(AppSettings.ImpostorRadioShortcut) => Settings.ImpostorRadioShortcut,
            _ => string.Empty
        };
        if (currentShortcut.Equals(normalizedShortcut, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearDuplicateShortcut(settingName, normalizedShortcut);

        switch (settingName)
        {
            case nameof(AppSettings.PushToTalkShortcut):
                Settings.PushToTalkShortcut = normalizedShortcut;
                break;
            case nameof(AppSettings.MuteShortcut):
                Settings.MuteShortcut = normalizedShortcut;
                break;
            case nameof(AppSettings.DeafenShortcut):
                Settings.DeafenShortcut = normalizedShortcut;
                break;
            case nameof(AppSettings.ImpostorRadioShortcut):
                Settings.ImpostorRadioShortcut = normalizedShortcut;
                break;
            default:
                return;
        }

        ConfigureHotkeys();
        _ = settingsService.SaveAsync(Settings);
        OnPropertyChanged(nameof(PushToTalkShortcut));
        OnPropertyChanged(nameof(MuteShortcut));
        OnPropertyChanged(nameof(DeafenShortcut));
        OnPropertyChanged(nameof(ImpostorRadioShortcut));
        OnPropertyChanged(nameof(Settings));
    }

    private static string NormalizeShortcutValue(string shortcut)
    {
        return string.IsNullOrWhiteSpace(shortcut) ? "Disabled" : shortcut.Trim();
    }

    private void ClearDuplicateShortcut(string settingName, string shortcut)
    {
        if (shortcut.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!settingName.Equals(nameof(AppSettings.PushToTalkShortcut), StringComparison.Ordinal) &&
            Settings.PushToTalkShortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase))
        {
            Settings.PushToTalkShortcut = "Disabled";
        }

        if (!settingName.Equals(nameof(AppSettings.MuteShortcut), StringComparison.Ordinal) &&
            Settings.MuteShortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase))
        {
            Settings.MuteShortcut = "Disabled";
        }

        if (!settingName.Equals(nameof(AppSettings.DeafenShortcut), StringComparison.Ordinal) &&
            Settings.DeafenShortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase))
        {
            Settings.DeafenShortcut = "Disabled";
        }

        if (!settingName.Equals(nameof(AppSettings.ImpostorRadioShortcut), StringComparison.Ordinal) &&
            Settings.ImpostorRadioShortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase))
        {
            Settings.ImpostorRadioShortcut = "Disabled";
        }
    }

    public ICommand LoadCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand LaunchGameCommand { get; }

    public ICommand ToggleConnectionCommand { get; }

    public ICommand RefreshAudioDevicesCommand { get; }

    public ICommand TestSpeakerCommand { get; }

    public ICommand TestVoiceEffectCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    public ICommand ToggleDeafenCommand { get; }

    public ICommand ToggleSettingsCommand { get; }

    public ICommand ToggleLaunchPlatformMenuCommand { get; }

    public ICommand SelectLaunchPlatformCommand { get; }

    public ICommand BrowseCustomLaunchPathCommand { get; }

    public ICommand OpenVoiceServerSettingsCommand { get; }

    public ICommand CloseVoiceServerSettingsCommand { get; }

    public ICommand SaveVoiceServerSettingsCommand { get; }

    public ICommand ResetVoiceServerCommand { get; }

    public ICommand RemoveVoiceServerCommand { get; }

    public ICommand CopyObsOverlayUrlCommand { get; }

    public ICommand RegenerateObsSecretCommand { get; }

    public ICommand ResetSettingsCommand { get; }

    public ICommand OpenGitHubCommand { get; }

    public ICommand OpenDiscordCommand { get; }

    public ICommand CloseCommand { get; }

    private async Task LoadAsync()
    {
        if (hasLoaded)
        {
            await RefreshFromCurrentGameStateAsync();
            return;
        }

        hasLoaded = true;
        try
        {
            IsBusy = true;
            Settings = await settingsService.LoadAsync();
            ApplyLanguageDefaults();
            RefreshVoiceServerUrls();
            StatusMessage = $"設定を読み込みました: {settingsService.SettingsPath}";
            await cosmeticCatalogService.InitializeAsync();
            RefreshAudioDevices();
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            voicePlaybackService.SetSpeaker(Settings.Speaker);
            ConfigureHotkeys();
            hotkeyService.Start();
            RefreshObsOverlayService();
            amongUsProcessService.Start();
            amongUsMemoryReaderService.Start();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshFromCurrentGameStateAsync()
    {
        amongUsProcessService.Start();
        amongUsMemoryReaderService.Start();
        remoteAudioConnectedPlayerKeys.Clear();

        if (lastAmongUsState is not null)
        {
            UpdateOtherPlayers(lastAmongUsState);
            UpdateMeetingOverlayPlayers(lastAmongUsState);
            QueueVoiceStateUpdate(lastAmongUsState);
            voicePlaybackService.ApplyMix(voiceSessionService.Peers);
        }

        RefreshConnectionState();
        RefreshObsOverlayService();
        StatusMessage = "Among Us の状態を再読み込みしました。";
        _ = ReconnectPeersInBackgroundAsync();
        await Task.CompletedTask;
    }

    private async Task ReconnectPeersInBackgroundAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await voiceSessionService.ReconnectPeersAsync(timeout.Token);
            Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshConnectionState();
                StatusMessage = "音声接続を再確認しました。";
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                StatusMessage = $"音声接続の再確認に失敗しました: {ex.Message}";
            });
        }
    }

    private void QueueVoiceStateUpdate(AmongUsState state)
    {
        _ = UpdateVoiceStateAsync(state);
    }

    private async Task UpdateVoiceStateAsync(AmongUsState state)
    {
        try
        {
            await voiceSessionService.UpdateGameStateAsync(state, Settings);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                StatusMessage = $"ボイスサーバー接続エラー: {ex.Message}";
                RefreshConnectionState();
            });
        }
    }

    private void ApplyLanguageDefaults()
    {
        if (string.IsNullOrWhiteSpace(Settings.Language))
        {
            Settings.Language = "ja";
        }

    }

    private async Task SaveAsync()
    {
        pendingSettingsSave?.Cancel();
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        ConfigureHotkeys();
        RestartVoiceCapture();
        voicePlaybackService.SetSpeaker(Settings.Speaker);
        if (lastAmongUsState is not null)
        {
            QueueVoiceStateUpdate(lastAmongUsState);
        }

        if (lastAmongUsState?.IsHost == true)
        {
            voiceSessionService.BroadcastLobbySettings(Settings);
        }

        OnPropertyChanged(nameof(DisplayLobbyCode));
        RefreshObsOverlayService();
        StatusMessage = "設定を保存しました。";
    }

    private void QueueSettingsSave()
    {
        var previousSave = pendingSettingsSave;
        var currentSave = new CancellationTokenSource();
        pendingSettingsSave = currentSave;
        previousSave?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, currentSave.Token);
                await settingsService.SaveAsync(Settings);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusMessage = $"設定の自動保存に失敗しました: {ex.Message}";
                });
            }
            finally
            {
                if (ReferenceEquals(pendingSettingsSave, currentSave))
                {
                    pendingSettingsSave = null;
                }

                currentSave.Dispose();
            }
        });
    }

    private void OpenVoiceServerSettings()
    {
        RefreshVoiceServerUrls();
        VoiceServerUrl = Settings.ServerUrl;
        SelectedVoiceServerUrl = VoiceServerUrls.Contains(NormalizeServerUrl(Settings.ServerUrl))
            ? NormalizeServerUrl(Settings.ServerUrl)
            : string.Empty;
        IsVoiceServerUrlValid = ValidateServerUrl(VoiceServerUrl);
        IsVoiceServerSettingsOpen = true;
    }

    private async Task SaveVoiceServerSettingsAsync()
    {
        var normalized = NormalizeServerUrl(VoiceServerUrl);
        if (!ValidateServerUrl(normalized))
        {
            IsVoiceServerUrlValid = false;
            StatusMessage = "ボイスサーバー URL が正しくありません。";
            return;
        }

        Settings.ServerUrl = normalized;
        Settings.ServerUrls = NormalizeServerUrls([normalized, ..VoiceServerUrls]);
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        OnPropertyChanged(nameof(ObsOverlayUrl));
        OnPropertyChanged(nameof(ObsOverlayStatus));
        IsVoiceServerSettingsOpen = false;
        StatusMessage = $"ボイスサーバーを変更しました: {Settings.ServerUrl}";
        QueueVoiceServerReconnect();
    }

    private async Task ResetVoiceServerAsync()
    {
        VoiceServerUrl = "https://bettercrewl.ink";
        Settings.ServerUrl = VoiceServerUrl;
        Settings.ServerUrls = [VoiceServerUrl];
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        OnPropertyChanged(nameof(ObsOverlayUrl));
        OnPropertyChanged(nameof(ObsOverlayStatus));
        IsVoiceServerSettingsOpen = false;
        StatusMessage = "ボイスサーバーをデフォルトに戻しました。";
        QueueVoiceServerReconnect();
    }

    private async Task RemoveVoiceServerAsync()
    {
        var normalized = NormalizeServerUrl(VoiceServerUrl);
        var nextUrls = VoiceServerUrls
            .Where(url => !string.Equals(url, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nextUrls.Count == 0)
        {
            nextUrls.Add("https://bettercrewl.ink");
        }

        Settings.ServerUrls = nextUrls;
        Settings.ServerUrl = nextUrls[0];
        VoiceServerUrl = Settings.ServerUrl;
        await settingsService.SaveAsync(Settings);
        RefreshVoiceServerUrls();
        OnPropertyChanged(nameof(ObsOverlayUrl));
        OnPropertyChanged(nameof(ObsOverlayStatus));
        StatusMessage = "保存済みボイスサーバーを削除しました。";
        QueueVoiceServerReconnect();
    }

    private async Task RegenerateObsSecretAsync()
    {
        Settings.ObsSecret = SettingsService.GenerateObsSecret();
        await settingsService.SaveAsync(Settings);
        RefreshObsOverlayService();
        StatusMessage = "OBSオーバーレイURLを再生成しました。OBS側のURLも貼り替えてください。";
    }

    private async Task ResetSettingsAsync()
    {
        var result = MessageBox.Show(
            "設定をデフォルトに戻しますか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Settings = new AppSettings();
        RefreshVoiceServerUrls();
        RefreshAudioDevices();
        RestartMicrophoneMonitor();
        RestartVoiceCapture();
        voicePlaybackService.SetSpeaker(Settings.Speaker);
        ConfigureHotkeys();
        await settingsService.SaveAsync(Settings);
        RefreshObsOverlayService();
        StatusMessage = "設定をデフォルトに戻しました。";
        QueueVoiceServerReconnect();
    }

    private void RefreshVoiceServerUrls()
    {
        var urls = NormalizeServerUrls(Settings.ServerUrls.Count > 0 ? Settings.ServerUrls : [Settings.ServerUrl]);
        if (!urls.Contains(NormalizeServerUrl(Settings.ServerUrl), StringComparer.OrdinalIgnoreCase) &&
            ValidateServerUrl(Settings.ServerUrl))
        {
            urls.Insert(0, NormalizeServerUrl(Settings.ServerUrl));
        }

        if (urls.Count == 0)
        {
            urls.Add("https://bettercrewl.ink");
        }

        Settings.ServerUrls = urls;
        VoiceServerUrls.Clear();
        foreach (var url in urls)
        {
            VoiceServerUrls.Add(url);
        }

        OnPropertyChanged(nameof(CanRemoveVoiceServer));
    }

    private void QueueVoiceServerReconnect()
    {
        if (!ShouldReconnectVoiceServer())
        {
            StatusMessage = $"ボイスサーバーを保存しました: {Settings.ServerUrl}";
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await voiceSessionService.DisconnectAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var connectTask = voiceSessionService.ConnectAsync(Settings.ServerUrl, CurrentLobbyCode, timeout.Token);
                var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(6)));
                if (completed != connectTask)
                {
                    throw new TimeoutException("接続がタイムアウトしました。");
                }

                await connectTask;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RefreshConnectionState();
                    StatusMessage = $"ボイスサーバーへ再接続しました: {Settings.ServerUrl}";
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusMessage = $"ボイスサーバーへの再接続に失敗しました: {ex.Message}";
                });
            }
        });
    }

    private bool ShouldReconnectVoiceServer()
    {
        return voiceSessionService.State == VoiceSessionState.Connected &&
               IsInGameSession &&
               !string.IsNullOrWhiteSpace(CurrentLobbyCode) &&
               !string.Equals(CurrentLobbyCode, "MENU", StringComparison.OrdinalIgnoreCase);
    }

    private Task LaunchGameAsync()
    {
        try
        {
            gameLaunchService.Launch(Settings.LaunchPlatform, Settings.CustomPlatforms);
            StatusMessage = $"{Settings.LaunchPlatform} 版 Among Us を起動しました。";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "起動エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    private void BrowseCustomLaunchPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Among Us の実行ファイルを選択",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CustomLaunchPath = dialog.FileName;
        SelectedPlatform = GamePlatform.Custom;
    }

    private GamePlatformInstance GetCustomLaunchPlatform()
    {
        const string key = "custom";
        if (!Settings.CustomPlatforms.TryGetValue(key, out var custom))
        {
            custom = new GamePlatformInstance
            {
                Default = false,
                Key = "CUSTOM",
                LaunchType = PlatformRunType.Exe,
                TranslateKey = "platform.custom"
            };
            Settings.CustomPlatforms[key] = custom;
        }

        custom.LaunchType = PlatformRunType.Exe;
        custom.Key = string.IsNullOrWhiteSpace(custom.Key) ? "CUSTOM" : custom.Key;
        return custom;
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await voiceSessionService.DisconnectAsync();
            RefreshConnectionState();
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "ボイスサーバーへ接続しています。";
            await voiceSessionService.ConnectAsync(Settings.ServerUrl, LobbyCode);
            StatusMessage = voiceSessionService.StatusText;
        }
        catch (Exception ex)
        {
            await voiceSessionService.DisconnectAsync();
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "接続エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            RefreshConnectionState();
        }
    }

    private void RefreshConnectionState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionButtonText));
        StatusMessage = voiceSessionService.StatusText;
    }

    private void RefreshVoiceMixFromSettings()
    {
        var state = voiceSessionService.CurrentState ?? lastAmongUsState;
        if (state is null)
        {
            return;
        }

        QueueVoiceStateUpdate(state);
        voicePlaybackService.ApplyMix(voiceSessionService.Peers);
        if (lastAmongUsState is not null)
        {
            UpdateOtherPlayers(state);
        }

        RefreshObsOverlayService();
    }

    private void RefreshLobbySettingsFromSettings()
    {
        OnEffectiveLobbySettingsChanged();
        var state = voiceSessionService.CurrentState ?? lastAmongUsState;
        if (state is null)
        {
            return;
        }

        QueueVoiceStateUpdate(state);
        if (state.IsHost)
        {
            voiceSessionService.BroadcastLobbySettings(Settings);
        }

        voicePlaybackService.ApplyMix(voiceSessionService.Peers);
        UpdateOtherPlayers(state);
        RefreshObsOverlayService();
    }

    private void RefreshAudioDevices()
    {
        Microphones.Clear();
        foreach (var device in audioDeviceService.GetMicrophones())
        {
            Microphones.Add(device);
        }

        Speakers.Clear();
        foreach (var device in audioDeviceService.GetSpeakers())
        {
            Speakers.Add(device);
        }
    }

    private async Task TestSpeakerAsync()
    {
        try
        {
            await audioDeviceService.PlaySpeakerTestAsync(Settings.Speaker, Settings.MasterVolume);
        }
        catch (Exception ex)
        {
            StatusMessage = $"スピーカーテストに失敗しました: {ex.Message}";
        }
    }

    private void RestartMicrophoneMonitor()
    {
        try
        {
            audioDeviceService.StartMicrophoneMonitor(Settings.Microphone);
            IsMicrophoneMonitorRunning = true;
        }
        catch (Exception ex)
        {
            IsMicrophoneMonitorRunning = false;
            MicrophoneLevel = 0;
            StatusMessage = $"マイク入力を開始できませんでした: {ex.Message}";
        }
    }

    private void RestartVoiceCapture()
    {
        if (IsVoiceEffectTestRunning)
        {
            return;
        }

        try
        {
            voiceCaptureService.Start(
                Settings.Microphone,
                Settings.MicrophoneGain,
                Settings.MicrophoneGainEnabled,
                Settings.NoiseSuppression,
                Settings.EchoCancellation);
            var talking = ShouldTransmitVoice();
            LocalIsTalking = talking;
            voiceCaptureService.SetTransmitting(talking);
        }
        catch (Exception ex)
        {
            StatusMessage = $"通話用マイク入力を開始できませんでした: {ex.Message}";
        }
    }

    private void ToggleVoiceEffectTest()
    {
        try
        {
            if (IsVoiceEffectTestRunning)
            {
                audioDeviceService.StopVoiceEffectTest();
                IsVoiceEffectTestRunning = false;
                RestartMicrophoneMonitor();
                RestartVoiceCapture();
                return;
            }

            audioDeviceService.StopMicrophoneMonitor();
            voiceCaptureService.Stop();
            IsMicrophoneMonitorRunning = false;
            audioDeviceService.StartVoiceEffectTest(
                Settings.Microphone,
                Settings.Speaker,
                Settings.VoiceEffectStrength,
                Settings.MasterVolume);
            IsVoiceEffectTestRunning = true;
        }
        catch (Exception ex)
        {
            audioDeviceService.StopVoiceEffectTest();
            IsVoiceEffectTestRunning = false;
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            StatusMessage = $"ボイスエフェクトテストに失敗しました: {ex.Message}";
        }
    }

    private bool RestartVoiceEffectTestIfRunning()
    {
        if (!IsVoiceEffectTestRunning)
        {
            return false;
        }

        try
        {
            audioDeviceService.StopVoiceEffectTest();
            audioDeviceService.StartVoiceEffectTest(
                Settings.Microphone,
                Settings.Speaker,
                Settings.VoiceEffectStrength,
                Settings.MasterVolume);
        }
        catch (Exception ex)
        {
            audioDeviceService.StopVoiceEffectTest();
            IsVoiceEffectTestRunning = false;
            RestartMicrophoneMonitor();
            RestartVoiceCapture();
            StatusMessage = $"ボイスエフェクトテストに失敗しました: {ex.Message}";
        }

        return true;
    }

    private void ToggleMute()
    {
        if (IsDeafened)
        {
            SetLocalMuteButtons(muted: false, deafened: false);
            _ = RefreshVadAsync(force: true);
            return;
        }

        SetLocalMuteButtons(!IsMuted, deafened: false);
        if (IsMuted)
        {
            IsPushToTalkHeld = false;
        }

        _ = RefreshVadAsync(force: true);
    }

    private void ToggleDeafen()
    {
        if (IsDeafened)
        {
            SetLocalMuteButtons(muted: false, deafened: false);
        }
        else
        {
            SetLocalMuteButtons(muted: true, deafened: true);
            IsPushToTalkHeld = false;
        }

        _ = RefreshVadAsync(force: true);
    }

    private void SetLocalMuteButtons(bool muted, bool deafened)
    {
        suppressLocalMuteStateNotification = true;
        try
        {
            IsMuted = muted || deafened;
            IsDeafened = deafened;
        }
        finally
        {
            suppressLocalMuteStateNotification = false;
        }

        voiceSessionService.SetLocalMuteState(IsMuted, IsDeafened);
        RefreshObsOverlayService();
    }

    private void OnMicrophoneLevelChanged(object? sender, double level)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MicrophoneLevel = level;
            _ = RefreshVadAsync();
        });
    }

    private void OnVoiceFrameCaptured(object? sender, VoiceAudioFrame frame)
    {
        capturedVoiceFrames++;
        if (frame.IsTransmitting)
        {
            transmittedVoiceFrames++;
        }

        voiceSessionService.QueueLocalAudioFrame(frame);

        if ((capturedVoiceFrames % 50) == 0)
        {
            Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(VoiceCaptureStatus)));
        }
    }

    private void ConfigureHotkeys()
    {
        hotkeyService.Configure(
            Settings.PushToTalkShortcut,
            Settings.MuteShortcut,
            Settings.DeafenShortcut,
            Settings.ImpostorRadioShortcut);
    }

    private void OnHotkeyChanged(object? sender, HotkeyEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!AmongUsProcessService.IsAmongUsForeground())
            {
                ReleaseHeldHotkeyState(e);
                return;
            }

            switch (e.Action)
            {
                case "push-to-talk":
                    IsPushToTalkHeld = e.IsPressed;
                    _ = RefreshVadAsync();
                    break;
                case "impostor-radio":
                    SetImpostorRadioHeld(e.IsPressed);
                    break;
                case "mute" when e.IsPressed:
                    ToggleMute();
                    break;
                case "deafen" when e.IsPressed:
                    ToggleDeafen();
                    break;
            }
        });
    }

    private void ReleaseHeldHotkeyState(HotkeyEventArgs e)
    {
        if (e.IsPressed)
        {
            return;
        }

        switch (e.Action)
        {
            case "push-to-talk":
                IsPushToTalkHeld = false;
                _ = RefreshVadAsync();
                break;
            case "impostor-radio":
                SetImpostorRadioHeld(false);
                break;
        }
    }

    private async Task RefreshVadAsync(bool force = false)
    {
        var talking = ShouldTransmitVoice();
        LocalIsTalking = talking;
        voiceCaptureService.SetTransmitting(talking);
        if (!force && talking == lastTalkingState)
        {
            return;
        }

        lastTalkingState = talking;
        await voiceSessionService.EmitVadAsync(talking);
    }

    private bool ShouldTransmitVoice()
    {
        var threshold = Settings.MicSensitivityEnabled ? Settings.MicSensitivity * 100d : 1.5d;
        return IsTransmitting && (!Settings.VadEnabled || MicrophoneLevel >= threshold);
    }

    private void SetImpostorRadioHeld(bool isPressed)
    {
        var localPlayer = lastAmongUsState?.Players.FirstOrDefault(static player => player.IsLocal);
        var canUseRadio = Settings.LocalLobbySettings.ImpostorRadioEnabled &&
                          localPlayer?.IsImpostor == true &&
                          lastAmongUsState?.GameState == GameState.Tasks;

        IsImpostorRadioHeld = isPressed && canUseRadio;
        voiceSessionService.SetLocalImpostorRadio(IsImpostorRadioHeld);
        StatusMessage = IsImpostorRadioHeld ? "インポスターラジオを使用中です。" : StatusMessage;
    }

    private void OnAmongUsProcessChanged(object? sender, AmongUsProcessInfo? processInfo)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsGameOpen = processInfo is not null;
            OnPropertyChanged(nameof(ShowWaitingView));
            OnPropertyChanged(nameof(ShowMenuView));
            OnPropertyChanged(nameof(ShowVoiceView));
            WaitingTitle = "Among Usの待機中";
            InstalledModLabel = processInfo?.InstalledMod.Label ?? "None";
            currentMod = processInfo?.InstalledMod.Id ?? AmongUsModType.None;
            amongUsMemoryReaderService.SetProcess(processInfo);
            if (processInfo is null)
            {
                IsInGameSession = false;
                CurrentLobbyCode = "MENU";
                lastAmongUsState = null;
                OnPropertyChanged(nameof(IsDiscussionView));
                voicePlaybackService.Clear();
                OnPropertyChanged(nameof(CanChangeLobbySettings));
                OnPropertyChanged(nameof(LobbySettingsModeText));
                OnEffectiveLobbySettingsChanged();
                RefreshObsOverlayService();
            }
            StatusMessage = processInfo is null
                ? "Among Us を起動してください。"
                : "Among Us を検知しました。";
            DiagnosticMessage = processInfo is null
                ? string.Empty
                : $"pid={processInfo.ProcessId} | x64={processInfo.Is64Bit} | mod={processInfo.InstalledMod.Label}";
        });
    }

    private void OnAmongUsDiagnosticChanged(object? sender, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            DiagnosticMessage = message;
        });
    }

    private void OnAmongUsStateChanged(object? sender, AmongUsState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            lastAmongUsState = state;
            IsInGameSession = state.GameState is GameState.Lobby or GameState.Tasks or GameState.Discussion;
            OnPropertyChanged(nameof(IsDiscussionView));
            OnPropertyChanged(nameof(CanChangeLobbySettings));
            OnPropertyChanged(nameof(LobbySettingsModeText));
            OnEffectiveLobbySettingsChanged();
            CurrentLobbyCode = state.LobbyCode;
            var localPlayer = state.Players.FirstOrDefault(static player => player.IsLocal);
            if (localPlayer is not null)
            {
                LocalPlayerName = localPlayer.Name;
                var colors = PlayerColorPalette.Get(localPlayer.ColorId, state.PlayerColors);
                LocalPlayerBrush = new SolidColorBrush(colors.Main);
                LocalPlayerShadowBrush = new SolidColorBrush(colors.Shadow);
                LocalAvatarImage = avatarImageService.GetPlayerImage(colors.Main, colors.Shadow);
                var cosmetics = cosmeticCatalogService.GetImages(localPlayer, currentMod);
                LocalHatFrontImage = cosmetics.HatFront;
                LocalHatBackImage = cosmetics.HatBack;
                LocalSkinImage = cosmetics.Skin;
                LocalVisorImage = cosmetics.Visor;
                LocalHatMargin = ToBodyCosmeticPlacementMargin(cosmetics.Hat, 78);
                LocalSkinMargin = ToSkinPlacementMargin(cosmetics.SkinPlacement, 78);
                LocalVisorMargin = ToPlacementMargin(cosmetics.VisorPlacement, 78);
                LocalHatWidth = ToBodyCosmeticPlacementWidth(cosmetics.Hat, 78);
                LocalSkinWidth = ToSkinPlacementWidth(cosmetics.SkinPlacement, 78);
                LocalVisorWidth = ToPlacementWidth(cosmetics.VisorPlacement, 78);
            }

            UpdateOtherPlayers(state);
            UpdateMeetingOverlayPlayers(state);
            if (IsImpostorRadioHeld)
            {
                SetImpostorRadioHeld(true);
            }

            RefreshObsOverlayService();
            QueueVoiceStateUpdate(state);
        });
    }

    private void OnVoiceSessionStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshConnectionState();
            if (voiceSessionService.State != VoiceSessionState.Connected)
            {
                remoteAudioConnectedPlayerKeys.Clear();
            }

            voicePlaybackService.ApplyMix(voiceSessionService.Peers);
            OnPropertyChanged(nameof(PeerConnectionStatus));
            OnPropertyChanged(nameof(CanChangeLobbySettings));
            OnPropertyChanged(nameof(LobbySettingsModeText));
            OnEffectiveLobbySettingsChanged();
            var effectiveState = voiceSessionService.CurrentState ?? lastAmongUsState;
            if (effectiveState is not null)
            {
                UpdateOtherPlayers(effectiveState);
                UpdateMeetingOverlayPlayers(effectiveState);
            }
            RefreshObsOverlayService();
        });
    }

    private void OnVoiceDebugLogChanged(object? sender, string log)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            VoiceDebugLog = log;
        });
    }

    private void OnVoiceSessionError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = error;
        });
    }

    private void RefreshObsOverlayService()
    {
        if (!Settings.ObsOverlay)
        {
            obsOverlayService.Stop();
            obsOverlayService.UpdateSnapshot(ObsOverlaySnapshot.Empty);
            OnPropertyChanged(nameof(ObsOverlayUrl));
            OnPropertyChanged(nameof(ObsOverlayStatus));
            return;
        }

        try
        {
            if (!obsOverlayService.IsRunning)
            {
                obsOverlayService.Start(Settings.ObsSecret ?? string.Empty);
            }

            obsOverlayService.UpdateSnapshot(CreateObsOverlaySnapshot());
            OnPropertyChanged(nameof(ObsOverlayUrl));
            OnPropertyChanged(nameof(ObsOverlayStatus));
        }
        catch (Exception ex)
        {
            StatusMessage = $"OBSオーバーレイを開始できませんでした: {ex.Message}";
            OnPropertyChanged(nameof(ObsOverlayUrl));
            OnPropertyChanged(nameof(ObsOverlayStatus));
        }
    }

    private ObsOverlaySnapshot CreateObsOverlaySnapshot()
    {
        var visible = Settings.EnableOverlay &&
                      ShowVoiceView &&
                      (Settings.MeetingOverlay || !IsDiscussionView);

        return new ObsOverlaySnapshot(
            visible,
            IsDiscussionView,
            Settings.MeetingOverlay,
            Settings.CompactOverlay,
            OverlayPosition,
            new ObsOverlayLocalPlayer(
                LocalPlayerName,
                DisplayLobbyCode,
                BrushToHex(LocalPlayerBrush, "#50EF39"),
                BrushToHex(LocalPlayerShadowBrush, "#15A742"),
                LocalIsTalking,
                IsMuted,
                IsDeafened,
                LocalHatBackImage,
                LocalHatFrontImage,
                LocalSkinImage,
                LocalVisorImage,
                ToObsCosmetic(LocalHatMargin, LocalHatWidth, 78),
                ToObsCosmetic(LocalSkinMargin, LocalSkinWidth, 78),
                ToObsCosmetic(LocalVisorMargin, LocalVisorWidth, 78)),
            OtherPlayers
                .Where(static player => player.HasVoicePeer && player.IsPeerConnectionReady)
                .Select(static player => new ObsOverlayPlayer(
                player.Name,
                BrushToHex(player.MainBrush, "#50EF39"),
                BrushToHex(player.ShadowBrush, "#15A742"),
                player.IsTalking,
                player.IsUsingRadio,
                player.IsAudible,
                player.IsRemoteMuted || player.IsPlayerMuted,
                player.ConnectionWarningVisible,
                player.ConnectionWarningText,
                player.ConnectionWarningIconPath,
                BrushToHex(player.ConnectionWarningBackground, "#EA3C2A"),
                BrushToHex(player.ConnectionWarningBorder, "#690A00"),
                player.HatBackImage,
                player.HatFrontImage,
                player.SkinImage,
                player.VisorImage,
                ToObsCosmetic(player.HatMargin, player.HatWidth, 38),
                ToObsCosmetic(player.SkinMargin, player.SkinWidth, 38),
                ToObsCosmetic(player.VisorMargin, player.VisorWidth, 38))).ToArray(),
            MeetingOverlayPlayers
                .Select(static player => new ObsOverlayMeetingPlayer(
                    player.PlayerId,
                    BrushToHex(player.GlowBrush, "#32FF7E"),
                    player.IsTalking))
                .ToArray());
    }

    private static ObsOverlayCosmetic ToObsCosmetic(Thickness margin, double width, double baseSize)
    {
        return new ObsOverlayCosmetic(
            Math.Round((margin.Left / baseSize) * 100d, 3),
            Math.Round((margin.Top / baseSize) * 100d, 3),
            Math.Round((width / baseSize) * 100d, 3));
    }

    private static string BrushToHex(Brush brush, string fallback)
    {
        if (brush is not SolidColorBrush solidColorBrush)
        {
            return fallback;
        }

        var color = solidColorBrush.Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void SetEffectiveLobbyBool(bool value, Func<LobbySettings, bool> getter, Action<LobbySettings, bool> setter, string propertyName)
    {
        if (!CanChangeLobbySettings || getter(Settings.LocalLobbySettings) == value)
        {
            return;
        }

        setter(Settings.LocalLobbySettings, value);
        OnPropertyChanged(propertyName);
        RefreshLobbySettingsFromSettings();
        QueueSettingsSave();
    }

    private void OnEffectiveLobbySettingsChanged()
    {
        OnPropertyChanged(nameof(EffectiveMaxDistance));
        OnPropertyChanged(nameof(EffectiveWallsBlockAudio));
        OnPropertyChanged(nameof(EffectiveVisionHearing));
        OnPropertyChanged(nameof(EffectiveHaunting));
        OnPropertyChanged(nameof(EffectiveThirdPartyHaunting));
        OnPropertyChanged(nameof(EffectiveVoiceEffectEnabled));
        OnPropertyChanged(nameof(EffectiveHearImpostorsInVents));
        OnPropertyChanged(nameof(EffectiveImpostorsHearImpostorsInVent));
        OnPropertyChanged(nameof(EffectiveCommsSabotage));
        OnPropertyChanged(nameof(EffectiveHearThroughCameras));
        OnPropertyChanged(nameof(EffectiveImpostorRadioEnabled));
        OnPropertyChanged(nameof(EffectiveDeadOnly));
        OnPropertyChanged(nameof(EffectiveMeetingGhostOnly));
    }

    private void OnPeerDataPayloadQueued(object? sender, PeerDataPayloadEventArgs e)
    {
    }

    private void OnPeerAudioFrameQueued(object? sender, PeerAudioFrameEventArgs e)
    {
        queuedVoiceFrames++;
        if ((queuedVoiceFrames % 50) == 0)
        {
            Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(VoiceCaptureStatus)));
        }
    }

    private void OnPeerAudioFrameReceived(object? sender, PeerAudioFrameEventArgs e)
    {
        voicePlaybackService.SubmitFrame(e.SocketId, e.Frame);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var peer = voiceSessionService.Peers.FirstOrDefault(peer => peer.SocketId == e.SocketId);
            if (peer is null || (peer.PlayerId <= 0 && peer.ClientId <= 0))
            {
                return;
            }

            var playerKey = GetPlayerKey(peer.PlayerId, peer.ClientId);
            var wasNewAudioConnection = remoteAudioConnectedPlayerKeys.Add(playerKey);
            if (wasNewAudioConnection && lastAmongUsState is not null)
            {
                UpdateOtherPlayers(lastAmongUsState);
                UpdateMeetingOverlayPlayers(lastAmongUsState);
            }

            MarkRemoteTalking(playerKey, e.Frame.Level);
        });
    }

    private void UpdateOtherPlayers(AmongUsState state)
    {
        var existingTiles = OtherPlayers.ToDictionary(static player => player.ConfigKey, StringComparer.Ordinal);
        var nextTiles = new List<PlayerTileViewModel>();
        var peersByPlayerKey = voiceSessionService.Peers
            .Where(static peer => peer.PlayerId > 0 || peer.ClientId > 0)
            .GroupBy(static peer => GetPlayerKey(peer.PlayerId, peer.ClientId))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static peer => peer.PeerConnectionState == VoicePeerConnectionState.Connected)
                    .ThenByDescending(static peer => peer.IsTalking)
                    .ThenBy(static peer => peer.SocketId)
                    .First());
        var now = DateTimeOffset.UtcNow;

        foreach (var player in state.Players.Where(static player => !player.IsLocal && !player.Disconnected).OrderBy(static player => player.Id))
        {
            var playerKey = GetPlayerKey(player.Id, player.ClientId);
            peersByPlayerKey.TryGetValue(playerKey, out var peer);
            var hasRecentVoiceFrame = remoteTalkingUntil.TryGetValue(playerKey, out var activeUntil) && activeUntil > now;
            var isPeerConnectionReady =
                peer?.PeerConnectionState == VoicePeerConnectionState.Connected ||
                remoteAudioConnectedPlayerKeys.Contains(playerKey);
            var colors = PlayerColorPalette.Get(player.ColorId, state.PlayerColors);
            var playerConfigKey = player.NameHash != 0 ? player.NameHash.ToString() : player.Name;
            var playerConfig = Settings.PlayerConfigMap.TryGetValue(playerConfigKey, out var config) ? config : new SocketConfig();
            var cosmetics = cosmeticCatalogService.GetImages(player, currentMod);
            if (!existingTiles.TryGetValue(playerConfigKey, out var tile))
            {
                tile = new PlayerTileViewModel(
                    playerConfigKey,
                    player.Id,
                    player.ClientId,
                    player.Name,
                    new SolidColorBrush(colors.Main),
                    new SolidColorBrush(colors.Shadow),
                    avatarImageService.GetPlayerImage(colors.Main, colors.Shadow),
                    cosmetics,
                    player.IsDead,
                    hasRecentVoiceFrame,
                    peer?.IsUsingRadio == true,
                    peer?.IsMuted == true,
                    peer?.IsDeafened == true,
                    peer?.Mix?.IsAudible != false,
                    peer is not null,
                    isPeerConnectionReady,
                    Settings.CompactOverlay,
                    playerConfig,
                    OnPlayerTileConfigChanged);
            }
            else
            {
                tile.UpdateState(
                player.IsDead,
                hasRecentVoiceFrame,
                peer?.IsUsingRadio == true,
                peer?.IsMuted == true,
                peer?.IsDeafened == true,
                peer?.Mix?.IsAudible != false,
                peer is not null,
                isPeerConnectionReady,
                Settings.CompactOverlay,
                playerConfig);
            }

            nextTiles.Add(tile);
        }

        SynchronizeOtherPlayers(nextTiles);
        OnPropertyChanged(nameof(IsOverlayPanelBackgroundVisible));
        OnPropertyChanged(nameof(OverlayPanelBackground));
        OnPropertyChanged(nameof(OverlayPanelPadding));
    }

    private void SynchronizeOtherPlayers(IReadOnlyList<PlayerTileViewModel> nextTiles)
    {
        for (var index = 0; index < nextTiles.Count; index++)
        {
            var next = nextTiles[index];
            if (index < OtherPlayers.Count && ReferenceEquals(OtherPlayers[index], next))
            {
                continue;
            }

            var existingIndex = OtherPlayers.IndexOf(next);
            if (existingIndex >= 0)
            {
                OtherPlayers.Move(existingIndex, index);
            }
            else
            {
                OtherPlayers.Insert(index, next);
            }
        }

        while (OtherPlayers.Count > nextTiles.Count)
        {
            OtherPlayers.RemoveAt(OtherPlayers.Count - 1);
        }
    }

    private void UpdateMeetingOverlayPlayers(AmongUsState state)
    {
        MeetingOverlayPlayers.Clear();
        if (state.GameState != GameState.Discussion)
        {
            return;
        }

        var peersByPlayerKey = voiceSessionService.Peers
            .Where(static peer => peer.PlayerId > 0 || peer.ClientId > 0)
            .GroupBy(static peer => GetPlayerKey(peer.PlayerId, peer.ClientId))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static peer => peer.PeerConnectionState == VoicePeerConnectionState.Connected)
                    .ThenByDescending(static peer => peer.IsTalking)
                    .ThenBy(static peer => peer.SocketId)
                    .First());
        var now = DateTimeOffset.UtcNow;
        foreach (var player in state.Players
                     .Where(static player => !player.Disconnected)
                     .OrderBy(static player => player.IsDead)
                     .ThenBy(static player => player.Id))
        {
            var playerKey = GetPlayerKey(player.Id, player.ClientId);
            peersByPlayerKey.TryGetValue(playerKey, out var peer);
            var hasRecentVoiceFrame = remoteTalkingUntil.TryGetValue(playerKey, out var activeUntil) && activeUntil > now;
            var colors = PlayerColorPalette.Get(player.ColorId, state.PlayerColors);
            var talking = player.IsLocal ? LocalIsTalking : hasRecentVoiceFrame;
            MeetingOverlayPlayers.Add(new MeetingOverlayPlayerViewModel(
                player.Id,
                player.ClientId,
                new SolidColorBrush(colors.Main),
                talking));
        }
    }

    private void MarkRemoteTalking(int playerKey, double level)
    {
        if (level < RemoteTalkingLevelThreshold)
        {
            return;
        }

        remoteTalkingUntil[playerKey] = DateTimeOffset.UtcNow.AddMilliseconds(450);
        var meetingPlayer = MeetingOverlayPlayers.FirstOrDefault(player => GetPlayerKey(player.PlayerId, player.ClientId) == playerKey);
        if (meetingPlayer is not null)
        {
            meetingPlayer.IsTalking = true;
        }

        var tile = OtherPlayers.FirstOrDefault(player => GetPlayerKey(player.PlayerId, player.ClientId) == playerKey);
        if (tile is null)
        {
            RefreshObsOverlayService();
            return;
        }

        tile.IsTalking = true;
        OnPropertyChanged(nameof(IsOverlayPanelBackgroundVisible));
        OnPropertyChanged(nameof(OverlayPanelBackground));
        OnPropertyChanged(nameof(OverlayPanelPadding));
        _ = Task.Delay(500).ContinueWith(_ =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (remoteTalkingUntil.TryGetValue(playerKey, out var activeUntil) &&
                    activeUntil <= DateTimeOffset.UtcNow)
                {
                    remoteTalkingUntil.Remove(playerKey);
                    var currentTile = OtherPlayers.FirstOrDefault(player => GetPlayerKey(player.PlayerId, player.ClientId) == playerKey);
                    if (currentTile is not null)
                    {
                        currentTile.IsTalking = false;
                    }

                    OnPropertyChanged(nameof(IsOverlayPanelBackgroundVisible));
                    OnPropertyChanged(nameof(OverlayPanelBackground));
                    OnPropertyChanged(nameof(OverlayPanelPadding));

                    var currentMeetingPlayer = MeetingOverlayPlayers.FirstOrDefault(player => GetPlayerKey(player.PlayerId, player.ClientId) == playerKey);
                    if (currentMeetingPlayer is not null)
                    {
                        currentMeetingPlayer.IsTalking = false;
                    }

                    RefreshObsOverlayService();
                }
            });
        });
        RefreshObsOverlayService();
    }

    private void OnPlayerTileConfigChanged(PlayerTileViewModel player)
    {
        Settings.PlayerConfigMap[player.ConfigKey] = new SocketConfig
        {
            IsMuted = player.IsPlayerMuted,
            Volume = player.VolumePercent / 100d
        };

        _ = settingsService.SaveAsync(Settings);
        if (lastAmongUsState is not null)
        {
            QueueVoiceStateUpdate(lastAmongUsState);
        }
    }

    private static int GetPlayerKey(int playerId, int clientId)
    {
        return playerId > 0 ? playerId : clientId;
    }

    private void OnAmongUsMemoryError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = error;
        });
    }

    private static string ModIdToProtocol(AmongUsModType mod)
    {
        return mod switch
        {
            AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
            AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
            AmongUsModType.TownOfUs => "TOWN_OF_US",
            AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
            AmongUsModType.LasMonjas => "LAS_MONJAS",
            AmongUsModType.Other => "OTHER",
            _ => "NONE"
        };
    }

    private static bool ValidateServerUrl(string url)
    {
        var normalized = NormalizeServerUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (string.Equals(uri.Host, "discord.gg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/";
    }

    private static void OpenExternal(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string NormalizeServerUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed[..^1] : trimmed;
    }

    private static string MaskLobbyCode(string lobbyCode)
    {
        if (string.IsNullOrWhiteSpace(lobbyCode) ||
            string.Equals(lobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return lobbyCode;
        }

        return new string('*', lobbyCode.Length);
    }

    private static List<string> NormalizeServerUrls(IEnumerable<string> urls)
    {
        return urls
            .Select(NormalizeServerUrl)
            .Where(ValidateServerUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static class PlayerColorPalette
    {
        private static readonly (Color Main, Color Shadow)[] Colors =
        [
            (Color.FromRgb(0xC5, 0x11, 0x11), Color.FromRgb(0x7A, 0x08, 0x38)),
            (Color.FromRgb(0x13, 0x2E, 0xD1), Color.FromRgb(0x09, 0x15, 0x8E)),
            (Color.FromRgb(0x11, 0x7F, 0x2D), Color.FromRgb(0x0A, 0x4D, 0x2E)),
            (Color.FromRgb(0xED, 0x54, 0xBA), Color.FromRgb(0xAB, 0x2B, 0xAD)),
            (Color.FromRgb(0xEF, 0x7D, 0x0D), Color.FromRgb(0xB3, 0x3E, 0x15)),
            (Color.FromRgb(0xF5, 0xF5, 0x57), Color.FromRgb(0xC3, 0x88, 0x23)),
            (Color.FromRgb(0x3F, 0x47, 0x4E), Color.FromRgb(0x1E, 0x1F, 0x26)),
            (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x83, 0x94, 0xBF)),
            (Color.FromRgb(0x6B, 0x2F, 0xBB), Color.FromRgb(0x3B, 0x17, 0x7C)),
            (Color.FromRgb(0x71, 0x49, 0x1E), Color.FromRgb(0x5E, 0x26, 0x15)),
            (Color.FromRgb(0x38, 0xFE, 0xDC), Color.FromRgb(0x24, 0xA8, 0xBE)),
            (Color.FromRgb(0x50, 0xEF, 0x39), Color.FromRgb(0x15, 0xA7, 0x42))
        ];

        public static (Color Main, Color Shadow) Get(int colorId, IReadOnlyList<PlayerColorPair> dynamicColors)
        {
            if (colorId >= 0 && colorId < dynamicColors.Count)
            {
                return (FromGameColor(dynamicColors[colorId].Main), FromGameColor(dynamicColors[colorId].Shadow));
            }

            return colorId >= 0 && colorId < Colors.Length ? Colors[colorId] : Colors[11];
        }

        private static Color FromGameColor(uint color)
        {
            return Color.FromRgb(
                (byte)(color & 0x000000ff),
                (byte)((color & 0x0000ff00) >> 8),
                (byte)((color & 0x00ff0000) >> 16));
        }
    }

    private static Thickness ToSkinPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        return ToBodyCosmeticPlacementMargin(placement, baseSize);
    }

    private static double ToSkinPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        return ToBodyCosmeticPlacementWidth(placement, baseSize);
    }

    private static Thickness ToBodyCosmeticPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        if (placement.WidthPercent <= 0)
        {
            return new Thickness(baseSize * 0.20d, baseSize * 0.06d, 0, 0);
        }

        var rawLeft = (placement.LeftPercent / 100d) * baseSize;
        var rawTop = (placement.TopPercent / 100d) * baseSize;
        return new Thickness(
            rawLeft + (baseSize * 0.18d),
            Math.Max(baseSize * 0.04d, rawTop - (baseSize * 0.04d)),
            0,
            0);
    }

    private static Thickness ToPlacementMargin(CosmeticPlacement placement, double baseSize)
    {
        if (placement.WidthPercent <= 0)
        {
            return new Thickness(0);
        }

        return new Thickness(
            (placement.LeftPercent / 100d) * baseSize,
            (placement.TopPercent / 100d) * baseSize,
            0,
            0);
    }

    private static double ToPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        return placement.WidthPercent <= 0 ? baseSize : (placement.WidthPercent / 100d) * baseSize;
    }

    private static double ToBodyCosmeticPlacementWidth(CosmeticPlacement placement, double baseSize)
    {
        var width = placement.WidthPercent <= 0 ? baseSize * 2d : (placement.WidthPercent / 100d) * baseSize * 2d;
        return Math.Clamp(width, baseSize * 1.64d, baseSize * 2.24d);
    }

    public sealed class OptionViewModel
    {
        public OptionViewModel(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }

        public string Label { get; }
    }

    public sealed class PlayerTileViewModel : ObservableObject
    {
        private readonly Action<PlayerTileViewModel> onConfigChanged;
        private bool compactOverlay;
        private bool hasVoicePeer;
        private bool isAudible;
        private bool isPeerConnectionReady;
        private bool isPlayerMuted;
        private bool isRemoteDeafened;
        private bool isRemoteMuted;
        private bool isVolumePopupOpen;
        private bool isTalking;
        private bool isUsingRadio;
        private double opacity;
        private double volumePercent;

        public PlayerTileViewModel(
            string configKey,
            int playerId,
            int clientId,
            string name,
            Brush mainBrush,
            Brush shadowBrush,
            ImageSource avatarImage,
            CosmeticImageSet cosmetics,
            bool isDead,
            bool isTalking,
            bool isUsingRadio,
            bool isRemoteMuted,
            bool isRemoteDeafened,
            bool isAudible,
            bool hasVoicePeer,
            bool isPeerConnectionReady,
            bool compactOverlay,
            SocketConfig config,
            Action<PlayerTileViewModel> onConfigChanged)
        {
            ConfigKey = configKey;
            PlayerId = playerId;
            ClientId = clientId;
            Name = name;
            MainBrush = mainBrush;
            ShadowBrush = shadowBrush;
            AvatarImage = avatarImage;
            this.onConfigChanged = onConfigChanged;
            isPlayerMuted = config.IsMuted;
            volumePercent = Math.Clamp(config.Volume * 100d, 0d, 200d);
            IsTalking = isTalking;
            this.isUsingRadio = isUsingRadio;
            this.isRemoteMuted = isRemoteMuted;
            this.isRemoteDeafened = isRemoteDeafened;
            this.isAudible = isAudible;
            this.hasVoicePeer = hasVoicePeer;
            this.isPeerConnectionReady = isPeerConnectionReady;
            this.compactOverlay = compactOverlay;
            HatFrontImage = cosmetics.HatFront;
            HatBackImage = cosmetics.HatBack;
            SkinImage = cosmetics.Skin;
            VisorImage = cosmetics.Visor;
            HatMargin = ToBodyCosmeticPlacementMargin(cosmetics.Hat, 38);
            SkinMargin = ToSkinPlacementMargin(cosmetics.SkinPlacement, 38);
            VisorMargin = ToPlacementMargin(cosmetics.VisorPlacement, 38);
            HatWidth = ToBodyCosmeticPlacementWidth(cosmetics.Hat, 38);
            SkinWidth = ToSkinPlacementWidth(cosmetics.SkinPlacement, 38);
            VisorWidth = ToPlacementWidth(cosmetics.VisorPlacement, 38);
            opacity = isDead || !isAudible ? 0.45 : 1.0;
        }

        public void UpdateState(
            bool isDead,
            bool isTalking,
            bool isUsingRadio,
            bool isRemoteMuted,
            bool isRemoteDeafened,
            bool isAudible,
            bool hasVoicePeer,
            bool isPeerConnectionReady,
            bool compactOverlay,
            SocketConfig config)
        {
            IsTalking = isTalking;
            IsUsingRadio = isUsingRadio;
            IsRemoteMuted = isRemoteMuted;
            IsRemoteDeafened = isRemoteDeafened;
            IsAudible = isAudible;
            HasVoicePeer = hasVoicePeer;
            IsPeerConnectionReady = isPeerConnectionReady;
            CompactOverlay = compactOverlay;
            Opacity = isDead || !isAudible ? 0.45 : 1.0;

            if (!IsVolumePopupOpen)
            {
                isPlayerMuted = config.IsMuted;
                volumePercent = Math.Clamp(config.Volume * 100d, 0d, 200d);
                OnPropertyChanged(nameof(IsPlayerMuted));
                OnPropertyChanged(nameof(VolumePercent));
                OnPropertyChanged(nameof(EffectiveVolumeText));
            }
        }

        public string ConfigKey { get; }

        public int PlayerId { get; }

        public int ClientId { get; }

        public string Name { get; }

        public Brush MainBrush { get; }

        public Brush ShadowBrush { get; }

        public ImageSource AvatarImage { get; }

        public bool IsTalking
        {
            get => isTalking;
            set
            {
                if (SetProperty(ref isTalking, value))
                {
                    OnPropertyChanged(nameof(RingBrush));
                    OnPropertyChanged(nameof(RingThickness));
                    OnPropertyChanged(nameof(RingGlowOpacity));
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool CompactOverlay
        {
            get => compactOverlay;
            set
            {
                if (SetProperty(ref compactOverlay, value))
                {
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool IsUsingRadio
        {
            get => isUsingRadio;
            private set
            {
                if (SetProperty(ref isUsingRadio, value))
                {
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool IsRemoteMuted
        {
            get => isRemoteMuted;
            private set
            {
                if (SetProperty(ref isRemoteMuted, value))
                {
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool IsRemoteDeafened
        {
            get => isRemoteDeafened;
            private set
            {
                if (SetProperty(ref isRemoteDeafened, value))
                {
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool IsAudible
        {
            get => isAudible;
            private set
            {
                if (SetProperty(ref isAudible, value))
                {
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool HasVoicePeer
        {
            get => hasVoicePeer;
            private set
            {
                if (SetProperty(ref hasVoicePeer, value))
                {
                    OnPropertyChanged(nameof(ConnectionWarningVisible));
                    OnPropertyChanged(nameof(ConnectionWarningText));
                    OnPropertyChanged(nameof(ConnectionWarningIconPath));
                    OnPropertyChanged(nameof(ConnectionWarningBackground));
                    OnPropertyChanged(nameof(ConnectionWarningBorder));
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool IsPeerConnectionReady
        {
            get => isPeerConnectionReady;
            private set
            {
                if (SetProperty(ref isPeerConnectionReady, value))
                {
                    OnPropertyChanged(nameof(ConnectionWarningVisible));
                    OnPropertyChanged(nameof(ConnectionWarningText));
                    OnPropertyChanged(nameof(ConnectionWarningIconPath));
                    OnPropertyChanged(nameof(ConnectionWarningBackground));
                    OnPropertyChanged(nameof(ConnectionWarningBorder));
                    OnPropertyChanged(nameof(IsVisibleInGameOverlay));
                }
            }
        }

        public bool ConnectionWarningVisible => !HasVoicePeer || !IsPeerConnectionReady;

        public bool IsVisibleInGameOverlay =>
            HasVoicePeer &&
            (IsPeerConnectionReady || IsRemoteMuted || IsRemoteDeafened) &&
            (!CompactOverlay || IsTalking);

        public string ConnectionWarningText => !HasVoicePeer
            ? "BetterCrewLink未接続"
            : !IsPeerConnectionReady
                ? "音声接続待ち"
                : string.Empty;

        public string ConnectionWarningIconPath => !HasVoicePeer
            ? "M22.99 9C19.15 5.16 13.8 3.76 8.84 4.78L11.36 7.3C14.83 7.13 18.35 8.35 20.99 11L22.99 9ZM18.99 13C17.7 11.71 16.15 10.87 14.5 10.44L18.03 13.97L18.99 13ZM2 3.05L5.07 6.1C3.6 6.82 2.22 7.78 1 9L2.99 11C4.23 9.76 5.66 8.84 7.19 8.23L9.43 10.47C7.81 10.89 6.27 11.73 5 13V13.01L6.99 15C8.35 13.64 10.13 12.96 11.91 12.94L18.98 20L20.25 18.74L3.29 1.79L2 3.05ZM9 17L12 20L15 17C13.35 15.34 10.66 15.34 9 17Z"
            : !IsPeerConnectionReady
                ? "M17 7H13V8.9H17C18.71 8.9 20.1 10.29 20.1 12C20.1 13.43 19.12 14.63 17.79 14.98L19.25 16.44C20.88 15.61 22 13.95 22 12C22 9.24 19.76 7 17 7ZM16 11H13.81L15.81 13H16V11ZM2 4.27L5.11 7.38C3.29 8.12 2 9.91 2 12C2 14.76 4.24 17 7 17H11V15.1H7C5.29 15.1 3.9 13.71 3.9 12C3.9 10.41 5.11 9.1 6.66 8.93L8.73 11H8V13H10.73L13 15.27V17H14.73L18.74 21L20 19.74L3.27 3L2 4.27Z"
                : string.Empty;

        public Brush ConnectionWarningBackground => !HasVoicePeer
            ? new SolidColorBrush(Color.FromRgb(0xEA, 0x3C, 0x2A))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));

        public Brush ConnectionWarningBorder => !HasVoicePeer
            ? new SolidColorBrush(Color.FromRgb(0x69, 0x0A, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x69, 0x49, 0x00));

        public bool IsPlayerMuted
        {
            get => isPlayerMuted;
            set
            {
                if (SetProperty(ref isPlayerMuted, value))
                {
                    onConfigChanged(this);
                    OnPropertyChanged(nameof(EffectiveVolumeText));
                }
            }
        }

        public double VolumePercent
        {
            get => volumePercent;
            set
            {
                var next = Math.Round(Math.Clamp(value, 0d, 200d));
                if (SetProperty(ref volumePercent, next))
                {
                    onConfigChanged(this);
                    OnPropertyChanged(nameof(EffectiveVolumeText));
                }
            }
        }

        public string EffectiveVolumeText => IsPlayerMuted ? "ミュート" : $"{VolumePercent:0}%";

        public bool IsVolumePopupOpen
        {
            get => isVolumePopupOpen;
            set => SetProperty(ref isVolumePopupOpen, value);
        }

        public Brush RingBrush => IsTalking
            ? new SolidColorBrush(Color.FromRgb(0x32, 0xFF, 0x7E))
            : new SolidColorBrush(Color.FromRgb(0x3C, 0x37, 0x46));

        public double RingThickness => IsTalking ? 3.2 : 1.2;

        public double RingGlowOpacity => IsTalking ? 0.85 : 0;

        public string HatFrontImage { get; }

        public string HatBackImage { get; }

        public string SkinImage { get; }

        public string VisorImage { get; }

        public Thickness HatMargin { get; }

        public Thickness SkinMargin { get; }

        public Thickness VisorMargin { get; }

        public double HatWidth { get; }

        public double SkinWidth { get; }

        public double VisorWidth { get; }

        public double Opacity
        {
            get => opacity;
            private set => SetProperty(ref opacity, value);
        }
    }

    public sealed class MeetingOverlayPlayerViewModel : ObservableObject
    {
        private bool isTalking;

        public MeetingOverlayPlayerViewModel(int playerId, int clientId, Brush glowBrush, bool isTalking)
        {
            PlayerId = playerId;
            ClientId = clientId;
            GlowBrush = glowBrush;
            this.isTalking = isTalking;
        }

        public int PlayerId { get; }

        public int ClientId { get; }

        public Brush GlowBrush { get; }

        public bool IsTalking
        {
            get => isTalking;
            set => SetProperty(ref isTalking, value);
        }
    }
}
