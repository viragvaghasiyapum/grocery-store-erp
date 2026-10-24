using System.Windows;
using GroceryErp.Assets;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>Disposes an asset; the gain or loss is measured against its book value at that date.</summary>
public partial class AssetDisposalWindow : Window
{
    private readonly AssetService _assets;
    private readonly AssetItem _asset;
    private readonly string _operationId = OperationGuard.NewId();

    public AssetDisposalWindow(AssetService assets, IClock clock, AssetItem asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        InitializeComponent();
        _assets = assets;
        _asset = asset;
        AssetText.Text = TranslationSource.Instance.Format("Assets_DisposalAsset", asset.Name,
            asset.BookValue.Value.ToString("N3", TranslationSource.FormatCulture));
        DisposalDate.SelectedDate = clock.BusinessDate.ToDateTime(TimeOnly.MinValue);
        Loaded += (_, _) => ProceedsField.FocusInput();
    }

    private async void Dispose_OnClick(object sender, RoutedEventArgs e)
    {
        var form = new FormValidator();
        DateOnly? date = form.Date(DateField, DisposalDate.SelectedDate);
        Money? proceeds = form.Money(ProceedsField, DisposalValue.Text, required: true);
        if (!form.Apply(Fields, Summary)) return;
        if (MessageBox.Show(TranslationSource.Instance.Format("Assets_DisposeConfirm", _asset.Name),
            TranslationSource.Instance["Assets_Dispose"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DisposeButton.IsEnabled = false;
        try
        {
            await _assets.DisposeAsync(_asset.Id, date!.Value, proceeds!.Value, _operationId);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Summary.ShowMessage(UserError.Message(exception));
            DisposeButton.IsEnabled = true;
        }
    }
}
