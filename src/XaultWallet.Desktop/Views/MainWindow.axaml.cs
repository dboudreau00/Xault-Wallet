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

    private bool _maximizeOnOpen;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToScreen();
        if (_maximizeOnOpen)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Open at the size the window had when it was last closed (still fitted to the screen).</summary>
    public void RestorePlacement(AppSettings settings)
    {
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }

        _maximizeOnOpen = settings.WindowMaximized;
    }

    /// <summary>Note the window's size for next time (saved with the other settings; best effort).</summary>
    public void RememberPlacement(AppSettings settings)
    {
        try
        {
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Math.Round(ClientSize.Width);
                settings.WindowHeight = Math.Round(ClientSize.Height);
            }

            AppServices.Instance.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A window size is not worth a failed shutdown.
        }
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

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
