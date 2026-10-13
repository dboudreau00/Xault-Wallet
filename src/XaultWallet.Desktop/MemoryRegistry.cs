using XaultWallet.Core.Companion;

namespace XaultWallet.Desktop;

/// <summary>A registry that lives in memory, for the screenshot tool and the in-process tests: what
/// they do in Settings never reaches the real HKEY_CURRENT_USER.</summary>
internal sealed class MemoryRegistry : IUserRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string subKey, string name) =>
        _keys.TryGetValue(subKey, out Dictionary<string, string>? values) && values.TryGetValue(name, out string? value) ? value : null;

    public void SetValue(string subKey, string name, string value)
    {
        if (!_keys.TryGetValue(subKey, out Dictionary<string, string>? values))
        {
            _keys[subKey] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        values[name] = value;
    }

    public void DeleteTree(string subKey)
    {
        foreach (string key in _keys.Keys.Where(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase) || k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(key);
        }
    }
}
