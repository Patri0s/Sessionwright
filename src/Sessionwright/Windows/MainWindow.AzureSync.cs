using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MenuItem = System.Windows.Controls.MenuItem;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace Sessionwright;

// Groups kept in step with an Azure subscription. Part of the main window (see MainWindow.xaml.cs).
public partial class MainWindow
{
    // ---------- Azure sync ----------

    void Menu_SyncAzure(object sender, RoutedEventArgs e)
    {
        if (Selected is Group { AzureSync: not null } group) _ = SyncAzureAsync(group, manual: true);
    }

    /// <summary>At start, quietly: every group with a rule, one after another, a moment after the window is up.</summary>
    async Task SyncAzureGroupsAsync()
    {
        if (Store.Data.Settings.AzureAccounts.Count == 0 || !Store.Data.Groups.Any(g => g.AzureSync != null)) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        foreach (var group in Store.Data.Groups.Where(g => g.AzureSync != null).ToList()) await SyncAzureAsync(group, manual: false);
    }

    /// <summary>
    /// Brings a group in step with Azure: adds connections for new VMs and readdresses the ones the sync made. A
    /// connection whose VM is gone is removed only when the user, asked after a sync they started, says so.
    /// </summary>
    async Task SyncAzureAsync(Group group, bool manual)
    {
        AzureSyncPlan plan;
        try { plan = await AzureSync.PlanAsync(Store, group); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException
                                       or AzureSignInRequiredException or TaskCanceledException)
        {
            Log.Info($"Azure sync of {group.Name}: {ex.Message}");
            group.SyncTip = Loc.Format("AzureSyncMarkFailed", DateTime.Now.ToString("t"), ex.Message);
            if (manual) Notification.Problem(this, Loc.Format("AzureSyncFailedTitle", group.Name), ex.Message, warning: true);
            return;
        }
        if (!Store.Data.Groups.Contains(group) || App.IsExiting) return; // deleted while Azure was asked
        AzureSync.Apply(Store, plan);
        Log.Info($"Azure sync of {group.Name}: {plan.Add.Count} added, {plan.Update.Count} readdressed, {plan.Missing.Count} no longer in Azure");
        group.SyncTip = Loc.Format("AzureSyncMarkDone", DateTime.Now.ToString("t"), plan.Add.Count, plan.Update.Count);
        if (plan.Add.Count + plan.Update.Count > 0) Refresh();
        if (!manual) return;

        var summary = Loc.Format("AzureSyncDone", group.Name, plan.Add.Count, plan.Update.Count);
        if (plan.NoAddresses) summary += "\n\n" + Loc.Get("AzureSyncNoAddresses");
        // An open session's connection stays: its window would lose what it belongs to.
        var removable = plan.Missing.Where(c => !c.IsOpen).ToList();
        if (removable.Count == 0)
        {
            // Nothing to decide: said in the corner, not in a window that has to be closed.
            Notification.Show(this, SymbolRegular.CloudSync24, Loc.Get("AzureSyncTitle"), summary, TimeSpan.FromSeconds(plan.NoAddresses ? 40 : 12));
            return;
        }
        var names = string.Join("\n", removable.Select(c => c.Title));
        if (!Dialog.Confirm(this, summary + "\n\n" + Loc.Format("AzureSyncMissing", names), Loc.Get("AzureSyncRemove"), DialogKind.Warning,
                danger: true, cancelText: Loc.Get("AzureSyncKeep"))) return;
        foreach (var c in removable) Store.Delete(c);
        Refresh();
    }

    void Menu_SortByName(object sender, RoutedEventArgs e)
    {
        Store.SortByName(TargetGroup);
        Refresh();
    }

    static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
