using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace XaultWallet.Desktop.Controls;

/// <summary>
/// A stroked line icon drawn from geometry authored on a 24×24 grid (see the Icon* resources in
/// App.axaml). It inherits the text foreground, so an icon inside a button takes the button's
/// colour in every state without extra styling.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.8);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<Icon>(18);
        HeightProperty.OverrideDefaultValue<Icon>(18);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
        {
            return;
        }

        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, pen, Data);
        }
    }
}
