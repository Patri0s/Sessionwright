using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sessionwright;

/// <summary>
/// AllMonitors = full screen spanning several monitors, each one a remote monitor (mstsc /multimon): all of them, or
/// those chosen in <see cref="RdpOptions.Monitors"/> (mstsc's "selectedmonitors").
/// </summary>
public enum ResolutionMode { FitWindow, Fullscreen, Fixed, AllMonitors }

/// <summary>Power state of an Azure virtual machine (the "PowerState/..." status codes).</summary>
public enum VmPowerState { Unknown, Starting, Running, Stopping, Stopped, Deallocating, Deallocated }

/// <summary>On which days the Azure VMs of a connection (or a group's connections) are started ahead of work.</summary>
public enum AutoStartDays { Off, Weekdays, Daily }

/// <summary>Starting Azure VMs ahead of work (Sessionwright's own schedule, see AzureAutoStart).</summary>
public class VmAutoStart
{
    public AutoStartDays Days { get; set; } = AutoStartDays.Weekdays;
    /// <summary>Local time, "HH:mm".</summary>
    public string Time { get; set; } = "08:00";

    public bool Includes(DayOfWeek day) =>
        Days == AutoStartDays.Daily || Days == AutoStartDays.Weekdays && day is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public VmAutoStart Clone() => (VmAutoStart)MemberwiseClone();

    public override string ToString() => Days switch
    {
        AutoStartDays.Weekdays => Loc.Format("AutoStartWeekdaysAt", Time),
        AutoStartDays.Daily => Loc.Format("AutoStartDailyAt", Time),
        _ => Loc.Get("AutoStartOff"),
    };
}

/// <summary>The Azure virtual machine behind a connection: Sessionwright shows its state and starts it before connecting.</summary>
public class AzureVmLink
{
    /// <summary>The signed-in account (user name) that reaches it.</summary>
    public string Account { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Auto-shutdown postponed by Sessionwright: its usual daily time ("HHmm"), put back after <see cref="ShutdownRestoreAfter"/> (UTC).</summary>
    public string? ShutdownOriginal { get; set; }
    public DateTime? ShutdownRestoreAfter { get; set; }
    /// <summary>The shutdown at <see cref="ShutdownRestoreAfter"/> is skipped: the schedule is off until then.</summary>
    public bool ShutdownSkipped { get; set; }
    /// <summary>The day Sessionwright last started it by its auto-start (at most once a day).</summary>
    public DateTime? AutoStartedOn { get; set; }

    public AzureVmLink Clone() => (AzureVmLink)MemberwiseClone();
}

/// <summary>An Azure account Sessionwright is signed in to; the tokens are in the encrypted MSAL cache, never here.</summary>
public class AzureAccount
{
    public string Username { get; set; } = "";
    /// <summary>Home tenant.</summary>
    public string TenantId { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string TenantText => Loc.Format("AzureTenantId", TenantId);
    /// <summary>Serialized Azure.Identity AuthenticationRecord (no secrets): which cached account to use.</summary>
    public string Record { get; set; } = "";
}

// Values match IMsRdpClientSecuredSettings.AudioRedirectionMode.
public enum AudioMode { Local, Remote, None }

public class RdpOptions
{
    public ResolutionMode Resolution { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int ColorDepth { get; set; } = 32;
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectSmartCards { get; set; }
    public AudioMode Audio { get; set; }
    public bool AdminSession { get; set; }
    /// <summary>Windows shortcuts (Win, Alt+Tab) go to the remote session while it has focus.</summary>
    public bool RemoteShortcuts { get; set; } = true;
    public string? Gateway { get; set; }
    /// <summary>
    /// With <see cref="ResolutionMode.AllMonitors"/>: the monitors the full screen uses, by device name ("\\.\DISPLAY2");
    /// null = all of them. Names of monitors that are not plugged in are skipped (none left: all).
    /// </summary>
    public List<string>? Monitors { get; set; }

    public RdpOptions Clone() => (RdpOptions)MemberwiseClone();
}

public abstract partial class Node : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _expanded = true;
    public Guid? ParentId { get; set; }
    /// <summary>Position among the siblings of its kind (drag and drop); null = not placed yet, sorted by name after the rest.</summary>
    public double? Order { get; set; }
    /// <summary>null = inherit from parent group.</summary>
    public Guid? CredentialId { get; set; }
    /// <summary>null = inherit from parent group, then from global settings.</summary>
    public RdpOptions? Rdp { get; set; }
    /// <summary>Starting the Azure VMs ahead of work; null = as its group.</summary>
    public VmAutoStart? AutoStart { get; set; }
    /// <summary>Its sessions shut the Azure VM down once disconnected (deallocated); null = as its group.</summary>
    public bool? ShutDownVmAfter { get; set; }
    [ObservableProperty] private string? _notes;
    /// <summary>Web addresses to open from it (a group's go for the connections in it too); null = none of its own.</summary>
    public List<NodeLink>? Links { get; set; }
    /// <summary>Environment color ("#RRGGBB"), e.g. red for production; null = inherit from parent group.</summary>
    [ObservableProperty] private string? _color;
    /// <summary>Part of a multiple selection in the tree (Ctrl / Shift + click).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isMarked;
    /// <summary>Own color, else the nearest group's (set by the window on refresh).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string? _effectiveColor;

