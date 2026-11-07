using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GroceryErp.Catalog;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;
using GroceryErp.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

// Asked when units come back after their product was deleted: from a customer return, a cancelled
// vendor return or a damage correction. The units go into a new lot of an existing active product
// of the same type, or of a new product prefilled from the saved details; nothing is guessed, and
// each detail the original record did not save says so. Quantity and booked cost never change.
public sealed class RestoreProductWindow : Window
{
    private readonly RestoreDraft _draft;
    private readonly RestoreOptions _options;
    private readonly bool _regularTax;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ProductListItem>>> _search;
    private readonly Func<string, Task<bool>> _skuInUse;
    private readonly FormErrorSummary _summary = new();
    private readonly StackPanel _content = new();
    private readonly RadioButton _useExisting = new() { GroupName = "restore", IsChecked = true, Margin = new Thickness(0, 4, 0, 6) };
    private readonly RadioButton _createNew = new() { GroupName = "restore", Margin = new Thickness(0, 12, 0, 6) };
    private readonly StackPanel _existingPanel = new();
    private readonly Grid _newPanel = new();
    private readonly Grid _lotPanel = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox _query = new();
    private readonly DataGrid _results = new()
    {
        Height = 210,
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
    };
    private readonly TextBlock _noMatches = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly FormField _resultsField;
    private readonly TextBox _sku;
    private readonly TextBox _name;
    private readonly TextBox _localName;
    private readonly TextBox _hsn;
    private readonly ComboBox _category;
    private readonly ComboBox _unit;
    private readonly ComboBox _tax;
    // Yes/No choices start empty when the original record did not save the fact.
    private readonly ComboBox _trackExpiry;
    private readonly ComboBox _isLoose;
    private readonly TextBox _price = new();
    private readonly TextBox _mrp = new();
    private readonly DatePicker _expiry = new() { SelectedDateFormat = DatePickerFormat.Short };
    private readonly List<FormField> _newFields = [];
    private readonly FormField _skuField;
    private readonly FormField _nameField;
    private readonly FormField _localNameField;
    private readonly FormField _hsnField;
    private readonly FormField _categoryField;
    private readonly FormField _unitField;
    private readonly FormField _taxField;
    private readonly FormField _trackExpiryField;
    private readonly FormField _looseField;
    private readonly FormField _priceField;
    private readonly FormField _mrpField;
    private readonly FormField _expiryField;
    private readonly Button _save = new() { IsDefault = true, MinWidth = 150 };
    private CancellationTokenSource? _searching;

    public RestoreProductChoice? Result { get; private set; }

