namespace Sessionwright;

/// <summary>What the editor holds for a connection or a group, as plain values: no window is needed to check or save it.</summary>
public sealed class NodeForm
{
    public const string VpnMode = "vpn", CommandMode = "command", NoneMode = "none";

    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Port { get; set; } = "";
    public ConnectionProtocol Protocol { get; set; }
    public string SshUser { get; set; } = "";
    public string Tags { get; set; } = "";
    /// <summary>The network card's address to wake the machine with; empty = none.</summary>
    public string Mac { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>The notes are sealed and the master password was not entered: they are neither shown nor written.</summary>
    public bool NotesLocked { get; set; }
    public List<(string Name, string Url)> Links { get; set; } = [];
    public Guid? ParentId { get; set; }
    public Guid? CredentialId { get; set; }
    public string? Color { get; set; }
    /// <summary>Its own remote desktop settings; null = as its group.</summary>
    public RdpOptions? Rdp { get; set; }
    /// <summary>Own settings were asked for but could not be read (a screen size out of range).</summary>
    public bool RdpInvalid { get; set; }
    public AutoStartDays? AutoStart { get; set; }
    public string AutoStartTime { get; set; } = "";
    public bool? ShutDownVmAfter { get; set; }
    /// <summary>One of the modes above; null = as its group.</summary>
    public string? BeforeMode { get; set; }
    public string Vpn { get; set; } = "";
    public Guid? VpnCredentialId { get; set; }
    public string Command { get; set; } = "";
    public string WaitMinutes { get; set; } = "";
    /// <summary>A group's: where its connections come from.</summary>
    public string Source { get; set; } = OwnSource;
    public const string OwnSource = "own", AzureSource = "azure", SharedSource = "shared";
    public string SharedList { get; set; } = "";
    public AzureSyncRule? Sync { get; set; }
    /// <summary>A connection's Azure machine.</summary>
    public AzureVmLink? Vm { get; set; }
}

public enum NodeField { Name, Host, Port, Mac, Link, RdpSize, AutoStartTime, Vpn, Command, WaitMinutes, SharedList }

/// <param name="Index">Which link, for <see cref="NodeField.Link"/>.</param>
public sealed record NodeFormError(NodeField Field, string Message, int Index = -1);

/// <summary>
/// Checks an edited connection or group and writes it. Everything is checked first: after an error nothing was
/// written, so cancelling the editor then changes nothing.
/// </summary>
public static class NodeEdit
{
    /// <summary>Null when it was saved; otherwise the first thing that is wrong and the field it is in.</summary>
    public static NodeFormError? Save(Store store, Node node, NodeForm form)
    {
        if (form.RdpInvalid) return new(NodeField.RdpSize, Loc.Get("InvalidSize"));
        // Only web addresses: one is typed into the server's Run box, which would run anything else.
        for (var i = 0; i < form.Links.Count; i++)
        {
            var (linkName, url) = (form.Links[i].Name.Trim(), form.Links[i].Url.Trim());
            if (url.Length == 0 && linkName.Length == 0) continue;
            if (!Links.IsSafeTemplate(url)) return new(NodeField.Link, Loc.Format("LinkInvalid", url), i);
        }
        string? startTime = null;
        if (form.AutoStart is AutoStartDays.Weekdays or AutoStartDays.Daily)
        {
            if (AzureAutoStart.ParseTime(form.AutoStartTime) is not { } time) return new(NodeField.AutoStartTime, Loc.Get("InvalidTime"));
            startTime = time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        BeforeConnect? before = null;
        if (form.BeforeMode is { } mode)
        {
            before = new BeforeConnect();
            if (mode != NodeForm.NoneMode)
            {
                if (mode == NodeForm.VpnMode)
                {
                    (before.Vpn, before.VpnCredentialId) = (form.Vpn.Trim(), form.VpnCredentialId);
                    if (before.Vpn.Length == 0) return new(NodeField.Vpn, Loc.Get("VpnRequired"));
                }
                else if ((before.Command = form.Command.Trim()).Length == 0) return new(NodeField.Command, Loc.Get("BeforeConnectCommandRequired"));
                if (!int.TryParse(form.WaitMinutes, out var minutes) || minutes is < 1 or > 60)
                    return new(NodeField.WaitMinutes, Loc.Get("BeforeConnectInvalidWait"));
                before.WaitMinutes = minutes;
            }
        }
        var shared = node is Group && form.Source == NodeForm.SharedSource;
        if (shared && !SharedLists.IsAllowed(form.SharedList)) return new(NodeField.SharedList, Loc.Get("SharedListInsecure"));
        var name = form.Name.Trim();
        if (node is Group && name.Length == 0) return new(NodeField.Name, Loc.Get("NameRequired"));
        string host = "";
        var port = 0;
        if (node is Connection)
        {
            if (!Store.TryParseAddress(form.Host, out host, out port)) return new(NodeField.Host, Loc.Format("InvalidAddress", form.Host));
            // "host:port" typed into the host field wins over the port field.
            var hostHasPort = host != form.Host.Trim().Trim('[', ']');
            if (!hostHasPort && (!int.TryParse(form.Port, out port) || port is < 1 or > 65535)) return new(NodeField.Port, Loc.Get("InvalidPort"));
            if (form.Mac.Trim().Length > 0 && WakeOnLan.ParseMac(form.Mac) == null) return new(NodeField.Mac, Loc.Get("InvalidMac"));
        }

        // Everything is checked by now.
        if (node is Connection c)
        {
            // Readdressed by hand: the group's Azure sync leaves it alone from now on.
            if (c.Synced && !string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)) c.Synced = false;
            (c.Host, c.Port, c.Protocol) = (host, port, form.Protocol);
            c.SshUser = form.Protocol == ConnectionProtocol.Ssh && form.SshUser.Trim() is { Length: > 0 } user ? user : null;
            c.Tags = form.Tags.Trim() is { Length: > 0 } tags ? tags : null;
            c.Mac = WakeOnLan.ParseMac(form.Mac) is { } mac ? WakeOnLan.Format(mac) : null;
            if (c.AzureVm == null || form.Vm == null || !AzureVms.Same(c.AzureVm, form.Vm)) c.VmState = null;
            c.AzureVm = form.Vm;
            // An empty name stays empty: the title follows the host, also after the host is edited later.
        }
        var autoStart = form.AutoStart is { } days ? new VmAutoStart { Days = days, Time = startTime ?? "08:00" } : null;
        // A new plan may start today, even if an older one already did.
        if (autoStart?.ToString() != node.AutoStart?.ToString())
            foreach (var affected in node is Group g ? store.Data.Connections.Where(x => store.Ancestors(x).Contains(g)) : node is Connection self ? [self] : [])
                if (affected.AzureVm != null) affected.AzureVm.AutoStartedOn = null;
        node.AutoStart = autoStart;
        node.ShutDownVmAfter = form.ShutDownVmAfter;
        node.BeforeConnect = before;
        if (node is Group group)
        {
            // One source at a time: what was not chosen is cleared.
            group.AzureSync = form.Source == NodeForm.AzureSource ? form.Sync : null;
            group.SharedList = shared && form.SharedList.Trim() is { Length: > 0 } source ? source : null;
        }
        node.Name = name;
        if (!form.NotesLocked) (node.Notes, node.SealedNotes) = (form.Notes.Trim() is { Length: > 0 } notes ? notes : null, null);
        node.Links = form.Links.Where(l => l.Url.Trim().Length > 0)
            .Select(l => new NodeLink { Name = l.Name.Trim(), Url = l.Url.Trim() }).ToList() is { Count: > 0 } links ? links : null;
        (node.ParentId, node.CredentialId, node.Rdp, node.Color) = (form.ParentId, form.CredentialId, form.Rdp, form.Color);
        return null;
    }
}
