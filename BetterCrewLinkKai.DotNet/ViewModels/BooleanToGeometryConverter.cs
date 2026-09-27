using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BetterCrewLinkKai.DotNet.ViewModels;

public sealed class BooleanToGeometryConverter : IValueConverter
{
    public string TrueData { get; set; } = string.Empty;

    public string FalseData { get; set; } = string.Empty;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var data = value is bool flag && flag ? TrueData : FalseData;
        return Geometry.Parse(data);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
