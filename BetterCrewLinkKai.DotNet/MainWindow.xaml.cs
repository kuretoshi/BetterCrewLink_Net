using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterCrewLinkKai.DotNet.Services;
using BetterCrewLinkKai.DotNet.ViewModels;

namespace BetterCrewLinkKai.DotNet;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer overlayRefreshTimer;
    private OverlayWindow? overlayWindow;
    private MeetingOverlayWindow? meetingOverlayWindow;
    private MainWindowViewModel? currentViewModel;
    private readonly Dictionary<MainWindowViewModel.PlayerTileViewModel, int> playerVolumeHoverCounts = [];
    private bool? lastHardwareAcceleration;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        overlayRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        overlayRefreshTimer.Tick += (_, _) =>
        {
            ApplyHardwareAcceleration();
            RefreshOverlay();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.LoadCommand.CanExecute(null))
        {
            currentViewModel = viewModel;
            currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.LoadCommand.Execute(null);
            ApplyHardwareAcceleration();
            overlayRefreshTimer.Start();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        overlayRefreshTimer.Stop();
        if (currentViewModel is not null)
        {
            currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        overlayWindow?.Close();
        overlayWindow = null;
        meetingOverlayWindow?.Close();
        meetingOverlayWindow = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ShowVoiceView) or
            nameof(MainWindowViewModel.IsDiscussionView) or
            nameof(MainWindowViewModel.AlwaysOnTop) or
            nameof(MainWindowViewModel.EnableOverlay) or
            nameof(MainWindowViewModel.CompactOverlay) or
            nameof(MainWindowViewModel.MeetingOverlay) or
            nameof(MainWindowViewModel.OverlayPosition) or
            nameof(MainWindowViewModel.HardwareAcceleration) or
            nameof(MainWindowViewModel.Settings))
        {
            ApplyHardwareAcceleration();
            RefreshOverlay();
        }
    }

    private void ApplyHardwareAcceleration()
    {
        if (currentViewModel is null || lastHardwareAcceleration == currentViewModel.Settings.HardwareAcceleration)
        {
            return;
        }

        lastHardwareAcceleration = currentViewModel.Settings.HardwareAcceleration;
        System.Windows.Media.RenderOptions.ProcessRenderMode = currentViewModel.Settings.HardwareAcceleration
            ? RenderMode.Default
            : RenderMode.SoftwareOnly;
    }

    private void RefreshOverlay()
    {
        if (currentViewModel is null)
        {
            return;
        }

        var settings = currentViewModel.Settings;
        var overlayPosition = MainWindowViewModel.NormalizeOverlayPosition(settings.OverlayPosition);
        var shouldShow = settings.EnableOverlay &&
                         overlayPosition != "hidden" &&
                         currentViewModel.ShowVoiceView &&
                         (settings.MeetingOverlay || !currentViewModel.IsDiscussionView);
        RefreshMeetingOverlay();

        if (!shouldShow)
        {
            overlayWindow?.Hide();
            return;
        }

        overlayWindow ??= new OverlayWindow
        {
            DataContext = currentViewModel
        };

        overlayWindow.DataContext = currentViewModel;
        overlayWindow.ApplyOverlayMode(overlayPosition, settings.CompactOverlay);
        PositionOverlay(overlayWindow, overlayPosition);
        OverlayWindowInterop.KeepAttached(overlayWindow);

        if (!overlayWindow.IsVisible)
        {
            overlayWindow.Show();
        }
    }

    private void RefreshMeetingOverlay()
    {
        if (currentViewModel is null)
        {
            return;
        }

        var settings = currentViewModel.Settings;
        var shouldShow = settings.EnableOverlay &&
                         settings.MeetingOverlay &&
                         currentViewModel.ShowVoiceView &&
                         currentViewModel.IsDiscussionView &&
                         AmongUsProcessService.IsAmongUsForeground();
        if (!shouldShow)
        {
            meetingOverlayWindow?.Hide();
            return;
        }

        var bounds = TryGetAmongUsClientBounds(this) ?? SystemParameters.WorkArea;
        meetingOverlayWindow ??= new MeetingOverlayWindow();

        meetingOverlayWindow.Attach(currentViewModel);
        meetingOverlayWindow.Left = bounds.Left;
        meetingOverlayWindow.Top = bounds.Top;
        meetingOverlayWindow.Width = bounds.Width;
        meetingOverlayWindow.Height = bounds.Height;
        OverlayWindowInterop.KeepAttached(meetingOverlayWindow);

        if (!meetingOverlayWindow.IsVisible)
        {
            meetingOverlayWindow.Show();
        }
    }

    private static void PositionOverlay(Window window, string overlayPosition)
    {
        var bounds = TryGetAmongUsClientBounds(window) ?? SystemParameters.WorkArea;
        var position = MainWindowViewModel.NormalizeOverlayPosition(overlayPosition);
        var width = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
        if (double.IsNaN(width) || width <= 1)
        {
            width = window.MinWidth > 1 ? window.MinWidth : 76;
        }

        if (double.IsNaN(height) || height <= 1)
        {
            height = window.MinHeight > 1 ? window.MinHeight : 38;
        }

        const double edgeMargin = 12;
        switch (position)
        {
            case "top":
                window.Left = bounds.Left + Math.Max(edgeMargin, (bounds.Width - width) / 2d);
                window.Top = bounds.Top + 24;
                break;
            case "bottom-left":
                window.Left = bounds.Left + edgeMargin;
                window.Top = bounds.Bottom - height - 24;
                break;
            case "left":
                window.Left = bounds.Left + edgeMargin;
                window.Top = bounds.Top + Math.Max(24, (bounds.Height - height) / 2d);
                break;
            case "left-box":
                window.Left = bounds.Left;
                window.Top = bounds.Top + Math.Max(24, (bounds.Height - height) / 2d);
                break;
            case "right-box":
                window.Left = bounds.Right - width;
                window.Top = bounds.Top + Math.Max(24, (bounds.Height - height) / 2d);
                break;
            case "right":
            default:
                window.Left = bounds.Right - width - edgeMargin;
                window.Top = bounds.Top + Math.Max(24, (bounds.Height - height) / 2d);
                break;
        }
    }

    private static Rect? TryGetAmongUsClientBounds(Visual relativeTo)
    {
        foreach (var process in Process.GetProcessesByName("Among Us"))
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle == 0 || !IsWindowVisible(handle) || !GetClientRect(handle, out var clientRect))
                {
                    continue;
                }

                var origin = new NativePoint();
                if (!ClientToScreen(handle, ref origin))
                {
                    continue;
                }

                var topLeft = new Point(origin.X, origin.Y);
                var bottomRight = new Point(origin.X + clientRect.Right, origin.Y + clientRect.Bottom);
                var source = PresentationSource.FromVisual(relativeTo);
                if (source?.CompositionTarget is not null)
                {
                    var transform = source.CompositionTarget.TransformFromDevice;
                    topLeft = transform.Transform(topLeft);
                    bottomRight = transform.Transform(bottomRight);
                }

                return new Rect(topLeft, bottomRight);
            }
            catch
            {
                // Fall back to desktop positioning if the game window cannot be inspected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            var wasTimerEnabled = overlayRefreshTimer.IsEnabled;
            if (wasTimerEnabled)
            {
                overlayRefreshTimer.Stop();
            }

            try
            {
                DragMove();
            }
            finally
            {
                if (wasTimerEnabled)
                {
                    overlayRefreshTimer.Start();
                }
            }
        }
    }

    private void OnPlayerVolumeMouseEnter(object sender, MouseEventArgs e)
    {
        if (TryGetPlayerTileViewModel(sender, out var player))
        {
            playerVolumeHoverCounts.TryGetValue(player, out var count);
            playerVolumeHoverCounts[player] = count + 1;
            player.IsVolumePopupOpen = true;
        }
    }

    private async void OnPlayerVolumeMouseLeave(object sender, MouseEventArgs e)
    {
        if (TryGetPlayerTileViewModel(sender, out var player))
        {
            await Task.Delay(120);
            if (!playerVolumeHoverCounts.TryGetValue(player, out var count))
            {
                return;
            }

            count = Math.Max(0, count - 1);
            if (count == 0)
            {
                playerVolumeHoverCounts.Remove(player);
                player.IsVolumePopupOpen = false;
            }
            else
            {
                playerVolumeHoverCounts[player] = count;
            }
        }
    }

    private static bool TryGetPlayerTileViewModel(object sender, out MainWindowViewModel.PlayerTileViewModel player)
    {
        if (sender is FrameworkElement element)
        {
            if (element.DataContext is MainWindowViewModel.PlayerTileViewModel directPlayer)
            {
                player = directPlayer;
                return true;
            }

            var parent = element.Parent;
            while (parent is FrameworkElement parentElement)
            {
                if (parentElement.DataContext is MainWindowViewModel.PlayerTileViewModel parentPlayer)
                {
                    player = parentPlayer;
                    return true;
                }

                parent = parentElement.Parent;
            }
        }

        player = null!;
        return false;
    }

    private void OnShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcut = FormatShortcutKey(key);
        if (shortcut is null)
        {
            return;
        }

        ApplyShortcut(sender, shortcut);
        e.Handled = true;
    }

    private void OnShortcutPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var shortcut = e.ChangedButton switch
        {
            MouseButton.XButton1 => "MouseButton4",
            MouseButton.XButton2 => "MouseButton5",
            _ => null
        };
        if (shortcut is null)
        {
            return;
        }

        ApplyShortcut(sender, shortcut);
        e.Handled = true;
    }

    private void ApplyShortcut(object sender, string shortcut)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string settingName)
        {
            return;
        }

        textBox.Text = shortcut;
        textBox.CaretIndex = textBox.Text.Length;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UpdateShortcut(settingName, shortcut);
        }
    }

    private static string? FormatShortcutKey(Key key)
    {
        return key switch
        {
            Key.Escape => "Disabled",
            Key.Back => "Disabled",
            Key.Delete => "Disabled",
            Key.Space => "Space",
            Key.LeftCtrl => "LControl",
            Key.RightCtrl => "RControl",
            Key.LeftAlt => "LAlt",
            Key.RightAlt => "RAlt",
            Key.LeftShift => "LShift",
            Key.RightShift => "RShift",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Numpad{(int)key - (int)Key.NumPad0}",
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            _ => null
        };
    }
}

