using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace XaultWallet.Desktop;

/// <summary>
/// The app's motion in one place: durations, easing, and the user's reduce-motion preference. With
/// motion reduced nothing fades, slides, scales or shakes; content simply appears. Effects only touch
/// Opacity and RenderTransform (never layout), so they can't move anything a click or a test targets
/// once they end, and they always end on the element's resting values.
/// </summary>
/// <remarks>
/// Three details of Avalonia 11.1's animation engine shape this class (each was observed, and is
/// covered by the UI smoke test):
/// <list type="bullet">
/// <item>An animation's first frame applies on the next clock tick, after the frame may already have
/// rendered: entrances set their starting values directly first, or the final state flashes for a
/// frame (Avalonia's own CrossFade does: the new screen shows at full opacity for one frame).</item>
/// <item>FillMode.Forward commits the last frame as a LOCAL value, and an animation torn down before
/// its first frame commits default(T): opacity 0, for good. Entrances therefore restore their resting
/// values in a finally, and none are style animations (see <see cref="Entrance"/>).</item>
/// <item>There is no keyframe animator for RenderTransform; <see cref="RegisterAnimators"/> adds one.</item>
/// </list>
/// </remarks>
public static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>Windows: Settings › Accessibility › Visual effects › Animation effects off. Any OS:
    /// XAULTWALLET_REDUCE_MOTION=1 (0 forces motion on).</summary>
    public static bool Reduced { get; } = DetectReduced();

    private static readonly Easing EaseOut = new CubicEaseOut();

    /// <summary>
    /// Avalonia 11.1 has no keyframe animator for RenderTransform (only for transitions), so
    /// "translateY(12px)" in a KeyFrame throws "No animator registered". Registered once at startup.
    /// </summary>
    public static void RegisterAnimators()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0)
        {
            Animation.RegisterCustomAnimator<ITransform, TransformOperationsAnimator>();
        }
    }

    private static int s_registered;

    /// <summary>Interpolates CSS-style transform lists ("translate(…)", "scale(…)", "rotate(…)").</summary>
    private sealed class TransformOperationsAnimator : InterpolatingAnimator<ITransform>
    {
        public override ITransform Interpolate(double progress, ITransform oldValue, ITransform newValue) =>
            TransformOperations.Interpolate(
                oldValue as TransformOperations ?? TransformOperations.Identity,
                newValue as TransformOperations ?? TransformOperations.Identity,
                progress);
    }

    /// <summary>Content arriving: fades in while rising a few pixels into place. For elements that
    /// rest at full opacity with no transform of their own.</summary>
    public static Task RiseInAsync(Visual target, double distance = 10, double milliseconds = 280, CancellationToken ct = default, int delayMs = 0) =>
        EnterAsync(target, Translate(0, distance), milliseconds, ct, delayMs);

    /// <summary>Something appearing on top (a dialog, a toast): fades in while settling from 96 %.</summary>
    public static Task PopInAsync(Visual target, double milliseconds = 240, CancellationToken ct = default) =>
        EnterAsync(target, TransformOperations.Parse("scale(0.96)"), milliseconds, ct);

    /// <summary>Fade in where it stands (the scrim behind a dialog).</summary>
    public static Task FadeInAsync(Visual target, double milliseconds = 200, CancellationToken ct = default, int delayMs = 0) =>
        EnterAsync(target, TransformOperations.Identity, milliseconds, ct, delayMs);

    /// <summary>Fade out. The element is left transparent: hide it next. (Its next entrance sets
    /// its own starting values, so the committed 0 never lingers on a visible element.)</summary>
    public static async Task FadeOutAsync(Visual target, double milliseconds = 200, CancellationToken ct = default)
    {
        if (Reduced)
        {
            return;
        }

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children = { Frame(0, (Visual.OpacityProperty, target.Opacity)), Frame(1, (Visual.OpacityProperty, 0d)) },
        };

        try
        {
            await animation.RunAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            // a newer entrance took over
        }
    }

    /// <summary>"That didn't work": a short horizontal shake (wrong password).</summary>
    public static Task ShakeAsync(Visual target, CancellationToken ct = default) =>
        PlayAsync(target, 420, new LinearEasing(), ct,
            (0.00, Visual.RenderTransformProperty, Translate(0, 0)),
            (0.15, Visual.RenderTransformProperty, Translate(-9, 0)),
            (0.35, Visual.RenderTransformProperty, Translate(8, 0)),
            (0.55, Visual.RenderTransformProperty, Translate(-5, 0)),
            (0.75, Visual.RenderTransformProperty, Translate(3, 0)),
            (1.00, Visual.RenderTransformProperty, Translate(0, 0)));

    /// <summary>A changed value: dims briefly and comes back, drawing the eye (the balance).</summary>
    public static Task PulseAsync(Visual target, CancellationToken ct = default) =>
        PlayAsync(target, 520, new SineEaseInOut(), ct,
            (0.00, Visual.OpacityProperty, 1d),
            (0.35, Visual.OpacityProperty, 0.45d),
            (1.00, Visual.OpacityProperty, 1d));

    internal static TransformOperations Translate(double x, double y) =>
        TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {y}px)"));

    /// <summary>Entrance from (transparent, <paramref name="start"/>) to the resting state, optionally
    /// after a delay (it is hidden from the start, so a stagger never flashes the final state).</summary>
    private static async Task EnterAsync(Visual target, TransformOperations start, double milliseconds, CancellationToken ct, int delayMs = 0)
    {
        if (Reduced)
        {
            return;
        }

        target.Opacity = 0; // the first frame renders before the animation's first tick
        target.RenderTransform = start;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = EaseOut,
            FillMode = FillMode.Forward, // the end state is committed in the same tick: no blank frame
            Children =
            {
                Frame(0, (Visual.OpacityProperty, 0d), (Visual.RenderTransformProperty, start)),
                Frame(1, (Visual.OpacityProperty, 1d), (Visual.RenderTransformProperty, TransformOperations.Identity)),
            },
        };

        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            await animation.RunAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            // superseded; resting values are restored below
        }
        finally
        {
            // Always, even if the animation was torn down early (when Avalonia's final fill can
            // commit 0): an entrance must never leave content invisible.
            target.Opacity = 1;
            target.RenderTransform = null;
        }
    }

    /// <summary>An in-place effect that starts and ends on the resting values (no fill).</summary>
    private static async Task PlayAsync(Visual target, double milliseconds, Easing easing, CancellationToken ct,
        params (double Cue, AvaloniaProperty Property, object Value)[] frames)
    {
        if (Reduced)
        {
            return;
        }

        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing };
        foreach (var cue in frames.GroupBy(f => f.Cue).OrderBy(g => g.Key))
        {
            animation.Children.Add(Frame(cue.Key, cue.Select(f => (f.Property, f.Value)).ToArray()));
        }

        try
        {
            await animation.RunAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            // stopped early: with no fill the element is already on its resting values
        }
    }

    private static KeyFrame Frame(double cue, params (AvaloniaProperty Property, object Value)[] setters)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        foreach ((AvaloniaProperty property, object value) in setters)
        {
            frame.Setters.Add(new Setter(property, value));
        }

        return frame;
    }

    private static bool DetectReduced()
    {
        string? forced = Environment.GetEnvironmentVariable("XAULTWALLET_REDUCE_MOTION");
        if (!string.IsNullOrEmpty(forced))
        {
            return forced is not ("0" or "false" or "False");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0) && enabled == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}