    public override string ToString() => Name;

    /// <summary>What a screen reader announces for its row: the name with what the row otherwise only shows (icons, the status dot).</summary>
    [JsonIgnore] public virtual string Spoken => $"{Name}, {Loc.Get("SpokenGroup")}";

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Spoken));
}

public partial class Group : Node
{
    [JsonIgnore] public ObservableCollection<Node> Children { get; } = [];
}

public partial class Connection : Node
{
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port = 3389;
    [ObservableProperty] private bool _favorite;
    /// <summary>Place in the favorites as the user arranged them (dragged); equal places go by name.</summary>
    public int FavoriteOrder { get; set; }
    public DateTime? LastUsed { get; set; }
    public string? Tags { get; set; }
    [ObservableProperty]
    [property: JsonIgnore]
    private bool? _online;
    /// <summary>A session of this connection is open.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isOpen;
    /// <summary>
    /// Since when a session of it was left logged on: disconnected, not signed out, so its programs may still run on the
    /// server. Known only from this app's own sessions; null after a sign-out.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeftRunning), nameof(LeftRunningTip))]
    private DateTime? _leftRunningAt;
    /// <summary>Optional Azure virtual machine behind it.</summary>
    public AzureVmLink? AzureVm { get; set; }
    /// <summary>Last known power state of <see cref="AzureVm"/> (refreshed with the host probe).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VmStatusLine))]
    [property: JsonIgnore]
    private VmPowerState? _vmState;
    /// <summary>Next auto-shutdown of <see cref="AzureVm"/> (UTC) while it runs; null when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VmShutdownText), nameof(VmShutdownTip), nameof(VmRunningTip), nameof(VmShutdownSkipped), nameof(VmShutdownWhen), nameof(VmStatusLine))]
    [property: JsonIgnore]
    private DateTime? _vmShutdownAt;
    /// <summary>The account may not change the auto-shutdown schedule (no Microsoft.DevTestLab/schedules/write): no postpone / skip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VmShutdownTip))]
    [property: JsonIgnore]
    private bool _vmShutdownReadOnly;
    [JsonIgnore] public bool VmShutdownSkipped => AzureVm?.ShutdownSkipped == true;
    // Today: the time alone; another day (after a skip) with its weekday.
    [JsonIgnore] public string? VmShutdownText => VmShutdownAt?.ToLocalTime() is { } at
        ? at.Date == DateTime.Today ? at.ToString("t") : $"{at:ddd} {at:t}" : null;
    /// <summary>The shutdown with its preposition, for sentences: "o 17:00", "w sob. 17:00".</summary>
    [JsonIgnore] public string? VmShutdownWhen => VmShutdownAt?.ToLocalTime() is not { } at ? null
        : Loc.Format(at.Date == DateTime.Today ? "AtTime" : at.DayOfWeek == DayOfWeek.Tuesday ? "OnTuesday" : "OnDay", VmShutdownText);
    [JsonIgnore] public string? VmShutdownTip => VmShutdownSkipped
        ? VmShutdownText is { } t ? Loc.Format("AzureShutdownTipSkipped", t) : null
        : VmShutdownWhen is { } when ? Loc.Format(VmShutdownReadOnly ? "AzureShutdownTipReadOnly" : "AzureShutdownTip", when) : null;
    /// <summary>The machine card's line on the home page: "running · shuts down at 17:00".</summary>
    [JsonIgnore] public string VmStatusLine => AzureVms.Describe(VmState)
        + (VmState is VmPowerState.Running && VmShutdownWhen is { } w ? " · " + Loc.Format("HomeVmShutsDown", w) : "");
    [JsonIgnore] public string VmRunningTip => VmShutdownWhen is { } w ? Loc.Format("VmTipRunningUntil", w) : Loc.Get("VmTipRunning");
    /// <summary>Where its session window was last closed: it opens there again.</summary>
    public WindowPlacement? Placement { get; set; }

