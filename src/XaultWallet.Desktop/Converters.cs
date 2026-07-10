using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop;

/// <summary>true -> green, false -> amber. Used for the seed-verification status text.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter OkWarn = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#4CAF7D"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E0A030"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Ok : Warn;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats an atomic-unit ulong as an XMR decimal string for the history grid.</summary>
public sealed class AtomicToXmrConverter : IValueConverter
{
    public static readonly AtomicToXmrConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ulong atomic
            ? MoneroRpcClient.AtomicToXmr(atomic).ToString("0.############", culture)
            : "0";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
