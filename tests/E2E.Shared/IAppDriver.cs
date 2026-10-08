namespace XaultWallet.E2E;

/// <summary>
/// What an end-to-end scenario can do to the running app: the same verbs a person has. Elements are
/// addressed by their UI Automation id (AutomationProperties.AutomationId in the XAML), the interface
/// screen readers use too. Two implementations: in-process headless (Linux, fast) and Windows UI
/// Automation against the real published XaultWallet.exe.
/// </summary>
public interface IAppDriver
{
    /// <summary>Wait until the element exists, is visible and (unless <paramref name="enabled"/> is
    /// false) enabled.</summary>
    Task WaitForAsync(string id, TimeSpan timeout, bool enabled = true);

    /// <summary>Visible right now (no waiting).</summary>
    Task<bool> IsVisibleAsync(string id);

    /// <summary>Click like a user: the element is scrolled into view and clicked in its centre.</summary>
    Task ClickAsync(string id);

    /// <summary>Replace the text of a text box by typing.</summary>
    Task TypeAsync(string id, string text);

    /// <summary>Pick the combo box item that reads <paramref name="item"/>. With
    /// <paramref name="navigates"/>, choosing it replaces the screen the combo box is on (the wallet
    /// switcher): the box goes away with it, so it isn't collapsed or read back.</summary>
    Task SelectAsync(string id, string item, bool navigates = false);

    /// <summary>Set a toggle switch, check box or radio button.</summary>
    Task SetCheckedAsync(string id, bool on);

    /// <summary>What a screen reader gets for the element: the value of a text box or combo box,
    /// else its accessible name, else the text elements inside it joined by spaces.</summary>
    Task<string> ReadTextAsync(string id);

    /// <summary>Every text element inside a container (accessible names), in reading order.</summary>
    Task<IReadOnlyList<string>> ReadTextsAsync(string id);

    /// <summary>Save a screenshot of the app window as &lt;name&gt;.png.</summary>
    Task ScreenshotAsync(string name);

    /// <summary>Visible automation ids with their text: printed when a step fails.</summary>
    Task<string> DescribeScreenAsync();

    /// <summary>What is on the system clipboard, or null where the driver can't see the system
    /// clipboard (the headless platform keeps its own, in process).</summary>
    Task<ClipboardContent?> ReadClipboardAsync();
}

/// <summary>The clipboard's text, and whether it is marked to stay out of clipboard history and the
/// cloud clipboard; <paramref name="Marks"/> spells out the marks that were found.</summary>
public sealed record ClipboardContent(string Text, bool KeptOutOfHistory, string Marks);
