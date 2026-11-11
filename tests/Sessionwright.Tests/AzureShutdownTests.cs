using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sessionwright;
using Xunit;

public class AzureShutdownTests
{
    const string Cet = "Central European Standard Time";

    [Fact]
    public void ParsesPortalAutoShutdownSchedule()
    {
        using var doc = JsonDocument.Parse("""
            {
              "id": "/subscriptions/1111/resourceGroups/DEV-RG/providers/Microsoft.DevTestLab/schedules/shutdown-computevm-DEV-12",
              "location": "westeurope",
              "properties": {
                "status": "Enabled", "taskType": "ComputeVmShutdownTask",
                "dailyRecurrence": { "time": "2015" }, "timeZoneId": "Central European Standard Time",
                "targetResourceId": "/subscriptions/1111/resourceGroups/DEV-RG/providers/Microsoft.Compute/virtualMachines/DEV-12"
              }
            }
            """);
        var s = AzureVms.ParseShutdown(doc.RootElement)!;
        Assert.Equal(("DEV-12", true, "2015", Cet), (s.VmName, s.Enabled, s.Time, s.TimeZoneId));
    }

    [Fact]
    public void IgnoresOtherScheduleTypes()
    {
        using var doc = JsonDocument.Parse("""{ "id": "x", "properties": { "taskType": "LabVmAutoStart", "targetResourceId": "a/b", "dailyRecurrence": { "time": "0800" } } }""");
        Assert.Null(AzureVms.ParseShutdown(doc.RootElement));
    }

