using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SyncBox
{
    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is bool flag && !flag;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is bool flag ? !flag : Binding.DoNothing;
    }

    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            if (string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase))
            {
                flag = !flag;
            }

            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is Visibility visibility && visibility == Visibility.Visible;
    }

    public sealed class ConnectionColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush Online = Frozen(Colors.SpringGreen);
        private static readonly SolidColorBrush Offline = Frozen(Colors.Crimson);

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is bool flag && flag ? Online : Offline;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Binding.DoNothing;

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool hasText = !string.IsNullOrWhiteSpace(value as string);
            if (string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase))
            {
                hasText = !hasText;
            }

            return hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}