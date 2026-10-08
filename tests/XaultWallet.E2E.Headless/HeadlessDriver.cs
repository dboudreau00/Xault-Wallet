using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace XaultWallet.E2E;

/// <summary>
/// Drives the real MainWindow in-process through real input: pointer clicks at an element's centre
/// (so hit-testing applies, and an overlay covering a button fails the click just as it would a
/// person's) and keyboard text input. Must run on the UI thread; the host keeps the dispatcher pumped.
/// </summary>
internal sealed class HeadlessDriver : IAppDriver
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(45);
    private readonly Window _window;
    private readonly string _screenshots;

    public HeadlessDriver(Window window, string screenshots)
    {
        _window = window;
        _screenshots = screenshots;
    }

    public async Task WaitForAsync(string id, TimeSpan timeout, bool enabled = true)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Control? c = Find(id);
            if (c is not null && (!enabled || c.IsEffectivelyEnabled))
            {
                return;
            }

            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException(c is null
                    ? $"'{id}' did not appear within {timeout.TotalSeconds:0}s."
                    : $"'{id}' stayed disabled for {timeout.TotalSeconds:0}s.");
            }

            await Task.Delay(50);
        }
    }

    public Task<bool> IsVisibleAsync(string id) => Task.FromResult(Find(id) is not null);

    public async Task ClickAsync(string id)
    {
        await WaitForAsync(id, DefaultWait);
        Find(id)!.BringIntoView();
        await SettleAsync();

        Control c = Find(id) ?? throw new InvalidOperationException($"'{id}' disappeared before it could be clicked.");
        Point center = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), _window)
                       ?? throw new InvalidOperationException($"'{id}' is not inside the window.");
        if (center.X < 0 || center.Y < 0 || center.X > _window.Bounds.Width || center.Y > _window.Bounds.Height)
        {
            throw new InvalidOperationException($"'{id}' is outside the visible window at {center}.");
        }

        _window.MouseMove(center, RawInputModifiers.None);
        _window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        _window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        await SettleAsync();
    }

    public async Task TypeAsync(string id, string text)
    {
        await ClickAsync(id); // focus it the way a person does
        if (Find(id) is not TextBox box)
        {
            throw new InvalidOperationException($"'{id}' is not a text box.");
        }

        if (!box.IsFocused)
        {
            box.Focus();
        }

        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); // select all, then type over it
        _window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
        _window.KeyTextInput(text);
        await SettleAsync();

        if ((box.Text ?? string.Empty) != text)
        {
            throw new InvalidOperationException(
                $"typed into '{id}' but it holds \"{(box.PasswordChar == default ? box.Text : "(hidden)")}\".");
        }
    }

    public async Task SelectAsync(string id, string item, bool navigates = false)
    {
        await WaitForAsync(id, DefaultWait);
        if (Find(id) is not ComboBox combo)
        {
            throw new InvalidOperationException($"'{id}' is not a combo box.");
        }

        int index = combo.Items.Cast<object?>().ToList().FindIndex(i => (i is ContentControl cc ? cc.Content : i)?.ToString() == item);
        if (index < 0)
        {
            throw new InvalidOperationException($"'{id}' has no item \"{item}\".");
        }

        // Keyboard, as with Tab + arrow keys. A closed combo box moves its selection with Up/Down —
        // except the wallet switcher, whose arrows open its list (choosing a wallet starts it): there
        // the arrows move through the list and Enter chooses.
        combo.Focus();
        await SettleAsync();
        for (int guard = 0; guard < 20; guard++)
        {
            if (navigates && TopLevel.GetTopLevel(combo) is null)
            {
                break; // the choice replaced the screen
            }

            if (!combo.IsDropDownOpen && combo.SelectedIndex == index)
            {
                break;
            }

            int focused = combo.GetRealizedContainers().FirstOrDefault(c => c.IsFocused) is { } entry ? combo.IndexFromContainer(entry) : -1;
            PhysicalKey key = !combo.IsDropDownOpen
                ? (combo.SelectedIndex > index ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown)
                : focused == index ? PhysicalKey.Enter
                : focused > index ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown;
            _window.KeyPressQwerty(key, RawInputModifiers.None);
            _window.KeyReleaseQwerty(key, RawInputModifiers.None);
            await SettleAsync();
        }

        if (navigates && TopLevel.GetTopLevel(combo) is null)
        {
            return; // gone with its screen: nothing to read back (the caller checks the new screen)
        }

        if (combo.SelectedIndex != index)
        {
            throw new InvalidOperationException($"'{id}' stayed at item {combo.SelectedIndex}, wanted {index}.");
        }
    }

    public async Task SetCheckedAsync(string id, bool on)
    {
        await WaitForAsync(id, DefaultWait);
        if (Find(id) is not ToggleButton toggle)
        {
            throw new InvalidOperationException($"'{id}' is not a toggle.");
        }

        if (toggle.IsChecked != on)
        {
            await ClickAsync(id);
        }

        if (toggle.IsChecked != on)
        {
            throw new InvalidOperationException($"clicking '{id}' did not set it to {on}.");
        }
    }

    // Text is read through Avalonia's automation peers: the tree its Windows UI Automation provider
    // serves to screen readers and to the Windows driver. Both drivers therefore read the same thing,
    // and text a screen reader can't get (an empty accessible name) fails here on Linux as well.

    public Task<string> ReadTextAsync(string id)
    {
        Control c = Find(id) ?? throw new InvalidOperationException($"'{id}' is not on screen.");
        return Task.FromResult(AccessibleText(Peer(c)).Trim());
    }

    public Task<IReadOnlyList<string>> ReadTextsAsync(string id)
    {
        Control c = Find(id) ?? throw new InvalidOperationException($"'{id}' is not on screen.");
        return Task.FromResult<IReadOnlyList<string>>(TextElements(Peer(c)).ToList());
    }

    public async Task ScreenshotAsync(string name)
    {
        await SettleAsync(30); // let entrances finish (staggered up to ~0.6 s)
        Directory.CreateDirectory(_screenshots);
        using var frame = _window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(_screenshots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    public Task<string> DescribeScreenAsync()
    {
        var sb = new StringBuilder("Visible elements:\n");
        foreach (Control c in _window.GetVisualDescendants().OfType<Control>().Where(IsShown))
        {
            string? id = AutomationProperties.GetAutomationId(c);
            if (!string.IsNullOrEmpty(id))
            {
                string text = AccessibleText(Peer(c)).Trim();
                sb.Append("  ").Append(id).Append(c.IsEffectivelyEnabled ? "" : " (disabled)")
                  .Append(text.Length > 0 ? ": " + (text.Length > 140 ? text[..140] + "…" : text) : "").Append('\n');
            }
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>The headless platform's clipboard lives in this process, not the system's: the Windows
    /// driver checks the real one.</summary>
    public Task<ClipboardContent?> ReadClipboardAsync() => Task.FromResult<ClipboardContent?>(null);

    // ---------------------------------------------------------------- lookup

    private Control? Find(string id) =>
        _window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id && IsShown(c));

    /// <summary>Visible to a person: effectively visible, laid out, and not faded out (a screen
    /// leaving through a transition keeps its controls in the tree for a moment).</summary>
    private static bool IsShown(Control c) =>
        c.IsEffectivelyVisible
        && c.Bounds.Width > 0 && c.Bounds.Height > 0
        && c.GetSelfAndVisualAncestors().OfType<Visual>().All(v => v.Opacity > 0.01);

    private static AutomationPeer Peer(Control c) => ControlAutomationPeer.CreatePeerForElement(c);

    /// <summary>Value (text box, combo box), else accessible name, else the text elements inside.</summary>
    private static string AccessibleText(AutomationPeer peer)
    {
        if (peer.GetProvider<IValueProvider>() is { } value)
        {
            return value.Value ?? string.Empty;
        }

        string name = peer.GetName();
        return name.Length > 0 ? name : string.Join(" ", TextElements(peer));
    }

    /// <summary>Names of the Text elements in the subtree, in tree order (hidden parts aren't in it).</summary>
    private static IEnumerable<string> TextElements(AutomationPeer peer)
    {
        if (peer.GetAutomationControlType() == AutomationControlType.Text && peer.GetName().Trim() is { Length: > 0 } name)
        {
            yield return name;
        }

        foreach (AutomationPeer child in peer.GetChildren())
        {
            foreach (string text in TextElements(child))
            {
                yield return text;
            }
        }
    }

    private static async Task SettleAsync(int frames = 4)
    {
        for (int i = 0; i < frames; i++)
        {
            await Task.Delay(30);
        }
    }
}
