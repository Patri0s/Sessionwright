using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Velopack.Locators;

namespace Sessionwright;

/// <summary>
/// The desktop and Start menu shortcuts, as the installer makes them: same file name, target and AppUserModelID, so a
/// shortcut made from Settings groups with the running app on the taskbar and Velopack removes it on uninstall (it
/// removes every shortcut pointing into the install folder). Velopack's own helper for this is obsolete.
/// </summary>
static class Shortcut
{
    public enum Place { Desktop, StartMenu }

    const string FileName = "Sessionwright.lnk";

    static string PathOf(Place place) => Path.Combine(Environment.GetFolderPath(place == Place.Desktop
        ? Environment.SpecialFolder.DesktopDirectory : Environment.SpecialFolder.Programs), FileName);

    public static bool Exists(Place place) => File.Exists(PathOf(place));

    /// <summary>Makes or removes the shortcut; false when it could not (said in the log) or this copy is not installed.</summary>
    public static bool Set(Place place, bool on)
    {
        if (!Updater.IsInstalled) return false;
        try
        {
            if (on) Create(PathOf(place));
            else File.Delete(PathOf(place));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Log.Error(ex, "Changing a shortcut");
            return false;
        }
    }

    static void Create(string path)
    {
        var locator = VelopackLocator.Current;
        // Where the installer's shortcuts point: current\Sessionwright.exe, the folder each update replaces.
        Write(path, locator.AppContentDir ?? throw new IOException("The install folder is unknown."), locator.AppUserModelId);
    }

    internal static void Write(string path, string folder, string? appUserModelId)
    {
        var target = Path.Combine(folder, "Sessionwright.exe");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(folder);
            link.SetIconLocation(target, 0);
            if (appUserModelId is { Length: > 0 } id)
            {
                var store = (IPropertyStore)link;
                var key = AppUserModelIdKey;
                var value = new PropVariant { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(id) };
                try
                {
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally { Marshal.FreeCoTaskMem(value.pointer); }
            }
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    // System.AppUserModel.ID
    static readonly PropertyKey AppUserModelIdKey = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath(IntPtr file, int max, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription(IntPtr name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation(IntPtr path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort vt;
        ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        IntPtr padding;
    }
}
