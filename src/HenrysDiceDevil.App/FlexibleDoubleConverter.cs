using System.Globalization;
using System.Windows.Data;

namespace HenrysDiceDevil.App;

public sealed class FlexibleDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        return "0";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = (value as string)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return 0.0;
        }

        if (text.StartsWith(".", StringComparison.Ordinal))
        {
            text = "0" + text;
        }

        text = text.Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return Binding.DoNothing;
    }
}
