using System.Windows;

namespace BetterCrewLinkKai.DotNet;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            OverlayWindowInterop.ApplyOverlayStyle(this);
            OverlayWindowInterop.KeepAttached(this);
        };
    }

    public void ApplyCompactMode(bool compact)
    {
        Width = compact ? 184 : 300;
        OverlayPanel.Padding = compact ? new Thickness(0) : new Thickness(0, 4, 0, 4);
    }

    public void ApplyOverlayMode(string overlayPosition, bool compact)
    {
        var position = ViewModels.MainWindowViewModel.NormalizeOverlayPosition(overlayPosition);
        MinWidth = 0;
        if (position == "top" && compact)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN;
            OverlayWindowInterop.KeepAttached(this);
            UpdateLayout();
            return;
        }

        SizeToContent = SizeToContent.Height;
        Width = position switch
        {
            "top" => compact ? 520 : 720,
            "bottom-left" => compact ? 150 : 180,
            "right-box" or "left-box" => 92,
            _ => compact ? 184 : 300
        };
        OverlayWindowInterop.KeepAttached(this);
        UpdateLayout();
    }
}
