using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace GroceryErp.Data.Security;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
}

public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultMemoryKiB = 65_536;
    private const int DefaultIterations = 3;
    private const int DefaultLanes = 2;

    public string Hash(string password)
    {
        ValidatePassword(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Compute(password, salt, DefaultMemoryKiB, DefaultIterations, DefaultLanes);
        return $"argon2id$m={DefaultMemoryKiB},t={DefaultIterations},p={DefaultLanes}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        try
        {
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "argon2id") return false;
            Dictionary<string, int> parameters = parts[1].Split(',').Select(static part => part.Split('='))
                .ToDictionary(static part => part[0], static part => int.Parse(part[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Compute(password, salt, parameters["m"], parameters["t"], parameters["p"]);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception exception) when (exception is FormatException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt, int memory, int iterations, int lanes)
    {
        if (memory is < 8 or > 1_048_576 || iterations is < 1 or > 20 || lanes is < 1 or > 16)
            throw new FormatException("Unsafe Argon2 parameters.");
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = lanes,
        };
        return argon.GetBytes(HashBytes);
    }

    private static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < 8) throw new ArgumentException("Password must contain at least 8 characters.", nameof(password));
    }
}
