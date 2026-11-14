using System.IO;
using System.Security.Cryptography;

namespace Sessionwright;

/// <summary>Kept in the data file when a master password is set: what derives its key and tells a right password from a wrong one.</summary>
public sealed class MasterProtection
{
    public int Iterations { get; set; } = 600_000;
    public byte[] Salt { get; set; } = [];
    /// <summary>A known text sealed with the key.</summary>
    public byte[] Check { get; set; } = [];
}

/// <summary>Thrown for a password sealed with the master password while that is not entered (the prompt was cancelled).</summary>
public sealed class MasterLockedException() : CryptographicException("The saved passwords are locked by the master password.");

/// <summary>
/// Saved passwords encrypted with a password of the user's instead of Windows' DPAPI, so the data file can travel to
/// another PC or account (a portable copy on a USB stick). The same scheme as an export with passwords: PBKDF2-SHA256
/// and AES-GCM. The key is asked for at the first use of a saved password and kept in memory until the app closes or
/// the user locks the passwords; "remember on this PC" keeps it under DPAPI outside the data folder.
/// </summary>
public static class MasterPassword
{
    public const int MinLength = 10;
    const string CheckText = "Sessionwright";
    static readonly byte[] Magic = "SWM1"u8.ToArray(); // a DPAPI blob starts with 01 00 00 00
    static readonly byte[] Entropy = "Sessionwright.master"u8.ToArray();

    static byte[]? _key;
    static MasterProtection? _of; // the protection the key in memory belongs to
    static bool _declined;

    /// <summary>The app's prompt: asks for the master password of its data and unlocks with it; false when cancelled or none is set.</summary>
    public static Func<bool>? Ask { get; set; }

    public static bool Unlocked => _key != null;
    public static bool IsSealed(byte[] secret) => secret.AsSpan().StartsWith(Magic);

    /// <summary>Per PC and account, never next to a portable copy's data: remembered keys (DPAPI) by salt.</summary>
    internal static string RememberFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sessionwright", "master");

    static string RememberFile(MasterProtection m) => Path.Combine(RememberFolder, Convert.ToHexString(m.Salt) + ".key");

    static byte[] Key(MasterProtection m, string password) =>
        Rfc2898DeriveBytes.Pbkdf2(password, m.Salt, m.Iterations, HashAlgorithmName.SHA256, 32);

    static bool Fits(MasterProtection m, byte[] key) => Importer.ExportProtection.Open(key, m.Check) == CheckText;

    static bool Holds(MasterProtection m) => _key != null && _of == m;

    public static bool TryUnlock(MasterProtection m, string password)
    {
        var key = Key(m, password);
        if (!Fits(m, key)) return false;
        (_key, _of, _declined) = (key, m, false);
        return true;
    }

    /// <summary>Forgets the key until it is entered again, on this PC too.</summary>
    public static void Lock(MasterProtection? m = null)
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        (_key, _of) = (null, null);
        if (m != null) Forget(m);
    }

    public static void Remember(MasterProtection m)
    {
        if (!Holds(m)) return;
        try
        {
            Directory.CreateDirectory(RememberFolder);
            File.WriteAllBytes(RememberFile(m), ProtectedData.Protect(_key!, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Info($"Master password not remembered: {ex.Message}"); }
    }

    public static bool TryRemembered(MasterProtection m)
    {
        try
        {
            if (!File.Exists(RememberFile(m))) return false;
            var key = ProtectedData.Unprotect(File.ReadAllBytes(RememberFile(m)), Entropy, DataProtectionScope.CurrentUser);
            if (!Fits(m, key)) return false;
            (_key, _of, _declined) = (key, m, false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return false; }
    }

    public static void Forget(MasterProtection m)
    {
        try { File.Delete(RememberFile(m)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Every key remembered on this PC (uninstalling with the data).</summary>
    public static void ForgetAll()
    {
        try { if (Directory.Exists(RememberFolder)) Directory.Delete(RememberFolder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The password of a sealed secret; asks for the master password when it is not in memory.</summary>
    public static string Open(byte[] secret)
    {
        if (_key == null && Ask?.Invoke() != true) throw new MasterLockedException();
        return Importer.ExportProtection.Open(_key!, secret[Magic.Length..]) ?? throw new CryptographicException("Sealed with another master password.");
    }

    static byte[] Seal(string secret) => [.. Magic, .. Importer.ExportProtection.Seal(_key!, secret)];

    static List<(Credential Credential, string Password)> Readable(AppData data, Func<byte[], bool> which) =>
        [.. data.Credentials.Where(c => c.Secret != null && which(c.Secret))
            .Select(c => (c, Password: TryRead(c.Secret!))).Where(x => x.Password != null).Select(x => (x.c, x.Password!))];

    static string? TryRead(byte[] secret)
    {
        try { return Store.Unprotect(secret); }
        catch (CryptographicException) { return null; } // saved by another Windows account: stays as it is
    }

    /// <summary>
    /// Sets the master password, or changes it: every saved password that can be read is sealed with the new key.
    /// False when a master password is already set and was not entered: its passwords would be lost.
    /// </summary>
    public static bool Set(AppData data, string password)
    {
        if (data.Settings.Master is { } old && !Holds(old) && (Ask?.Invoke() != true || !Holds(old))) return false;
        var readable = Readable(data, _ => true);
        if (data.Settings.Master is { } before) Forget(before);
        var m = new MasterProtection { Salt = RandomNumberGenerator.GetBytes(16) };
        (_key, _of, _declined) = (Key(m, password), m, false);
        m.Check = Importer.ExportProtection.Seal(_key, CheckText);
        data.Settings.Master = m;
        foreach (var (credential, plain) in readable) credential.Secret = Seal(plain);
        return true;
    }

    /// <summary>Back to Windows' protection for this account; false when the master password was not entered.</summary>
    public static bool Remove(AppData data)
    {
        if (data.Settings.Master is not { } m) return true;
        if (!Holds(m) && (Ask?.Invoke() != true || !Holds(m))) return false;
        var readable = Readable(data, IsSealed);
        data.Settings.Master = null;
        Lock(m);
        foreach (var (credential, plain) in readable) credential.Secret = Store.Protect(plain);
        return true;
    }

    /// <summary>
    /// Before a save: passwords saved since (under DPAPI) are sealed, so the file stays readable on another PC. Asks
    /// for the master password once when it is not in memory; declined, they stay under DPAPI until it is entered.
    /// </summary>
    public static void SealPending(AppData data)
    {
        if (data.Settings.Master is not { } m || !data.Credentials.Any(c => c.Secret != null && !IsSealed(c.Secret))) return;
        if (!Holds(m))
        {
            if (_declined) return;
            if (Ask?.Invoke() != true || !Holds(m))
            {
                _declined = true;
                return;
            }
        }
        foreach (var (credential, plain) in Readable(data, s => !IsSealed(s))) credential.Secret = Seal(plain);
    }
}
