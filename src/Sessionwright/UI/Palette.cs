using System.Globalization;
using System.Text;

namespace Sessionwright;

/// <summary>What a command typed into the search does to a connection: "start DEV-12", "zalogowani na DEV-13".</summary>
public enum PaletteVerb { Connect, ConnectAs, Edit, Start, ShutDown, Restart, SignedIn, SignOut, Disconnect, Postpone, Link }

public static class Palette
{
    // Both languages whatever the UI language (a Polish user may still type "start"), compared without diacritics
    // ("wylacz"). Longest first: "uruchom ponownie" before "uruchom", "connect as" before "connect".
    static readonly (string Words, PaletteVerb Verb)[] Verbs = new (string, PaletteVerb)[]
    {
        ("connect as", PaletteVerb.ConnectAs), ("polacz jako", PaletteVerb.ConnectAs),
        ("connect", PaletteVerb.Connect), ("polacz", PaletteVerb.Connect),
        ("edit", PaletteVerb.Edit), ("edytuj", PaletteVerb.Edit),
        ("start", PaletteVerb.Start), ("uruchom", PaletteVerb.Start), ("wlacz", PaletteVerb.Start),
        ("restart", PaletteVerb.Restart), ("uruchom ponownie", PaletteVerb.Restart),
        ("shut down", PaletteVerb.ShutDown), ("shutdown", PaletteVerb.ShutDown), ("stop", PaletteVerb.ShutDown),
        ("wylacz", PaletteVerb.ShutDown), ("zatrzymaj", PaletteVerb.ShutDown),
        ("signed in", PaletteVerb.SignedIn), ("who", PaletteVerb.SignedIn), ("zalogowani", PaletteVerb.SignedIn), ("kto", PaletteVerb.SignedIn),
        ("sign out", PaletteVerb.SignOut), ("wyloguj", PaletteVerb.SignOut),
        ("disconnect", PaletteVerb.Disconnect), ("rozlacz", PaletteVerb.Disconnect),
        ("postpone", PaletteVerb.Postpone), ("odloz", PaletteVerb.Postpone),
        ("links", PaletteVerb.Link), ("link", PaletteVerb.Link), ("linki", PaletteVerb.Link),
    }.OrderByDescending(v => v.Item1.Length).ToArray();

    // Read as a sentence: "zalogowani na X", "sign out of X", "rozłącz z X".
    static readonly string[] Fillers = ["na ", "on ", "of ", "from ", "z ", "ze ", "to ", "do "];

    /// <summary>The verb the text starts with and the rest (a connection to look for; may be empty), or null.</summary>
    public static (PaletteVerb Verb, string Target)? Parse(string text)
    {
        var folded = Fold(text.Trim());
        foreach (var (words, verb) in Verbs)
        {
            if (!folded.StartsWith(words, StringComparison.Ordinal) || folded.Length > words.Length && folded[words.Length] != ' ') continue;
            // Folding keeps the length (one letter for one), so the rest is cut from the text as typed.
            var rest = text.Trim()[words.Length..].TrimStart();
            var filler = Fillers.FirstOrDefault(f => Fold(rest).StartsWith(f, StringComparison.Ordinal));
            return (verb, filler == null ? rest : rest[filler.Length..].TrimStart());
        }
        return null;
    }

    /// <summary>Lower case without diacritics, one letter for one: "Wyłącz" → "wylacz".</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch == 'ł') sb.Append('l');
            else if (ch.ToString().Normalize(NormalizationForm.FormD) is var d && d.Length > 1
                     && CharUnicodeInfo.GetUnicodeCategory(d[1]) == UnicodeCategory.NonSpacingMark) sb.Append(d[0]);
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
