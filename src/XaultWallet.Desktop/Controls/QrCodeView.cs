using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace XaultWallet.Desktop.Controls;

/// <summary>
/// Renders <see cref="Text"/> as a QR code (dark modules on white, 4-module quiet zone, ECC level M).
/// Encoding is QRCoder's; the snapshot tool decodes the rendered image and checks it round-trips to
/// the address shown on screen, so the QR can never silently disagree with the text.
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Text));

    private const int QuietZone = 4;
    private bool[,]? _modules;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(TextProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _modules = Encode(Text);
        }
    }

    private static bool[,]? Encode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // ModuleMatrix already includes QRCoder's own 4-module quiet zone; strip it so this control
        // owns the margin and the white background around it.
        int full = data.ModuleMatrix.Count, n = full - 8;
        var m = new bool[n, n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                m[y, x] = data.ModuleMatrix[y + 4][x + 4];
            }
        }

        return m;
    }

    public override void Render(DrawingContext context)
    {
        double side = Math.Min(Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.White, new Rect(0, 0, side, side));
        if (_modules is null)
        {
            return;
        }

        int n = _modules.GetLength(0);
        // Whole-pixel modules keep edges crisp (scanners dislike anti-aliased seams).
        double module = Math.Floor(side / (n + (2 * QuietZone)));
        double offset = Math.Floor((side - (module * n)) / 2);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    if (!_modules[y, x])
                    {
                        continue;
                    }

                    double px = offset + (x * module), py = offset + (y * module);
                    g.BeginFigure(new Point(px, py), true);
                    g.LineTo(new Point(px + module, py));
                    g.LineTo(new Point(px + module, py + module));
                    g.LineTo(new Point(px, py + module));
                    g.EndFigure(true);
                }
            }
        }

        context.DrawGeometry(new SolidColorBrush(Color.Parse("#111114")), null, geometry);
    }
}
