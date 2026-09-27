using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BetterCrewLinkKai.DotNet.ViewModels;

namespace BetterCrewLinkKai.DotNet;

public partial class MeetingOverlayWindow : Window
{
    private MainWindowViewModel? viewModel;

    public MeetingOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            OverlayWindowInterop.ApplyOverlayStyle(this);
            OverlayWindowInterop.KeepAttached(this);
        };
        SizeChanged += (_, _) => Render();
        IsVisibleChanged += (_, _) => Render();
    }

    public void Attach(MainWindowViewModel nextViewModel)
    {
        if (ReferenceEquals(viewModel, nextViewModel))
        {
            Render();
            return;
        }

        Detach();
        viewModel = nextViewModel;
        viewModel.MeetingOverlayPlayers.CollectionChanged += OnMeetingPlayersChanged;
        foreach (var player in viewModel.MeetingOverlayPlayers)
        {
            player.PropertyChanged += OnMeetingPlayerPropertyChanged;
        }

        Render();
    }

    public void Detach()
    {
        if (viewModel is null)
        {
            return;
        }

        viewModel.MeetingOverlayPlayers.CollectionChanged -= OnMeetingPlayersChanged;
        foreach (var player in viewModel.MeetingOverlayPlayers)
        {
            player.PropertyChanged -= OnMeetingPlayerPropertyChanged;
        }

        viewModel = null;
        MeetingCanvas.Children.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    private void OnMeetingPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MainWindowViewModel.MeetingOverlayPlayerViewModel player in e.OldItems)
            {
                player.PropertyChanged -= OnMeetingPlayerPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MainWindowViewModel.MeetingOverlayPlayerViewModel player in e.NewItems)
            {
                player.PropertyChanged += OnMeetingPlayerPropertyChanged;
            }
        }

        Render();
    }

    private void OnMeetingPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.MeetingOverlayPlayerViewModel.IsTalking))
        {
            Render();
        }
    }

    private void Render()
    {
        MeetingCanvas.Children.Clear();
        if (viewModel is null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var players = viewModel.MeetingOverlayPlayers.ToArray();
        if (players.Length == 0)
        {
            return;
        }

        var ratioDifference = Math.Abs((ActualWidth / ActualHeight) - 1.7d);
        var hudWidth = ratioDifference < 0.25d
            ? ActualWidth / 1.192d
            : ratioDifference < 0.5d
                ? ActualWidth / 1.146d
                : ActualWidth / 1.591d;
        var hudHeight = hudWidth / 1.72d;
        var hudLeft = (ActualWidth - hudWidth) / 2d;
        var hudTop = (ActualHeight - hudHeight) / 2d;

        var tabletLeft = hudLeft + (hudWidth * 0.004d);
        var tabletTop = hudTop + (hudHeight * 0.15d);
        var tabletHeight = hudHeight * 0.105d;
        var cardWidth = hudWidth * 0.30d;
        var cardHeight = tabletHeight * 1.09d;
        var marginLeft = hudWidth * 0.024d;
        var marginRight = hudWidth * 0.0023d;
        var marginBottom = hudWidth * 0.019d;
        var radius = hudHeight / 100d;
        var glowSize = Math.Max(8d, hudHeight / 100d);

        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            if (!player.IsTalking)
            {
                continue;
            }

            var glowColor = ToColor(player.GlowBrush);
            var column = index % 3;
            var row = index / 3;
            var left = tabletLeft + marginLeft + (column * (cardWidth + marginLeft + marginRight));
            var top = tabletTop + (row * (cardHeight + marginBottom));
            var border = new Border
            {
                Width = cardWidth,
                Height = cardHeight,
                CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xD8, glowColor.R, glowColor.G, glowColor.B)),
                Background = new SolidColorBrush(Color.FromArgb(0x22, glowColor.R, glowColor.G, glowColor.B)),
                Opacity = 1,
                Effect = new DropShadowEffect
                {
                    Color = glowColor,
                    BlurRadius = glowSize * 2.8d,
                    ShadowDepth = 0,
                    Opacity = 0.95
                }
            };

            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            MeetingCanvas.Children.Add(border);
        }
    }

    private static Color ToColor(Brush brush)
    {
        return brush is SolidColorBrush solidColorBrush
            ? solidColorBrush.Color
            : Color.FromRgb(0x2E, 0xCC, 0x71);
    }
}
