using System.IO;
using GroceryErp.Core.Errors;
using GroceryErp.Localization;
using GroceryErp.Data.Infrastructure;
using Microsoft.Data.Sqlite;
using Serilog;

namespace GroceryErp.App.Presentation;

internal static class UserError
{
    public static string Message(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Log.Error(exception, "Presentation operation failed");
        if (exception is SqliteException sqlite) exception = SqliteErrorMapper.Map(sqlite);
        if (exception is IOException io && (io.HResult & 0xffff) is 0x70 or 0x27)
            exception = new DomainException(ErrorCode.DiskFull);
        else if (exception is IOException)
            exception = new DomainException(ErrorCode.DatabaseIo);
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
