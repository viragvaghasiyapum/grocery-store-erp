using GroceryErp.Core.Errors;
using GroceryErp.Localization;
using Serilog;

namespace GroceryErp.App.Presentation;

internal static class UserError
{
    public static string Message(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Log.Error(exception, "Presentation operation failed");
        if (exception is DomainException domain)
        {
            string key = $"Error_{domain.Code}";
            return domain.Arguments.Count == 0
                ? TranslationSource.Instance[key]
                : TranslationSource.Instance.Format(key, [.. domain.Arguments]);
        }
        return TranslationSource.Instance["App_ActionFailed"];
    }
}
