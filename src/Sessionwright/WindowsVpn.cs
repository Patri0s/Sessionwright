using System.Runtime.InteropServices;
using System.Text;

namespace Sessionwright;

/// <summary>
/// VPN connections set up in Windows (Settings > VPN), dialed through the Remote Access API that rasdial uses too, with
/// the user name and password handed over in memory: never on a command line (which other programs and security logs
/// see), never saved into the VPN's settings. A pre-shared key and the like stay where Windows keeps them.
/// </summary>
static class WindowsVpn
{
    const int MaxEntryName = 256, MaxPhoneNumber = 128, MaxCallbackNumber = 128, UserLength = 256, PasswordLength = 256,
        DomainLength = 15, MaxPath = 260;
    const int BufferTooSmall = 603, PortAlreadyOpen = 602, NoPhonebook = 621;

    /// <summary>A VPN of this user or of all users, with the phone book it lives in.</summary>
    public sealed record Entry(string Name, string Phonebook);

    /// <summary>
    /// The VPN connections Windows knows for this user, by name; empty when there are none or they cannot be read.
    /// <paramref name="phonebook"/>: one phone-book file instead of Windows' own (tests).
    /// </summary>
    public static IReadOnlyList<Entry> Entries(string? phonebook = null)
    {
        var size = Marshal.SizeOf<RasEntryName>();
        var bytes = size;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.WriteInt32(buffer, size); // dwSize of the first entry tells the API which layout is meant
                var result = RasEnumEntries(null, phonebook, buffer, ref bytes, out var count);
                if (result == BufferTooSmall) continue;
                if (result == NoPhonebook) return []; // no VPN was ever set up on this PC
                if (result != 0)
                {
                    Log.Info($"VPN: the list could not be read ({Describe(result)})");
                    return [];
                }
                return Enumerable.Range(0, count)
                    .Select(i => Marshal.PtrToStructure<RasEntryName>(buffer + i * size))
                    .Select(e => new Entry(e.Name, string.IsNullOrEmpty(e.Phonebook) ? phonebook ?? "" : e.Phonebook))
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    /// <summary>
    /// Connects the VPN called <paramref name="name"/> and waits until it is up (it blocks: run it off the UI thread).
    /// Null when connected or already connected, else why not, in Windows' words.
    /// </summary>
    public static string? Connect(string name, string? user, string? domain, string? password, string? phonebook = null)
    {
        if (Entries(phonebook).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.CurrentCultureIgnoreCase)) is not { } entry)
            return Loc.Get("VpnNotFound");

        // A domain longer than the field (a DNS name) goes with the user name, as DOMAIN\user would.
        (user, domain) = (user ?? "", domain ?? "");
        if (domain.Length > DomainLength && !user.Contains('\\') && !user.Contains('@')) (user, domain) = ($@"{domain}\{user}", "");
        var parameters = new RasDialParams
        {
            Size = Marshal.SizeOf<RasDialParams>(), EntryName = entry.Name, UserName = user, Password = password ?? "", Domain = domain,
            PhoneNumber = "", CallbackNumber = "",
        };
        var result = RasDial(IntPtr.Zero, entry.Phonebook.Length > 0 ? entry.Phonebook : null, ref parameters, 0, IntPtr.Zero, out var connection);
        if (result is 0 or PortAlreadyOpen) return null;
        if (connection != IntPtr.Zero) RasHangUp(connection); // a failed dial still holds a port until hung up
        return Describe(result);
    }

    static string Describe(int error)
    {
        var text = new StringBuilder(512);
        return RasGetErrorString(error, text, text.Capacity) == 0 ? $"{text.ToString().Trim()} ({error})" : $"error {error}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RasEntryName
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxEntryName + 1)] public string Name;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath + 1)] public string Phonebook;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RasDialParams
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxEntryName + 1)] public string EntryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPhoneNumber + 1)] public string PhoneNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxCallbackNumber + 1)] public string CallbackNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UserLength + 1)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PasswordLength + 1)] public string Password;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DomainLength + 1)] public string Domain;
        public int SubEntry;
        public IntPtr CallbackId;
        public int InterfaceIndex;
    }

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RasEnumEntries(string? reserved, string? phonebook, IntPtr entries, ref int bytes, out int count);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RasDial(IntPtr extensions, string? phonebook, ref RasDialParams parameters, int notifierType, IntPtr notifier, out IntPtr connection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RasHangUp(IntPtr connection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RasGetErrorString(int error, StringBuilder text, int length);
}
