using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace XaultWallet.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
