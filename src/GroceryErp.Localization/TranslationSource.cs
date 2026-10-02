using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace GroceryErp.Localization;

/// <summary>Provides runtime-switchable translations while keeping formatting fixed to English (India).</summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    private static readonly HashSet<string> SupportedCultures = new(StringComparer.Ordinal)
    {
        "en-IN",
        "gu-IN",
    };

    private readonly Dictionary<string, ResourceManager> _managers = new(StringComparer.Ordinal);
    private CultureInfo _uiCulture = CultureInfo.GetCultureInfo("en-IN");
    private bool _pseudoLocalizationEnabled;

    private TranslationSource()
    {
        RegisterShared(SharedStrings.ResourceManager);
    }

    /// <summary>Gets the process-wide translation source used by XAML bindings.</summary>
    public static TranslationSource Instance { get; } = new();

    /// <summary>Gets the fixed culture used for every numeric value.</summary>
    public static CultureInfo FormatCulture { get; } = CultureInfo.GetCultureInfo("en-IN");

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool PseudoLocalizationEnabled
    {
        get => _pseudoLocalizationEnabled;
        set
        {
            if (_pseudoLocalizationEnabled == value) return;
            _pseudoLocalizationEnabled = value;
            OnPropertyChanged("Item[]");
            OnPropertyChanged();
        }
    }

    /// <summary>Gets or sets the current supported UI culture.</summary>
    public CultureInfo UiCulture
    {
        get => _uiCulture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SupportedCultures.Contains(value.Name))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value.Name, "Unsupported UI culture.");
            }

            if (Equals(_uiCulture, value))
            {
                return;
            }

            _uiCulture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            OnPropertyChanged("Item[]");
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the translated value for a stable resource key.</summary>
    public string this[string key] => Get(key, _uiCulture);

    /// <summary>Registers shared strings under the shared application and module-independent prefixes.</summary>
    public void RegisterShared(ResourceManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        foreach (string prefix in new[] { "Common", "Error", "Validation", "Enum", "Print", "Pnl", "App", "Shell", "Demo", "Setup", "Seed", "Login", "Lock", "Startup", "Settings", "ReleaseNotes", "Catalog", "Purchasing", "Stock", "Sales", "Pos", "Receipt", "Dashboard", "MoneyOut", "Assets", "Reports" })
        {
            _managers[prefix] = manager;
        }
    }

    /// <summary>Registers a module resource manager by its stable prefix.</summary>
    public void Register(string prefix, ResourceManager manager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(manager);
        _managers[prefix] = manager;
    }

    /// <summary>Gets a translation in an explicitly supplied supported culture.</summary>
    public string Get(string key, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        string prefix = key.Split('_', 2)[0];
        string translated = _managers.TryGetValue(prefix, out ResourceManager? manager)
            ? manager.GetString(key, culture) ?? $"⟦{key}⟧"
            : $"⟦{key}⟧";
        return _pseudoLocalizationEnabled ? PseudoLocalizer.Transform(translated) : translated;
    }

    /// <summary>Formats translated placeholders with the fixed English-number culture.</summary>
    public string Format(string key, params object[] args) =>
        string.Format(FormatCulture, this[key], args);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

