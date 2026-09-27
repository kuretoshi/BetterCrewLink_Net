using System.Diagnostics;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class GameLaunchService
{
    public IReadOnlyDictionary<GamePlatform, GamePlatformInstance> DefaultPlatforms { get; } =
        new Dictionary<GamePlatform, GamePlatformInstance>
        {
            [GamePlatform.Steam] = new()
            {
                Default = true,
                Key = "STEAM",
                LaunchType = PlatformRunType.Uri,
                RunPath = "steam://rungameid/945360",
                Execute = [string.Empty],
                TranslateKey = "platform.steam"
            },
            [GamePlatform.Epic] = new()
            {
                Default = true,
                Key = "EPIC",
                LaunchType = PlatformRunType.Uri,
                RunPath = "com.epicgames.launcher://apps/963137e4c29d4c79a81323b8fab03a40?action=launch&silent=true",
                Execute = [string.Empty],
                TranslateKey = "platform.epicgames"
            },
            [GamePlatform.Microsoft] = new()
            {
                Default = true,
                Key = "MICROSOFT",
                LaunchType = PlatformRunType.Exe,
                RunPath = "none",
                Execute = ["Among Us.exe"],
                TranslateKey = "platform.microsoft"
            }
        };

    public void Launch(GamePlatform platform, IReadOnlyDictionary<string, GamePlatformInstance> customPlatforms)
    {
        var target = ResolvePlatform(platform, customPlatforms);

        if (target.LaunchType == PlatformRunType.Uri)
        {
            Process.Start(new ProcessStartInfo(target.RunPath) { UseShellExecute = true });
            return;
        }

        var executable = target.RunPath == "none" ? target.Execute.FirstOrDefault() : target.RunPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("起動対象が設定されていません。");
        }

        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    private GamePlatformInstance ResolvePlatform(
        GamePlatform platform,
        IReadOnlyDictionary<string, GamePlatformInstance> customPlatforms)
    {
        if (platform != GamePlatform.Custom && DefaultPlatforms.TryGetValue(platform, out var defaultPlatform))
        {
            return defaultPlatform;
        }

        var custom = customPlatforms.Values.FirstOrDefault(static platformInstance =>
            !string.IsNullOrWhiteSpace(platformInstance.RunPath));
        return custom ?? throw new InvalidOperationException("カスタム起動設定がありません。");
    }
}
