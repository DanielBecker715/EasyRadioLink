using System;
using System.Globalization;
using System.Windows.Data;

namespace EasyRadioLink.Client.Utils.ValueConverters;

/// <summary>Shows a password as "••••••" ("" for no password) - used by the favourites list.</summary>
internal class PasswordMaskConverter : IValueConverter
{
    private const string Mask = "••••••";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string password && password.Length > 0 ? Mask : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
