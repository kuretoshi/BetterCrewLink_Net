using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BetterCrewLinkKai.DotNet.ViewModels;

public sealed class BooleanToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.White;

    public Brush FalseBrush { get; set; } = Brushes.White;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
