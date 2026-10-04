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

    /// <summary>Pick an item of a combo box by index.</summary>
    Task SelectAsync(string id, int index);

    /// <summary>Set a toggle switch, check box or radio button.</summary>
    Task SetCheckedAsync(string id, bool on);

    /// <summary>The text an element shows (text box value, text block, or accessible name).</summary>
    Task<string> ReadTextAsync(string id);

    /// <summary>Every piece of text inside a container, in reading order.</summary>
    Task<IReadOnlyList<string>> ReadTextsAsync(string id);

    /// <summary>Save a screenshot of the app window as &lt;name&gt;.png.</summary>
    Task ScreenshotAsync(string name);

    /// <summary>Visible automation ids with their text: printed when a step fails.</summary>
    Task<string> DescribeScreenAsync();
}
