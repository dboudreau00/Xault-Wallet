using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop;

/// <summary>Bool-to-brush converters. OkWarn: true -> green, false -> amber (status text).
/// MainnetBadge: true (mainnet, real funds) -> red, false (test networks) -> slate.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter OkWarn = new();

    /// <summary>true (an error) -> danger red, false -> muted secondary text.</summary>
    public static readonly BoolToBrushConverter ErrorMuted = new()
    {
        _whenTrue = new SolidColorBrush(Color.Parse("#FF6B6B")),
        _whenFalse = new SolidColorBrush(Color.Parse("#9C9CA7")),
    };
    public static readonly BoolToBrushConverter MainnetBadge = new()
    {
        _whenTrue = new SolidColorBrush(Color.Parse("#8A3B3B")),
        _whenFalse = new SolidColorBrush(Color.Parse("#3B5A6E")),
    };

    private IBrush _whenTrue = new SolidColorBrush(Color.Parse("#4CAF7D"));
    private IBrush _whenFalse = new SolidColorBrush(Color.Parse("#E0A030"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? _whenTrue : _whenFalse;

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
            // A fund-display path must fail LOUD, not render 0: an unexpected type would
            // silently show every transaction as zero XMR.
            : Avalonia.AvaloniaProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