    [Theory]
    // 20:15 CET (UTC+1 in winter): today if before, else tomorrow.
    [InlineData("2026-11-10T18:00:00Z", "2026-11-10T19:15:00Z")]
    [InlineData("2026-11-10T19:30:00Z", "2026-11-11T19:15:00Z")]
    // Summer time (UTC+2).
    [InlineData("2026-07-10T12:00:00Z", "2026-07-10T18:15:00Z")]
    public void NextShutdownFollowsTheScheduleTimeZone(string now, string expected)
    {
        var at = AzureVms.NextShutdown("2015", Cet, DateTime.Parse(now).ToUniversalTime());
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), at);
    }

    [Fact]
    public void UnreadableTimeHasNoShutdown() => Assert.Null(AzureVms.NextShutdown("8pm", Cet, DateTime.UtcNow));

    [Fact]
    public void PostponingMovesTheDailyTimeAndCrossesMidnight()
    {
        var now = DateTime.Parse("2026-11-10T19:00:00Z").ToUniversalTime(); // 20:00 CET
        Assert.Equal(("2115", DateTime.Parse("2026-11-10T20:15:00Z").ToUniversalTime()), AzureVms.ShiftShutdown("2015", Cet, now, 1));
        Assert.Equal("0015", AzureVms.ShiftShutdown("2015", Cet, now, 4).Time);
    }

    // SymbolIcon shows only one UTF-16 unit: an icon above U+FFFF renders as an unrelated character.
    [Fact]
    public void XamlIconsAreInTheBasicPlane() => CheckIcons();

    static void CheckIcons([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../src/Sessionwright"));
        // {ui:SymbolIcon X}, Symbol="X", <Setter Property="Symbol" Value="X" />, SymbolRegular.X in code
        var pattern = @"\{ui:SymbolIcon (\w+)|Symbol=""(\w+)""|Property=""Symbol"" Value=""(\w+)""|SymbolRegular\.(\w+)";
        var names = Directory.GetFiles(src, "*.xaml").Concat(Directory.GetFiles(src, "*.cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), pattern).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value))
            .Distinct().ToList();
        Assert.NotEmpty(names);
        var bad = names.Where(n => (int)Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(n) > 0xFFFF).ToList();
        Assert.Empty(bad);
    }

    // The pointer as in Windows 11: an arrow over controls, a hand only over links (ui:HyperlinkButton has it itself).
    [Fact]
    public void NoHandCursorOutsideLinks() => CheckCursors();

    static void CheckCursors([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../src/Sessionwright"));
        var bad = Directory.GetFiles(src, "*.xaml").Concat(Directory.GetFiles(src, "*.cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Cursor=""Hand""|Cursors\.Hand|Property=""Cursor"" Value=""Hand""")
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.Empty(bad);
    }

    // Smooth wheel scrolling comes from the app's implicit ScrollViewer style: a style of its own without BasedOn
    // replaces it (the settings pages scrolled in jumps), and item-based scrolling turns it off.
    [Fact]
    public void EveryScrollViewerKeepsSmoothScrolling() => CheckScrolling();

    static void CheckScrolling([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../src/Sessionwright"));
        var bad = Directory.GetFiles(src, "*.xaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<Style\b[^>]*TargetType=""(ui:Passive)?ScrollViewer""[^>]*>|CanContentScroll=""True""")
                .Where(m => !m.Value.Contains("BasedOn=")).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.Empty(bad);
    }
}

public class QuickConnectKeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+R")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("Alt+Windows+K")]
    public void KeyRoundTrips(string text) => Assert.Equal(text, SessionKeys.FormatKey(SessionKeys.ParseKey(text)!));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Nonsense")]
    public void NoKeyWhenEmptyOrUnreadable(string? text) => Assert.Null(SessionKeys.ParseKey(text));
}

public class AzureAutoStartTests
{
    static readonly VmAutoStart Weekdays8 = new() { Days = AutoStartDays.Weekdays, Time = "08:00" };
    static readonly DateTime Monday = new(2026, 11, 2);

    [Theory]
    [InlineData(7, 59, false)]   // not yet
    [InlineData(8, 0, true)]
    [InlineData(10, 30, true)]   // the PC came on later: caught up
    [InlineData(11, 0, false)]   // too late in the day to start it unasked
    public void DueOnWeekdaysWithinTheCatchUp(int hour, int minute, bool due) =>
        Assert.Equal(due, AzureAutoStart.Due(Weekdays8, new AzureVmLink(), Monday.AddHours(hour).AddMinutes(minute)));

    [Fact]
    public void NotOnWeekendsUnlessDaily()
    {
        var saturday = Monday.AddDays(5).AddHours(9);
        Assert.False(AzureAutoStart.Due(Weekdays8, new AzureVmLink(), saturday));
        Assert.True(AzureAutoStart.Due(new VmAutoStart { Days = AutoStartDays.Daily, Time = "08:00" }, new AzureVmLink(), saturday));
    }

    [Fact]
    public void OnceADay()
    {
        var now = Monday.AddHours(9);
        Assert.False(AzureAutoStart.Due(Weekdays8, new AzureVmLink { AutoStartedOn = Monday }, now));
        Assert.True(AzureAutoStart.Due(Weekdays8, new AzureVmLink { AutoStartedOn = Monday.AddDays(-3) }, now));
    }

    [Fact]
    public void OffOrNoneNeverDue()
    {
        Assert.False(AzureAutoStart.Due(null, new AzureVmLink(), Monday.AddHours(9)));
        Assert.False(AzureAutoStart.Due(new VmAutoStart { Days = AutoStartDays.Off }, new AzureVmLink(), Monday.AddHours(9)));
    }

    [Theory]
    [InlineData("8:00", "08:00")]
    [InlineData(" 07:45 ", "07:45")]
    [InlineData("7.30", "07:30")]
    [InlineData("25:00", null)]
    [InlineData("rano", null)]
    public void ParsesTimes(string text, string? expected) =>
        Assert.Equal(expected, AzureAutoStart.ParseTime(text)?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
}

public class AzurePermissionTests
{
    static bool Allows(string json, string action)
    {
        using var doc = JsonDocument.Parse(json);
        return AzureVms.Allows(doc.RootElement.EnumerateArray().ToList(), action);
    }

    const string Write = "Microsoft.DevTestLab/schedules/write";

    [Fact]
    public void VirtualMachineContributorMayChangeSchedules() =>
        Assert.True(Allows("""[ { "actions": [ "Microsoft.Compute/virtualMachines/*", "Microsoft.DevTestLab/schedules/*" ], "notActions": [] } ]""", Write));

    [Fact]
    public void OwnerMay() => Assert.True(Allows("""[ { "actions": [ "*" ], "notActions": [] } ]""", Write));

    [Fact]
    public void StartOnlyRoleMayNot() =>
        Assert.False(Allows("""[ { "actions": [ "Microsoft.Compute/virtualMachines/start/action", "Microsoft.Compute/*/read", "Microsoft.DevTestLab/schedules/read" ], "notActions": [] } ]""", Write));

    [Fact]
    public void ExcludedByNotActions() =>
        Assert.False(Allows("""[ { "actions": [ "*" ], "notActions": [ "Microsoft.DevTestLab/*" ] } ]""", Write));

    [Fact]
    public void AnotherRoleCanStillAllow() =>
        Assert.True(Allows("""[ { "actions": [ "*" ], "notActions": [ "Microsoft.DevTestLab/*" ] }, { "actions": [ "Microsoft.DevTestLab/schedules/*" ] } ]""", Write));
}