    [JsonIgnore] public string Address => Port == 3389 ? Host : $"{Host}:{Port}";
    [JsonIgnore] public string? AzureTip => AzureVm is { } vm ? Loc.Format("AzureLinkedTip", vm.Name, vm.ResourceGroup) : null;
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public override string ToString() => Title;

    [JsonIgnore] public override string Spoken => string.Join(", ", new[]
    {
        Title, Name.Length > 0 ? Address : null,
        IsOpen ? Loc.Get("SessionOpen") : LeftRunningTip,
        VmState != null ? AzureVms.Describe(VmState) : Online is { } online ? Loc.Get(online ? "Reachable" : "Unreachable") : null,
        Favorite ? Loc.Get("Favorite") : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    [JsonIgnore] public bool HasLeftRunning => LeftRunningAt != null;
    [JsonIgnore] public string? LeftRunningTip => LeftRunningAt is { } at ? Loc.Format("LeftRunningTip", Loc.Ago(at, DateTime.Now)) : null;

    /// <summary>The tooltip's "… ago" is relative: called periodically to keep it current.</summary>
    public void RefreshAge() => OnPropertyChanged(nameof(LeftRunningTip));

    // Title and Address are derived, so views bound to them (tabs, cards, full-screen bar) refresh on rename/edit.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(Host)) OnPropertyChanged(nameof(Title));
        if (e.PropertyName is nameof(Host) or nameof(Port)) OnPropertyChanged(nameof(Address));
        if (e.PropertyName is nameof(Host) or nameof(Port) or nameof(IsOpen) or nameof(LeftRunningAt) or nameof(LeftRunningTip) or nameof(VmState)
            or nameof(Online) or nameof(Favorite)) OnPropertyChanged(nameof(Spoken));
    }
}

public partial class Credential : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private string _name = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FullUser))] private string _user = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FullUser))] private string? _domain;
    /// <summary>Password encrypted with DPAPI (current Windows user).</summary>
    public byte[]? Secret { get; set; }

    [JsonIgnore] public string FullUser => string.IsNullOrEmpty(Domain) ? User : $@"{Domain}\{User}";
    public override string ToString() => $"{Name} ({FullUser})";

    /// <summary>Empty password = no secret: Sessionwright asks for it when connecting.</summary>
    public void SetPassword(string? password) => Secret = string.IsNullOrEmpty(password) ? null : Store.Protect(password);
}

