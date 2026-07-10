using Avalonia.Controls;
using Avalonia.Controls.Templates;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop;

/// <summary>Resolves a View for a given ViewModel by naming convention (…ViewModel → …View).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null)
        {
            return new TextBlock { Text = "null" };
        }

        string name = data.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        Type? type = Type.GetType(name);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
