using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twenty.Api.Auth;

public sealed record StoredAccount(string Username, string Salt, string Hash, int Iterations, string Stamp, int Failures,
    DateTimeOffset? LockedUntil, DateTimeOffset CreatedAt, DateTimeOffset PasswordChangedAt);
public enum LoginOutcome { Success, Invalid, Locked }
public sealed record LoginResult(LoginOutcome Outcome, string? Stamp = null, DateTimeOffset? LockedUntil = null);
public sealed class AuthException(string code) : Exception(code) { public string Code { get; } = code; }

// One local operator account. The file lives in the private data volume; only a PBKDF2 hash is stored.
// Until the account exists, setup needs a one-time code from private configuration or the container log.
public sealed class AccountStore
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumFailures = 5;
    public static readonly TimeSpan LockoutPeriod = TimeSpan.FromMinutes(15);
    private const int SaltBytes = 16, HashBytes = 32;
    private readonly string directory, path;
    private readonly int iterations;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly byte[] dummySalt = RandomNumberGenerator.GetBytes(SaltBytes);
    private StoredAccount? account;

    public AccountStore(string root, string? configuredSetupCode = null, int iterations = 600_000, TimeProvider? clock = null)
    {
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        directory = Path.Combine(root, "auth"); path = Path.Combine(directory, "account.json");
        this.iterations = iterations; this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        account = File.Exists(path) ? JsonSerializer.Deserialize<StoredAccount>(File.ReadAllText(path)) ?? throw new AuthException("account-file-invalid") : null;
        if (account is null)
            SetupCode = string.IsNullOrWhiteSpace(configuredSetupCode) ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)) : configuredSetupCode.Trim();
    }

    /// <summary>Present only while no account exists. Generated codes are printed once to the log at startup.</summary>
    public string? SetupCode { get; private set; }
    public bool SetupRequired { get { lock (gate) return account is null; } }
    public StoredAccount? Account { get { lock (gate) return account; } }

    public string Setup(string? code, string? username, string? password)
    {
        lock (gate)
        {
            if (account is not null || SetupCode is null) throw new AuthException("setup-closed");
            if (code is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.Trim()), Encoding.UTF8.GetBytes(SetupCode)))
                throw new AuthException("setup-code-invalid");
            var name = ValidUsername(username); ValidPassword(password);
            var now = clock.GetUtcNow(); var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            Save(new StoredAccount(name, Convert.ToBase64String(salt), Convert.ToBase64String(Derive(password!, salt, iterations)), iterations,
                NewStamp(), 0, null, now, now));
            SetupCode = null;
            return account!.Stamp;
        }
    }

    public LoginResult Login(string? username, string? password)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (account is null) { Derive(password ?? "", dummySalt, iterations); return new(LoginOutcome.Invalid); }
            if (account.LockedUntil is { } until && until > now) return new(LoginOutcome.Locked, LockedUntil: until);
            var nameMatches = string.Equals(username?.Trim(), account.Username, StringComparison.OrdinalIgnoreCase);
            // Always derive, so an unknown name and a wrong password take the same time.
            var passwordMatches = Matches(password ?? "", account);
            if (nameMatches && passwordMatches)
            {
                if (account.Failures != 0 || account.LockedUntil is not null) Save(account with { Failures = 0, LockedUntil = null });
                return new(LoginOutcome.Success, account.Stamp);
            }
            var failures = account.Failures + 1;
            if (failures >= MaximumFailures) { Save(account with { Failures = 0, LockedUntil = now + LockoutPeriod }); return new(LoginOutcome.Locked, LockedUntil: now + LockoutPeriod); }
            Save(account with { Failures = failures, LockedUntil = null });
            return new(LoginOutcome.Invalid);
        }
    }

    public string ChangePassword(string? current, string? next)
    {
        lock (gate)
        {
            if (account is null) throw new AuthException("setup-required");
            if (!Matches(current ?? "", account)) throw new AuthException("password-current-invalid");
            ValidPassword(next);
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            // A new stamp signs out every other session.
            Save(account with { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Derive(next!, salt, iterations)), Iterations = iterations,
                Stamp = NewStamp(), Failures = 0, LockedUntil = null, PasswordChangedAt = clock.GetUtcNow() });
            return account.Stamp;
        }
    }

    public bool StampValid(string? stamp)
    {
        lock (gate) return account is not null && stamp is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stamp), Encoding.UTF8.GetBytes(account.Stamp));
    }

    private static string ValidUsername(string? username)
    {
        var name = username?.Trim() ?? "";
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new AuthException("username-invalid");
        return name;
    }
    private static void ValidPassword(string? password)
    {
        if (password is null || password.Length < MinimumPasswordLength) throw new AuthException("password-too-short");
        if (password.Length > 256) throw new AuthException("password-too-long");
    }
    private static bool Matches(string password, StoredAccount stored) =>
        CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(stored.Salt), stored.Iterations), Convert.FromBase64String(stored.Hash));
    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, HashBytes);
    private static string NewStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private void Save(StoredAccount next)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
        account = next;
    }
}
