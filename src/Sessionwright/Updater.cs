using System.Reflection;
using Velopack;
using Velopack.Locators;

namespace Sessionwright;

/// <summary>
/// Velopack updates from the releases of a GitHub repository (https://github.com/owner/repo), a UNC/local folder or an
/// http(s) URL. A GitHub repository is read without a token: it serves updates once it is public, and a private one
/// just reports no update. Failures are logged and swallowed: an unreachable feed must never disturb open sessions.
/// </summary>
public static class Updater
{
    static readonly string? DefaultFeed = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateFeed")?.Value;

    public static string? BuiltInFeed => string.IsNullOrWhiteSpace(DefaultFeed) ? null : DefaultFeed.Trim();

    /// <summary>The settings value wins over the build-time default (-p:UpdateFeed=); null = updates disabled.</summary>
    public static string? Feed => new[] { App.Store?.Data.Settings.UpdateFeed, DefaultFeed }
        .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))?.Trim();

    // Set only when started from a Velopack install; dev builds and plain publish output never update.
    static string? InstalledVersion => VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString() : null;

    public static bool IsInstalled => InstalledVersion != null;

    /// <summary>Installed and a feed is configured.</summary>
    public static bool IsEnabled => IsInstalled && Feed is { } feed && IsTrusted(feed);

    public static string CurrentVersion => InstalledVersion
        ?? typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    // A GitHub repository through its releases; else the string overload picks SimpleWebSource for http(s) and
    // SimpleFileSource for anything else (UNC/local path).
    /// <summary>
    /// Not plain http: whoever sits on the way (a hotel network, a proxy) could hand out their own "update", and it runs
    /// as the user. https and a folder (UNC/local, as safe as its access rights) are taken.
    /// </summary>
    public static bool IsTrusted(string feed) => !feed.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    static UpdateManager? Manager(string? feed = null) => (feed ?? Feed) is not { } f || !IsInstalled || !IsTrusted(f) ? null
        : IsGitHub(f) ? new UpdateManager(new Velopack.Sources.GithubSource(f.TrimEnd('/'), null, false))
        : new UpdateManager(f);

    internal static bool IsGitHub(string feed) =>
        Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Trim('/').Split('/').Length == 2;

    /// <summary>The newer release on the feed; null when there is none, updates are disabled, or the check failed.</summary>
    /// <param name="feed">Another feed than the saved one (typed into Settings, not saved yet).</param>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default, string? feed = null)
    {
        try
        {
            // Off the UI thread: the file source reads the share synchronously and an offline server blocks for seconds.
            return Manager(feed) is { } m ? await Task.Run(m.CheckForUpdatesAsync, cancel).WaitAsync(cancel) : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Error(ex, "Update");
            return null;
        }
    }

    /// <summary>
    /// Downloads the release into the install's packages folder (skipped when already there). Progress (0-100) is
    /// reported on the caller's thread. False on failure or cancel.
    /// </summary>
    public static async Task<bool> DownloadAsync(UpdateInfo update, Action<int>? progress = null, CancellationToken cancel = default)
    {
        try
        {
            if (Manager() is not { } m) return false;
            IProgress<int>? report = progress is null ? null : new Progress<int>(progress);
            await Task.Run(() => m.DownloadUpdatesAsync(update, p => report?.Report(p), cancel), cancel);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Error(ex, "Update");
            return false;
        }
    }

    /// <summary>
    /// Starts the Velopack updater, which waits for this process to exit, applies the downloaded release and starts
    /// the new version. True when the updater runs: the caller then shuts down.
    /// </summary>
    public static bool ApplyAndRestart(UpdateInfo update)
    {
        try
        {
            if (Manager() is not { } m) return false;
            m.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update");
            return false;
        }
    }
}
