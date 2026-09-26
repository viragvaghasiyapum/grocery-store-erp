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
            int failures = Interlocked.Increment(ref _failures);
            if (failures >= 3)
            {
                int seconds = Math.Min(30, 1 << Math.Min(failures - 3, 5));
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
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
        if (!hasher.Verify(password, user.PasswordHash)) throw new DomainException(ErrorCode.WrongPassword);
        Interlocked.Exchange(ref _failures, 0);
        await database.WriteAsync(transaction => transaction.Connection.Execute(
            "UPDATE app_user SET last_login_at = @at WHERE id = 1",
            new { at = clock.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) },
            transaction.Transaction), cancellationToken);
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
    string LotPrefix = "20", string ProductPrefix = "21");

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
                ["store.name"] = request.StoreName.Trim(), ["store.address"] = request.Address.Trim(),
                ["store.phone"] = request.Phone.Trim(), ["store.gstin"] = request.Gstin.Trim().ToUpperInvariant(),
                ["store.state"] = request.State.Trim(), ["tax.mode"] = request.TaxMode,
                ["ui.language"] = request.Language, ["backup.dir"] = request.BackupDirectory.Trim(),
                ["barcode.lot_prefix"] = request.LotPrefix, ["barcode.product_prefix"] = request.ProductPrefix,
                ["security.idle_lock_minutes"] = "15",
            };
            foreach ((string key, string value) in settings)
                transaction.Connection.Execute("UPDATE setting SET value = @value WHERE key = @key", new { key, value }, transaction.Transaction);
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
        if (!string.IsNullOrWhiteSpace(request.BackupDirectory)) AppPaths.RejectCloudSynchronizedPath(request.BackupDirectory);
    }
}
