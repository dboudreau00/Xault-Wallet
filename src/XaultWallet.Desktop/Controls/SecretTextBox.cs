using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace XaultWallet.Desktop.Controls;

/// <summary>
/// A password box that does not hand its text to other programs. Avalonia's TextBox reports its full
/// Text through UI Automation's Value pattern even when it is masked with PasswordChar, so any process
/// in the same desktop session could read a vault password as it is typed, with no hooks and no
/// injection (WPF's PasswordBox refuses this). Input through automation (SetValue) still works, so
/// assistive tools and the end-to-end tests can type into it; reading it back returns nothing.
/// </summary>
public sealed class SecretTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new SecretTextBoxPeer(this);

    // Re-implements IValueProvider so the value getter is ours; IsReadOnly and SetValue stay the
    // TextBox peer's.
    private sealed class SecretTextBoxPeer(TextBox owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        string? IValueProvider.Value => null;
    }
}