/// <summary>
/// Entrances declared in XAML and played from code when the element is attached:
/// <c>app:Entrance.Rise="70"</c> rises the element into place after 70 ms, <c>app:Entrance.Fade="300"</c>
/// fades it in after 300 ms. Not style animations on purpose: Avalonia 11.1 commits default(T) as a
/// LOCAL value when a FillMode.Forward style animation is torn down before its first frame, so a
/// style entrance (opacity 0 → 1) could leave a whole section invisible for good.
/// </summary>
public sealed class Entrance
{
    public static readonly AttachedProperty<int> RiseProperty =
        AvaloniaProperty.RegisterAttached<Entrance, Visual, int>("Rise", defaultValue: -1);

    public static readonly AttachedProperty<int> FadeProperty =
        AvaloniaProperty.RegisterAttached<Entrance, Visual, int>("Fade", defaultValue: -1);

    static Entrance()
    {
        RiseProperty.Changed.AddClassHandler<Visual>((v, _) => Watch(v));
        FadeProperty.Changed.AddClassHandler<Visual>((v, _) => Watch(v));
    }

    private Entrance()
    {
    }

    public static int GetRise(Visual element) => element.GetValue(RiseProperty);

    public static void SetRise(Visual element, int delayMs) => element.SetValue(RiseProperty, delayMs);

    public static int GetFade(Visual element) => element.GetValue(FadeProperty);

    public static void SetFade(Visual element, int delayMs) => element.SetValue(FadeProperty, delayMs);

    /// <summary>True when the element has an entrance (the UI smoke test checks they all settle).</summary>
    public static bool HasEntrance(Visual element) => GetRise(element) >= 0 || GetFade(element) >= 0;

    private static void Watch(Visual element)
    {
        element.AttachedToVisualTree -= OnAttached;
        element.AttachedToVisualTree += OnAttached;
    }

    // Attaching happens during layout, before the element is first rendered: hidden from frame one.
    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Visual element)
        {
            return;
        }

        if (GetRise(element) is var rise and >= 0)
        {
            _ = Motion.RiseInAsync(element, distance: 12, milliseconds: 420, delayMs: rise);
        }
        else if (GetFade(element) is var fade and >= 0)
        {
            _ = Motion.FadeInAsync(element, milliseconds: 450, delayMs: fade);
        }
    }
}

/// <summary>
/// Screen change: the old screen fades out quickly while the new one fades in rising into place.
/// Same contract as Avalonia's CrossFade: the old presenter ends hidden.
/// </summary>
public sealed class FadeLiftTransition : IPageTransition
{
    private static readonly Animation FadeOut = new()
    {
        Duration = TimeSpan.FromMilliseconds(170),
        Easing = new CubicEaseIn(),
        // Ends committed at 0 in the same tick, so the old screen can't reappear for a frame before
        // it is hidden. (The presenter is recycled: the next entrance sets its own starting values.)
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
        },
    };

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (Motion.Reduced)
        {
            if (to is not null)
            {
                to.IsVisible = true;
                to.Opacity = 1;
            }

            if (from is not null)
            {
                from.IsVisible = false;
            }

            return;
        }

        var tasks = new List<Task>();
        if (from is not null)
        {
            tasks.Add(FadeOut.RunAsync(from, cancellationToken));
        }

        if (to is not null)
        {
            to.IsVisible = true;
            tasks.Add(Motion.RiseInAsync(to, distance: 14, milliseconds: 340, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return; // a newer screen change took over
        }

        if (from is not null && !cancellationToken.IsCancellationRequested)
        {
            from.IsVisible = false;
        }
    }
}
