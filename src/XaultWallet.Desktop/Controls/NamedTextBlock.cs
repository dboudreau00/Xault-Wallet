using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace XaultWallet.Desktop.Controls;

/// <summary>
/// A <see cref="TextBlock"/> whose accessible name honours <c>AutomationProperties.Name</c>.
/// Avalonia 11.1's TextBlock peer always reports <see cref="TextBlock.Text"/> as the name and ignores
/// AutomationProperties.Name, and Text is null once the content is made of Runs. So the balance
/// (drawn as two Runs: whole part bright, fraction dimmed) was announced as nothing at all by screen
/// readers. Styled exactly like a TextBlock.
/// </summary>
public sealed class NamedTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override AutomationPeer OnCreateAutomationPeer() => new NamedTextBlockPeer(this);

    private sealed class NamedTextBlockPeer(NamedTextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            string? name = AutomationProperties.GetName(Owner);
            return string.IsNullOrEmpty(name) ? base.GetNameCore() ?? Owner.Inlines?.Text : name;
        }
    }
}
