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
    /// <summary>Notes are sealed too: they show only once the master password was entered.</summary>
    public bool Notes { get; set; }
    /// <summary>Locking Windows locks the passwords (and notes) as well.</summary>
    public bool LockWithWindows { get; set; }
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

    /// <summary>The key of <paramref name="m"/> is in memory, asked for when it was not.</summary>
    public static bool Ensure(MasterProtection m) => Holds(m) || (Ask?.Invoke() == true && Holds(m));

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

    /// <summary>"Lock passwords", or Windows was locked: the notes that are sealed in the file go out of memory with the key.</summary>
    public static void Lock(AppData data)
    {
        CloseNotes(data);
        Lock(data.Settings.Master);
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
        var old = data.Settings.Master;
        if (old != null && !Ensure(old)) return false;
        OpenNotes(data);
        var readable = Readable(data, _ => true);
        if (old != null) Forget(old);
        var m = new MasterProtection { Salt = RandomNumberGenerator.GetBytes(16), Notes = old?.Notes ?? false, LockWithWindows = old?.LockWithWindows ?? false };
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
        if (!Ensure(m)) return false;
        OpenNotes(data);
        var readable = Readable(data, IsSealed);
        data.Settings.Master = null;
        Lock(m);
        foreach (var (credential, plain) in readable) credential.Secret = Store.Protect(plain);
        return true;
    }

    /// <summary>
    /// Before a save: passwords saved since (under DPAPI) are sealed, so the file stays readable on another PC. Asks
    /// for the master password once when it is not in memory and there is something to seal (a password, or notes
    /// when those are sealed too); declined, that stays as it is until the master password is entered.
    /// </summary>
    public static void SealPending(AppData data)
    {
        if (data.Settings.Master is not { } m) return;
        var passwords = data.Credentials.Any(c => c.Secret != null && !IsSealed(c.Secret));
        if (!passwords && !(m.Notes && Own(data).Any(n => n.Notes != null))) return;
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

    // ---------- Notes, when the user chose to seal them too ----------

    /// <summary>The user's own groups and connections: a shared list's are the team's and never saved.</summary>
    static IEnumerable<Node> Own(AppData data) => data.Groups.Cast<Node>().Concat(data.Connections).Where(n => !n.Shared);

    /// <summary>The sealed notes back in plain, in memory; nothing without the key.</summary>
    public static void OpenNotes(AppData data)
    {
        if (_key == null) return;
        foreach (var n in Own(data).Where(n => n.SealedNotes != null).ToList())
        {
            if (Importer.ExportProtection.Open(_key, n.SealedNotes![Magic.Length..]) is not { } plain) continue; // sealed with another password: stays
            (n.Notes, n.SealedNotes) = (plain, null);
        }
    }

    /// <summary>
    /// Before the notes are read or changed (the editor, an export): asks for the master password when some are still
    /// sealed. False when it was not entered: those notes are not there to work with.
    /// </summary>
    public static bool TryOpenNotes(AppData data)
    {
        if (!Own(data).Any(n => n.SealedNotes != null)) return true;
        if (data.Settings.Master is not { } m || !Ensure(m)) return false;
        OpenNotes(data);
        return true;
    }

    /// <summary>
    /// Seals the notes and takes them out of memory, when notes are sealed and the key is there. Returns what was
    /// taken, for <see cref="RestoreNotes"/>: a save seals them only for the moment the file is written.
    /// </summary>
    public static List<(Node Node, string Notes)> CloseNotes(AppData data)
    {
        var taken = new List<(Node, string)>();
        if (data.Settings.Master is not { Notes: true } m || !Holds(m)) return taken;
        foreach (var n in Own(data).Where(n => n.Notes != null).ToList())
        {
            taken.Add((n, n.Notes!));
            (n.SealedNotes, n.Notes) = (Seal(n.Notes!), null);
        }
        return taken;
    }

    public static void RestoreNotes(List<(Node Node, string Notes)> taken)
    {
        foreach (var (n, notes) in taken) (n.Notes, n.SealedNotes) = (notes, null);
    }
}