public class AppSettings
{
    /// <summary>"" = follow Windows, otherwise "en" / "pl".</summary>
    public string Language { get; set; } = "";
    /// <summary>System / Light / Dark.</summary>
    public string Theme { get; set; } = "System";
    public bool ProbeHosts { get; set; } = true;
    /// <summary>The accent (buttons, selection, icons) follows Windows; off: the blue of the app icon.</summary>
    public bool UseSystemAccent { get; set; } = true;
    public List<AzureAccount> AzureAccounts { get; set; } = [];
    /// <summary>Started with Windows into the tray; closing the window keeps it running (auto-start of VMs, quick connect).</summary>
    public bool RunInTray { get; set; }
    /// <summary>Global key that brings Sessionwright forward with the search ready ("Ctrl+Alt+F"); empty = off.</summary>
    public string QuickConnectKey { get; set; } = "Ctrl+Alt+F";
    /// <summary>Disconnect and sign out at once, without asking first.</summary>
    public bool SkipSessionConfirmations { get; set; }
    public RdpOptions Rdp { get; set; } = new();
    /// <summary>Main window placement from the last session; null = default (centered).</summary>
    public WindowPlacement? MainPlacement { get; set; }
    public double SidebarWidth { get; set; } = 320;
    /// <summary>Width of the full-screen bar in DIPs, as last resized; 0 = default.</summary>
    public double FullScreenBarWidth { get; set; }
    /// <summary>Where the full-screen bar's center sits across its monitor (0 = left edge, 1 = right) as last dragged; null = centered.</summary>
    public double? FullScreenBarCenter { get; set; }
    /// <summary>Colors picked in the color picker, the latest first ("#RRGGBB").</summary>
    public List<string> RecentColors { get; set; } = [];
    /// <summary>Where the machines window was last closed: it opens there again.</summary>
    public WindowPlacement? MachinesPlacement { get; set; }
    /// <summary>The favorites section folded to its header.</summary>
    public bool FavoritesCollapsed { get; set; }
    /// <summary>Machines hidden in the machines window ("subscription/group/name", lower case).</summary>
    public List<string> HiddenVms { get; set; } = [];
    /// <summary>
    /// Machines without a connection whose auto-shutdown Sessionwright postponed or skipped: their links keep the usual time, so
    /// it is put back (AzureShutdown) also when they are in no connection.
    /// </summary>
    public List<AzureVmLink> PendingShutdowns { get; set; } = [];
    /// <summary>The script library (run on a connection's machine); null = the built-in scripts, never edited.</summary>
    public List<RemoteScript>? Scripts { get; set; }
    /// <summary>Where a session's small always-on-top view was last closed: the next one opens there, at that size.</summary>
    public WindowPlacement? MiniViewPlacement { get; set; }
    /// <summary>The day ("yyyy-MM-dd") of the latest "What's new" entry already shown; null = none yet.</summary>
    public string? NewsSeen { get; set; }
    /// <summary>The last closed session window: new session windows open with its size.</summary>
    public WindowPlacement? SessionPlacement { get; set; }
    /// <summary>Update feed: UNC/local folder or http(s) URL with the Velopack releases; null/"" = the build-time default.</summary>
    public string? UpdateFeed { get; set; }
}

/// <summary>
/// Restored window bounds in physical pixels (as GetWindowPlacement reports them) plus the maximized state. Physical, not
/// DIPs: with per-monitor DPI a DIP position means something else on each monitor.
/// </summary>
public class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public class AppData
{
    /// <summary>Format of the file (<see cref="Store.CurrentSchema"/>); 0 = written before it was versioned.</summary>
    public int SchemaVersion { get; set; }
    public ObservableCollection<Group> Groups { get; set; } = [];
    public ObservableCollection<Connection> Connections { get; set; } = [];
    public ObservableCollection<Credential> Credentials { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
