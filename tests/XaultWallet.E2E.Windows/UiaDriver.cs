using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace XaultWallet.E2E;

/// <summary>
/// Drives the real XaultWallet.exe from outside its process through Windows UI Automation, the
/// interface screen readers use. Elements are found by AutomationId in the RAW view of the tree, so
/// containers (a Border with an id) and every text element are visible to the test, not only the
/// "control" elements. Actions go through each control's automation pattern (Invoke, Value, Toggle,
/// SelectionItem, ExpandCollapse), which Avalonia implements by performing the control's own click
/// or text update; the real mouse pointer is moved onto every control first, so hover states show in
/// the screenshots and the app's inactivity auto-lock sees a person at work.
/// </summary>
/// <remarks>
/// FlaUI keeps the active cache request in thread-static state: no <c>await</c> inside an
/// <c>Activate()</c> scope, or the continuation runs on another thread without it.
/// </remarks>
internal sealed class UiaDriver : IAppDriver
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(90);
    private readonly UIA3Automation _uia;
    private readonly Window _window;
    private readonly int _processId;
    private readonly string _screenshots;
    private readonly Action<string> _log;
    private readonly CacheRequest _raw;

    public UiaDriver(UIA3Automation uia, Window window, int processId, string screenshots, Action<string> log)
    {
        _uia = uia;
        _window = window;
        _processId = processId;
        _screenshots = screenshots;
        _log = log;

        // Raw view (TrueCondition): FlaUI searches with the active request's TreeFilter. The default
        // without a request is the CONTROL view, which leaves out containers and template text.
        _raw = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            TreeFilter = TrueCondition.Default,
            AutomationElementMode = AutomationElementMode.Full,
        };
        var p = uia.PropertyLibrary.Element;
        foreach (var id in new[] { p.AutomationId, p.Name, p.ControlType, p.IsEnabled, p.IsOffscreen, p.BoundingRectangle })
        {
            _raw.Add(id);
        }
    }

    private ConditionFactory Cf => _uia.ConditionFactory;

    // ---------------------------------------------------------------- IAppDriver

    public async Task WaitForAsync(string id, TimeSpan timeout, bool enabled = true) =>
        await WaitForHitAsync(id, timeout, enabled);

    public Task<bool> IsVisibleAsync(string id) => Task.FromResult(Find(id) is not null);

    public async Task ClickAsync(string id)
    {
        Hit hit = await WaitForHitAsync(id, DefaultWait, enabled: true);
        PointAt(hit);
        string how = Act(id, () =>
        {
            AutomationElement e = hit.Element;
            if (e.Patterns.Invoke.TryGetPattern(out var invoke))
            {
                invoke.Invoke();
                return "invoke";
            }

            if (e.Patterns.SelectionItem.TryGetPattern(out var item))
            {
                item.Select(); // tab items, radio buttons
                return "select";
            }

            if (e.Patterns.Toggle.TryGetPattern(out var toggle))
            {
                toggle.Toggle();
                return "toggle";
            }

            Mouse.Click(Center(hit.Element.BoundingRectangle)); // nothing else to go through
            return "mouse";
        });
        _log($"    click {id} ({how})");
        await SettleAsync();
    }

    public async Task TypeAsync(string id, string text)
    {
        Hit hit = await WaitForHitAsync(id, DefaultWait, enabled: true);
        PointAt(hit);
        string now = Act(id, () =>
        {
            AutomationElement e = hit.Element;
            e.Focus(); // a person clicks into the box first
            var value = e.Patterns.Value.Pattern;
            value.SetValue(text);
            return value.Value.Value ?? string.Empty;
        });
        await SettleAsync();

        // Password boxes accept input through automation but never read back (SecretTextBox): the
        // scenario checks what typing them achieved instead (strength meter, unlock).
        bool secret = id.Contains("Password", StringComparison.Ordinal);
        if (secret ? now.Length != 0 : now != text)
        {
            throw new InvalidOperationException(secret
                ? $"'{id}' hands its text to UI Automation; a password box must not."
                : $"typed into '{id}' but it holds \"{now}\".");
        }
    }

    public async Task SelectAsync(string id, string item, bool navigates = false)
    {
        Hit hit = await WaitForHitAsync(id, DefaultWait, enabled: true);
        PointAt(hit);
        string Current() => Act(id, () => hit.Element.Patterns.Value.Pattern.Value.Value ?? string.Empty);
        if (Current() == item)
        {
            _log($"    {id} already reads \"{item}\"");
            return;
        }

        // The drop-down is a top-level popup window of the app's own process.
        Act(id, () => hit.Element.Patterns.ExpandCollapse.Pattern.Expand());
        AutomationElement option = await PollAsync(() => FindPopupItem(item), TimeSpan.FromSeconds(15), $"\"{item}\" in the '{id}' drop-down");
        Act(id, () => option.Patterns.SelectionItem.Pattern.Select());
        if (navigates)
        {
            // The choice replaced the screen: the combo box (and its drop-down) went with it.
            try { hit.Element.Patterns.ExpandCollapse.Pattern.Collapse(); } catch (Exception ex) when (IsUiaFailure(ex)) { }
            await SettleAsync();
            return;
        }

        Act(id, () => hit.Element.Patterns.ExpandCollapse.Pattern.Collapse());
        await SettleAsync();
        if (Current() != item)
        {
            throw new InvalidOperationException($"'{id}' reads \"{Current()}\" after selecting \"{item}\".");
        }
    }

    public async Task SetCheckedAsync(string id, bool on)
    {
        Hit hit = await WaitForHitAsync(id, DefaultWait, enabled: true);
        PointAt(hit);
        bool IsOn() => Act(id, () => hit.Element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On);
        if (IsOn() != on)
        {
            Act(id, () => hit.Element.Patterns.Toggle.Pattern.Toggle());
            await SettleAsync();
        }

        if (IsOn() != on)
        {
            throw new InvalidOperationException($"toggling '{id}' did not set it to {on}.");
        }
    }

    public Task<string> ReadTextAsync(string id)
    {
        Hit hit = Find(id) ?? throw new InvalidOperationException($"'{id}' is not on screen.");
        return Task.FromResult(Act(id, () => AccessibleText(hit)).Trim());
    }

    public Task<IReadOnlyList<string>> ReadTextsAsync(string id)
    {
        Hit hit = Find(id) ?? throw new InvalidOperationException($"'{id}' is not on screen.");
        return Task.FromResult<IReadOnlyList<string>>(Act(id, () => TextElements(hit.Element)));
    }

    public async Task ScreenshotAsync(string name)
    {
        await Task.Delay(900); // entrances (staggered up to ~0.6 s)
        Directory.CreateDirectory(_screenshots);
        using CaptureImage image = Capture.Element(_window);
        image.ToFile(Path.Combine(_screenshots, name + ".png"));
    }

    public Task<string> DescribeScreenAsync()
    {
        var sb = new StringBuilder("Visible elements (UI Automation):\n");
        List<Hit> hits;
        using (_raw.Activate())
        {
            hits = _window.FindAll(TreeScope.Descendants, TrueCondition.Default)
                .Select(ToHit)
                .Where(h => h.Shown && h.Id.Length > 0)
                .ToList();
        }

        foreach (Hit h in hits)
        {
            string text;
            try
            {
                text = AccessibleText(h).Trim();
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                text = "(" + ex.GetType().Name + ")";
            }

            sb.Append("  ").Append(h.Id).Append(" [").Append(h.Type).Append(']')
              .Append(h.Enabled ? string.Empty : " (disabled)")
              .Append(text.Length > 0 ? ": " + (text.Length > 140 ? text[..140] + "…" : text) : string.Empty)
              .Append('\n');
        }

        return Task.FromResult(sb.ToString());
    }

    // ---------------------------------------------------------------- lookup

    /// <summary>One element with the properties read in the same round trip (from the cache).</summary>
    private sealed record Hit(AutomationElement Element, string Id, string Name, ControlType Type, bool Enabled, bool Shown);

    private static Hit ToHit(AutomationElement e)
    {
        Rectangle r = e.Properties.BoundingRectangle.ValueOrDefault;
        return new Hit(
            e,
            e.Properties.AutomationId.ValueOrDefault ?? string.Empty,
            e.Properties.Name.ValueOrDefault ?? string.Empty,
            e.Properties.ControlType.ValueOrDefault,
            e.Properties.IsEnabled.ValueOrDefault,
            !e.Properties.IsOffscreen.ValueOrDefault && r.Width > 0 && r.Height > 0);
    }

    /// <summary>The first shown element with this id. Hidden controls aren't in the tree at all
    /// (Avalonia leaves IsVisible=false subtrees out); IsOffscreen and an empty box rule out the rest.</summary>
    private Hit? Find(string id)
    {
        try
        {
            using (_raw.Activate())
            {
                return _window.FindAll(TreeScope.Descendants, Cf.ByAutomationId(id)).Select(ToHit).FirstOrDefault(h => h.Shown);
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return null; // the element went away mid-query (screen change): not there
        }
    }

    private async Task<Hit> WaitForHitAsync(string id, TimeSpan timeout, bool enabled)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Hit? hit = Find(id);
            if (hit is not null && (!enabled || hit.Enabled))
            {
                return hit;
            }

            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException(hit is null
                    ? $"'{id}' did not appear within {timeout.TotalSeconds:0}s."
                    : $"'{id}' stayed disabled for {timeout.TotalSeconds:0}s.");
            }

            await Task.Delay(150);
        }
    }

    private AutomationElement? FindPopupItem(string name)
    {
        using (_raw.Activate())
        {
            foreach (AutomationElement top in _uia.GetDesktop().FindAllChildren(Cf.ByProcessId(_processId)))
            {
                AutomationElement? item = top.FindFirst(TreeScope.Descendants, Cf.ByControlType(ControlType.ListItem).And(Cf.ByName(name)));
                if (item is not null)
                {
                    return item;
                }
            }
        }

        return null;
    }

    /// <summary>The value of a text box / combo box, else the accessible name, else the text elements
    /// inside (the same rule the headless driver applies to Avalonia's automation peers).</summary>
    private string AccessibleText(Hit hit)
    {
        if (hit.Type is ControlType.Edit or ControlType.ComboBox && hit.Element.Patterns.Value.TryGetPattern(out var value))
        {
            return value.Value.Value ?? string.Empty;
        }

        string name = hit.Element.Properties.Name.ValueOrDefault ?? string.Empty; // live, not the cached copy
        return name.Length > 0 ? name : string.Join(" ", TextElements(hit.Element));
    }

    /// <summary>Names of the Text elements in the subtree (raw view), in tree order.</summary>
    private List<string> TextElements(AutomationElement root)
    {
        using (_raw.Activate())
        {
            return root.FindAll(TreeScope.Subtree, Cf.ByControlType(ControlType.Text))
                .Select(ToHit)
                .Where(h => h.Shown)
                .Select(h => h.Name.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Scroll the element into view and put the real pointer on it (best effort).</summary>
    private static void PointAt(Hit hit)
    {
        try
        {
            if (hit.Element.Patterns.ScrollItem.TryGetPattern(out var scroll))
            {
                scroll.ScrollIntoView();
            }

            Mouse.MoveTo(Center(hit.Element.BoundingRectangle));
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            // cosmetic only
        }
    }

    private static Point Center(Rectangle r) => new(r.Left + (r.Width / 2), r.Top + (r.Height / 2));

    /// <summary>Run a UI Automation call; failures surface as InvalidOperationException naming the
    /// element (the scenario's retry loops expect that type).</summary>
    private static T Act<T>(string id, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            throw new InvalidOperationException($"'{id}': {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private static void Act(string id, Action call) => Act(id, () =>
    {
        call();
        return true;
    });

    /// <summary>What UI Automation throws when an element changed or went away under a call
    /// (FlaUI's "not supported" exceptions derive from FlaUIException).</summary>
    private static bool IsUiaFailure(Exception ex) =>
        ex is COMException or FlaUIException or System.NotSupportedException or TimeoutException or ArgumentException;

    private static async Task<T> PollAsync<T>(Func<T?> probe, TimeSpan timeout, string what)
        where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (probe() is { } found)
            {
                return found;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"{what} did not appear within {timeout.TotalSeconds:0}s.");
    }

    private static Task SettleAsync() => Task.Delay(250);
}
