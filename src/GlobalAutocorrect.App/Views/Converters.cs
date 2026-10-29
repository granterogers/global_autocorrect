using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GlobalAutocorrect.App.Views;

/// <summary>true is Visible, false is Collapsed; <see cref="Invert"/> flips that.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) != Invert;
}

/// <summary>700 becomes "0.7 seconds", so the slider label follows the thumb straight away.</summary>
public sealed class MillisecondsToSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double ms ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " seconds" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
