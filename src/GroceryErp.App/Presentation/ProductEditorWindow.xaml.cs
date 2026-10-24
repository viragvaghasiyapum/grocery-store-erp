using System.Windows;
using GroceryErp.Catalog;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>Creates a product (optionally with its first lot) or edits one outside the Products panel.</summary>
public partial class ProductEditorWindow : Window
{
    private readonly CatalogService _catalog;
    private readonly long? _productId;

    public ProductEditorWindow(CatalogService catalog, long? productId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _productId = productId;
        InitializeComponent();
        if (productId is not null) TitleText.Text = TranslationSource.Instance["Catalog_EditorTitle"];
        Loaded += async (_, _) =>
        {
            IServiceProvider? services = (Application.Current as App)?.ServicesOrNull;
            DateOnly today = services?.GetService<IClock>()?.BusinessDate ?? DateOnly.FromDateTime(DateTime.Today);
            bool regular = services?.GetService<ISettings>()?.Current.TaxMode == "REGULAR";
            await Form.LoadAsync(_catalog, _productId, today, regular);
            SaveButton.IsEnabled = Form.CanSave;
            Form.FocusFirstField();
        };
    }

    /// <summary>Gets the saved product's ID.</summary>
    public long? SavedProductId { get; private set; }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            SavedProductId = await Form.SaveAsync();
            if (SavedProductId is not null) DialogResult = true;
        }
        finally
        {
            if (IsLoaded) SaveButton.IsEnabled = Form.CanSave;
        }
    }
}
