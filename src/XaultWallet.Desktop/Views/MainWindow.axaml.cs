using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace XaultWallet.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Style animations (entrances, pulses, spinners) are scoped to Window.motion.
        if (!Motion.Reduced)
        {
            Classes.Add("motion");
        }

        // Reset the auto-lock inactivity timer on any pointer or keyboard input.
        // Tunnel so we see the events regardless of what handles them downstream.
        AddHandler(PointerMovedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnUserActivity, RoutingStrategies.Tunnel);
    }

    private void OnUserActivity(object? sender, RoutedEventArgs e) =>
        (DataContext as ViewModels.MainWindowViewModel)?.NotifyActivity();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToScreen();
    }

    /// <summary>
    /// The default size doesn't fit every screen: a 1366x768 laptop at 125 % scaling has about
    /// 1090x570 usable. Shrink to the working area (never below the minimum) and centre, so the
    /// title bar and the bottom of each screen stay reachable.
    /// </summary>
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        PixelRect work = screen.WorkingArea;
        double scale = screen.Scaling;
        double maxWidth = (work.Width / scale) - 24;
        double maxHeight = (work.Height / scale) - 24;
        if (Width <= maxWidth && Height <= maxHeight)
        {
            return;
        }

        Width = Math.Max(MinWidth, Math.Min(Width, maxWidth));
        Height = Math.Max(MinHeight, Math.Min(Height, maxHeight));
        Position = new PixelPoint(
            work.X + Math.Max(0, (int)((work.Width - (Width * scale)) / 2)),
            work.Y + Math.Max(0, (int)((work.Height - (Height * scale)) / 2)));
    }

    // Drag the window by its custom title bar.
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // Double-click the title bar toggles maximize, like a native window.
    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
