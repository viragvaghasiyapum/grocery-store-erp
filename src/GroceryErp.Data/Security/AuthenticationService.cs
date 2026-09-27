using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Time;
using GroceryErp.Core.Validation;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Security;

public sealed class AuthenticationService(DbExecutor database, PasswordHasher hasher, IClock clock)
{
    private int _failures;

    public Task<bool> HasUserAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM app_user") != 0, cancellationToken);

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        UserCredential? user = await database.ReadAsync(connection => connection.QuerySingleOrDefault<UserCredential>(
            "SELECT username AS Username, password_hash AS PasswordHash FROM app_user WHERE id = 1"), cancellationToken);
        bool valid = user is not null && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase) && hasher.Verify(password, user.PasswordHash);
        if (!valid)
        {
            await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            throw new DomainException(ErrorCode.WrongPassword);
        }

        Interlocked.Exchange(ref _failures, 0);
        await database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE app_user SET last_login_at = @at WHERE id = 1",
            new { at = clock.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) },
            transaction.Transaction), cancellationToken);
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        UserCredential user = await database.ReadAsync(connection => connection.QuerySingle<UserCredential>(
            "SELECT username AS Username, password_hash AS PasswordHash FROM app_user WHERE id = 1"), cancellationToken);
        if (!hasher.Verify(currentPassword, user.PasswordHash)) throw new DomainException(ErrorCode.WrongPassword);
        string hash = hasher.Hash(newPassword);
        await database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE app_user SET password_hash = @hash WHERE id = 1", new { hash }, transaction.Transaction), cancellationToken);
    }

    public async Task UnlockAsync(string password, CancellationToken cancellationToken = default)
    {
        UserCredential user = await database.ReadAsync(connection => connection.QuerySingle<UserCredential>(
            "SELECT username AS Username, password_hash AS PasswordHash FROM app_user WHERE id = 1"), cancellationToken);
        if (!hasher.Verify(password, user.PasswordHash))
        {
            await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            throw new DomainException(ErrorCode.WrongPassword);
        }
        Interlocked.Exchange(ref _failures, 0);
        await database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE app_user SET last_login_at = @at WHERE id = 1",
            new { at = clock.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) },
            transaction.Transaction), cancellationToken);
    }

    private async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        int failures = Interlocked.Increment(ref _failures);
        if (failures < 3) return;
        int seconds = Math.Min(30, 1 << Math.Min(failures - 3, 5));
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
    }

    private sealed class UserCredential
    {
        public string Username { get; init; } = "";
        public string PasswordHash { get; init; } = "";
    }
}

public sealed record FirstRunRequest(
    string Username, string Password, string StoreName, string Address, string Phone,
    string Gstin, string State, string TaxMode, string Language, string BackupDirectory,
    string LotPrefix = "20", string ProductPrefix = "21",
    string ReceiptPrinterName = "", int ReceiptWidthDots = 576,
    string LabelPrinterName = "", string LabelSize = "50x25", string A4PrinterName = "",
    int UsbReminderDays = 7, string ScaleRule = "", bool GujaratiKeyboardAvailable = false,
    string StartChoice = "EMPTY");

public sealed class FirstRunService(DbExecutor database, PasswordHasher hasher)
{
    public Task CompleteAsync(FirstRunRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        string hash = hasher.Hash(request.Password);
        return database.WriteAsync(transaction =>
        {
            if (transaction.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM app_user", transaction: transaction.Transaction) != 0)
                throw new DomainException(ErrorCode.Validation, "Setup is already complete.");
            transaction.Connection.Execute(
                "INSERT INTO app_user(id, username, password_hash) VALUES (1, @Username, @hash)",
                new { request.Username, hash }, transaction.Transaction);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["store.name"] = request.StoreName.Trim(),
                ["store.address"] = request.Address.Trim(),
                ["store.phone"] = request.Phone.Trim(),
                ["store.gstin"] = request.Gstin.Trim().ToUpperInvariant(),
                ["store.state"] = request.State.Trim(),
                ["tax.mode"] = request.TaxMode,
                ["ui.language"] = request.Language,
                ["backup.dir"] = Path.GetFullPath(request.BackupDirectory.Trim()),
                ["barcode.lot_prefix"] = request.LotPrefix,
                ["barcode.product_prefix"] = request.ProductPrefix,
                ["security.idle_lock_minutes"] = "15",
            };
            settings["printer.receipt.name"] = request.ReceiptPrinterName.Trim();
            settings["printer.receipt.width_dots"] = request.ReceiptWidthDots.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["printer.receipt.mode"] = "RASTER";
            settings["printer.label.name"] = request.LabelPrinterName.Trim();
            settings["printer.label.size"] = request.LabelSize.Trim();
            settings["printer.a4.name"] = request.A4PrinterName.Trim();
            settings["backup.usb_reminder_days"] = request.UsbReminderDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["barcode.scale_rule"] = request.ScaleRule.Trim();
            settings["setup.keyboard.gujarati_available"] = request.GujaratiKeyboardAvailable ? "1" : "0";
            settings["setup.start_choice"] = request.StartChoice;
            foreach ((string key, string value) in settings)
                transaction.Connection.Execute("""
                    INSERT INTO setting(key, value) VALUES (@key, @value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """, new { key, value }, transaction.Transaction);
        }, cancellationToken);
    }

    private static void Validate(FirstRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.StoreName))
            throw new DomainException(ErrorCode.Validation, "Username and store name are required.");
        if (request.Password.Length < 8) throw new DomainException(ErrorCode.Validation, "Password must contain at least 8 characters.");
        if (request.TaxMode is not ("REGULAR" or "COMPOSITION" or "NONE")) throw new DomainException(ErrorCode.Validation, "Invalid tax mode.");
        if (request.Language is not ("en-IN" or "gu-IN")) throw new DomainException(ErrorCode.Validation, "Invalid language.");
        if (!string.IsNullOrWhiteSpace(request.Gstin) && !IndianIds.IsGstin(request.Gstin.Trim().ToUpperInvariant()))
            throw new DomainException(ErrorCode.Validation, "Invalid GSTIN.");
        if (request.LotPrefix == request.ProductPrefix || request.LotPrefix.Length != 2 || request.ProductPrefix.Length != 2 ||
            !request.LotPrefix.All(char.IsAsciiDigit) || !request.ProductPrefix.All(char.IsAsciiDigit))
            throw new DomainException(ErrorCode.Validation, "Barcode prefixes must be distinct two-digit values.");
        if (request.ReceiptWidthDots is not (384 or 576)) throw new DomainException(ErrorCode.Validation, "Receipt width must be 58 or 80 mm.");
        if (request.UsbReminderDays is < 1 or > 365) throw new DomainException(ErrorCode.Validation, "USB reminder must be between 1 and 365 days.");
        if (request.StartChoice is not ("EMPTY" or "IMPORT")) throw new DomainException(ErrorCode.Validation, "Invalid start choice.");
        if (string.IsNullOrWhiteSpace(request.BackupDirectory))
            throw new DomainException(ErrorCode.Validation, "Backup directory is required.");
        AppPaths.RejectCloudSynchronizedPath(request.BackupDirectory);
    }
}
