using GroceryErp.Data.Services;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>States before saving how a new dated money-out entry counts in profit and loss.</summary>
internal static class PeriodNotice
{
    public static string ForNewEntry(CorrectionDating dating)
    {
        ArgumentNullException.ThrowIfNull(dating);
        string date = dating.OriginalDate.ToString("dd MMM yyyy", TranslationSource.FormatCulture);
        return dating.Treatment == PeriodTreatment.OpenPeriod
            ? TranslationSource.Instance.Format("Form_PeriodOpen", date)
            : TranslationSource.Instance.Format("Form_PeriodLocked", date);
    }
}