    public RestoreProductWindow(RestoreDraft draft, RestoreOptions options, bool regularTax,
        Func<string, CancellationToken, Task<IReadOnlyList<ProductListItem>>> search, Func<string, Task<bool>> skuInUse)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);
        _draft = draft;
        _options = options;
        _regularTax = regularTax;
        _search = search;
        _skuInUse = skuInUse;
        Title = Tr("Stock_RestoreTitle");
        ScreenHelp.SetTopic(this, "restore-product");
        Width = 760;
        Height = 760;
        MinWidth = 620;
        MinHeight = 560;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "AppFont");
        NumberSubstitution.SetCultureSource(this, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(this, CultureInfo.GetCultureInfo("en-IN"));
        NumberSubstitution.SetSubstitution(this, NumberSubstitutionMethod.European);

        _useExisting.Content = Tr("Stock_RestoreUseExisting");
        _createNew.Content = Tr("Stock_RestoreCreateNew");
        _useExisting.Checked += (_, _) => ShowMode();
        _createNew.Checked += (_, _) => ShowMode();

        _query.Text = draft.ProductName;
        _query.ToolTip = Tr("Stock_RestoreSearch");
        AutomationProperties.SetName(_query, Tr("Stock_RestoreSearch"));
        _query.TextChanged += async (_, _) => { if (IsLoaded) await SearchAsync(); };
        _results.SetResourceReference(StyleProperty, "CatalogGrid");
        _results.Columns.Add(Column("Catalog_Sku", nameof(ProductListItem.Sku), 110));
        _results.Columns.Add(Column("Catalog_Name", nameof(ProductListItem.Name), 240));
        _results.Columns.Add(Column("Catalog_Unit", nameof(ProductListItem.Uom), 70));
        _results.Columns.Add(Column("Pos_Price", nameof(ProductListItem.SellingPrice), 90));
        _results.Columns.Add(Column("Stock_Mrp", nameof(ProductListItem.Mrp), 90));
        _results.SelectionChanged += (_, _) => UseProductPrices();
        _noMatches.Text = Tr("Stock_RestoreNoMatches");
        _resultsField = new FormField { Label = Tr("Stock_RestoreSearch"), IsRequired = true, Content = Stack(_query, _results, _noMatches) };
        _existingPanel.Children.Add(_resultsField);

        _sku = new TextBox { Text = draft.SuggestedSku, MaxLength = 20 };
        _name = new TextBox { Text = draft.ProductName, MaxLength = 120 };
        _localName = new TextBox { Text = draft.ProductNameLocal ?? "", MaxLength = 120 };
        _hsn = new TextBox { Text = draft.HsnCode ?? "", MaxLength = 8 };
        _category = Choice(options.Categories, draft.CategoryId);
        _unit = Choice(options.Units, draft.UnitId);
        _tax = Choice(options.TaxRates, draft.TaxRateId);
        _trackExpiry = YesNo(draft.TrackExpiry ?? (draft.ExpiryDate is null ? null : true));
        _isLoose = YesNo(draft.IsLoose);
        _skuField = NewField("Sales_RecreateSku", _sku, required: true, saved: true);
        _nameField = NewField("Sales_RecreateName", _name, required: true, saved: draft.ProductName.Length > 0);
        _localNameField = NewField("Sales_RecreateLocalName", _localName, required: false, saved: true);
        _categoryField = NewField("Sales_RecreateCategory", _category, required: true, saved: draft.CategoryId is not null);
        _unitField = NewField("Sales_RecreateUnit", _unit, required: true, saved: draft.UnitId is not null);
        _taxField = NewField("Sales_RecreateTax", _tax, required: regularTax, saved: draft.TaxRateId is not null);
        _hsnField = NewField("Sales_RecreateHsn", _hsn, required: regularTax, saved: draft.HsnCode is not null || !regularTax);
        _trackExpiryField = NewField("Sales_RecreateTrackExpiry", _trackExpiry, required: true, saved: draft.TrackExpiry is not null);
        _looseField = NewField("Sales_RecreateFractional", _isLoose, required: true, saved: draft.IsLoose is not null);
        LayOut(_newPanel, _newFields);

        _price.Text = Amount(draft.SellingPrice);
        _mrp.Text = Amount(draft.Mrp);
        _expiry.SelectedDate = draft.ExpiryDate?.ToDateTime(TimeOnly.MinValue);
        _expiry.IsEnabled = draft.ExpiryDate is null;
        _priceField = new FormField { Label = Tr("Stock_RestoreSellingPrice"), IsRequired = true, Content = _price };
        _mrpField = new FormField { Label = Tr("Stock_Mrp"), Content = _mrp };
        _expiryField = new FormField
        {
            Label = Tr("Sales_RecreateLotExpiry"),
            Hint = Tr(draft.ExpiryDate is null ? "Stock_RestoreExpiryHint" : "Stock_RestoreSavedOnRecord"),
            Content = _expiry,
        };
        LayOut(_lotPanel, [_priceField, _mrpField, _expiryField]);

        _content.Children.Add(_useExisting);
        _content.Children.Add(_existingPanel);
        _content.Children.Add(_createNew);
        _content.Children.Add(_newPanel);
        _content.Children.Add(Styled(new TextBlock { Text = Tr("Stock_RestoreNewLot"), Margin = new Thickness(0, 16, 0, 0) }, "FormContext"));
        _content.Children.Add(_lotPanel);
        ShowMode();

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        var titleRow = new DockPanel();
        var help = new HelpButton { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(help, Dock.Right);
        titleRow.Children.Add(help);
        titleRow.Children.Add(Styled(new TextBlock { Text = Title }, "FormTitle"));
        heading.Children.Add(titleRow);
        heading.Children.Add(Styled(new TextBlock
        {
            Text = TranslationSource.Instance.Format("Stock_RestoreIntro", draft.ProductName,
                draft.Quantity.Value.ToString("0.###", TranslationSource.FormatCulture) + " " + (draft.UnitCode ?? ""),
                draft.DocumentNumber, draft.UnitCost.Value.ToString("0.000", TranslationSource.FormatCulture)),
            TextWrapping = TextWrapping.Wrap,
        }, "FormContext"));
        root.Children.Add(heading);
        Grid.SetRow(_summary, 1);
        root.Children.Add(_summary);
        var scroll = new ScrollViewer
        {
            Content = _content,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        var footer = Styled(new StackPanel(), "FormFooter");
        _save.Content = Tr("Stock_RestoreSave");
        _save.Click += Save;
        footer.Children.Add(new Button { Content = Tr("Common_Cancel"), IsCancel = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) });
        footer.Children.Add(_save);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;
        Loaded += async (_, _) =>
        {
            _query.Focus();
            await SearchAsync();
        };
        Closed += (_, _) => _searching?.Cancel();
    }

    /// <summary>Shows the window for one restore with the app's services; null when the operator cancels.</summary>
    public static async Task<RestoreProductChoice?> AskAsync(Window? owner, RestoreDraft draft)
    {
        IServiceProvider services = ((App)Application.Current).Services;
        InventoryService inventory = services.GetRequiredService<InventoryService>();
        CatalogService catalog = services.GetRequiredService<CatalogService>();
        bool regularTax = services.GetRequiredService<ISettings>().Current.TaxMode == "REGULAR";
        var window = new RestoreProductWindow(draft, await inventory.GetRestoreOptionsAsync(), regularTax,
            (query, cancellationToken) => catalog.SearchAsync(
                new ProductSearchFilter(Query: query, Limit: 50) { ProductClass = draft.ProductClass }, cancellationToken),
            sku => inventory.IsSkuInUseAsync(sku))
        { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    private bool Existing => _useExisting.IsChecked == true;
    private ProductListItem? Selected => _results.SelectedItem as ProductListItem;

    private void ShowMode()
    {
        _existingPanel.Visibility = Existing ? Visibility.Visible : Visibility.Collapsed;
        _newPanel.Visibility = Existing ? Visibility.Collapsed : Visibility.Visible;
        if (Existing) UseProductPrices();
        else
        {
            _price.Text = Amount(_draft.SellingPrice);
            _mrp.Text = Amount(_draft.Mrp);
        }
    }

    // A chosen product's prices fill the new lot; the operator may still change them.
    private void UseProductPrices()
    {
        if (Selected is not { } product) return;
        _price.Text = Amount(product.SellingPrice ?? _draft.SellingPrice);
        _mrp.Text = Amount(product.Mrp ?? _draft.Mrp);
    }

    private async Task SearchAsync()
    {
        _searching?.Cancel();
        var searching = new CancellationTokenSource();
        _searching = searching;
        try
        {
            IReadOnlyList<ProductListItem> items = await _search(_query.Text.Trim(), searching.Token);
            if (searching.IsCancellationRequested) return;
            _results.ItemsSource = items;
            _noMatches.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _summary.ShowMessage(UserError.Message(exception)); }
    }

    private async void Save(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        Money? price = form.Money(_priceField, _price.Text, required: true);
        Money? mrp = form.Money(_mrpField, _mrp.Text);
        if (price is { } p && mrp is { } m && p > m) form.AddKey(_mrpField, "Error_PriceAboveMrp");
        RestoreNewProduct? created = null;
        long? productId = null;
        bool trackExpiry;
        RestoreOption? unit;
        if (Existing)
        {
            if (Selected is not { } product)
            {
                form.AddKey(_resultsField, "Stock_RestorePickProduct");
                trackExpiry = false;
                unit = null;
            }
            else
            {
                productId = product.Id;
                trackExpiry = product.TrackExpiry;
                unit = _options.Units.FirstOrDefault(option =>
                    string.Equals(option.Code, product.Uom, StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            string? sku = form.Text(_skuField, _sku.Text, required: true, maxLength: 20);
            string? name = form.Text(_nameField, _name.Text, required: true, maxLength: 120);
            string? localName = form.Text(_localNameField, _localName.Text, maxLength: 120);
            long? category = form.Choice(_categoryField, _category.SelectedValue);
            long? unitId = form.Choice(_unitField, _unit.SelectedValue);
            long? tax = form.Choice(_taxField, _tax.SelectedValue, required: _regularTax);
            string? hsn = form.Text(_hsnField, _hsn.Text, required: _regularTax, maxLength: 8);
            if (hsn is not null && (hsn.Length is not (4 or 6 or 8) || hsn.Any(static c => !char.IsAsciiDigit(c))))
                form.AddKey(_hsnField, "Stock_RestoreHsnDigits");
            long? track = form.Choice(_trackExpiryField, _trackExpiry.SelectedValue);
            long? loose = form.Choice(_looseField, _isLoose.SelectedValue);
            trackExpiry = track == 1;
            unit = _options.Units.FirstOrDefault(option => option.Id == unitId);
            if (sku is not null && await _skuInUse(sku)) form.AddKey(_skuField, "Error_SkuExists");
            if (sku is not null && name is not null && category is not null && unitId is not null)
                created = new RestoreNewProduct(sku, name, localName, category.Value, unitId.Value, tax, hsn,
                    trackExpiry, loose == 1);
        }
        // The same unit always fits; another unit must take the quantity.
        if (unit is not null && !string.Equals(unit.Code, _draft.UnitCode, StringComparison.OrdinalIgnoreCase) &&
            !unit.AllowFraction && !_draft.Quantity.IsWhole)
            form.AddKey(Existing ? _resultsField : _unitField, "Error_FractionalQtyNotAllowed");
        DateOnly? expiry = _draft.ExpiryDate is null
            ? form.Date(_expiryField, _expiry.SelectedDate, required: trackExpiry)
            : null;
        if (!form.Apply(_content, _summary)) return;
        Result = new RestoreProductChoice(productId, created, price!.Value, mrp, expiry);
        DialogResult = true;
    }

    private FormField NewField(string labelKey, Control input, bool required, bool saved)
    {
        var field = new FormField
        {
            Label = Tr(labelKey),
            IsRequired = required,
            Hint = saved ? null : Tr("Stock_RestoreNotSaved"),
            Content = input,
        };
        _newFields.Add(field);
        return field;
    }

    // Two columns in reading order.
    private static void LayOut(Grid grid, List<FormField> fields)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < fields.Count; index++)
        {
            if (index % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(fields[index], index / 2);
            Grid.SetColumn(fields[index], index % 2);
            grid.Children.Add(fields[index]);
        }
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (UIElement child in children) panel.Children.Add(child);
        return panel;
    }

    private static DataGridTextColumn Column(string headerKey, string path, double width) => new()
    {
        Header = Tr(headerKey),
        Binding = new Binding(path),
        Width = new DataGridLength(width),
    };

    private static ComboBox Choice(IReadOnlyList<RestoreOption> items, long? selected) => new()
    {
        ItemsSource = items,
        DisplayMemberPath = nameof(RestoreOption.Label),
        SelectedValuePath = nameof(RestoreOption.Id),
        SelectedValue = selected,
    };

    private static ComboBox YesNo(bool? value) =>
        Choice([new(1, Tr("Form_Yes")), new(0, Tr("Form_No"))], value switch { true => 1, false => 0, null => null });

    private static T Styled<T>(T element, string style) where T : FrameworkElement
    {
        element.SetResourceReference(StyleProperty, style);
        return element;
    }

    private static string Amount(Money? value) =>
        value?.Value.ToString("0.###", TranslationSource.FormatCulture) ?? "";

    private static string Tr(string key) => TranslationSource.Instance[key];
}
