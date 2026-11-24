using Sessionwright;
using Xunit;

public class PaletteTests
{
    [Theory]
    [InlineData("uruchom DEV-12", PaletteVerb.Start, "DEV-12")]
    [InlineData("Uruchom ponownie DEV-12", PaletteVerb.Restart, "DEV-12")]
    [InlineData("zalogowani na DEV-13", PaletteVerb.SignedIn, "DEV-13")]
    [InlineData("wyłącz dev", PaletteVerb.ShutDown, "dev")]
    [InlineData("wylacz dev", PaletteVerb.ShutDown, "dev")]
    [InlineData("Rozłącz z Serwer testowy", PaletteVerb.Disconnect, "Serwer testowy")]
    [InlineData("connect as dev 1", PaletteVerb.ConnectAs, "dev 1")]
    [InlineData("sign out of dev", PaletteVerb.SignOut, "dev")]
    [InlineData("wyloguj", PaletteVerb.SignOut, "")]
    [InlineData("link DEV-12", PaletteVerb.Link, "DEV-12")]
    [InlineData("linki dev", PaletteVerb.Link, "dev")]
    public void ReadsTheVerbAndTheConnection(string text, PaletteVerb verb, string rest) =>
        Assert.Equal((verb, rest), Palette.Parse(text));

    [Theory]
    [InlineData("DEV-12")]
    [InlineData("starter")] // a word that only begins like a verb
    [InlineData("uru")]
    public void LeavesOtherTextToTheSearch(string text) => Assert.Null(Palette.Parse(text));

    [Fact]
    public void FoldsPolishLettersOneForOne() => Assert.Equal("zazolc gesla jazn", Palette.Fold("Zażółć gęślą jaźń"));

    // One account written two ways counts once; two accounts with one user name in two domains stay two.
    [Theory]
    [InlineData("CORP\\jdoe", "jdoe@corp.com", true)]
    [InlineData("corp\\JDOE", "CORP\\jdoe", true)]
    [InlineData(".\\Administrator", "CORP\\Administrator", false)]
    [InlineData("CORP\\jdoe", "CORP\\asmith", false)]
    [InlineData("", "CORP\\jdoe", false)]
    public void TellsAccountsApart(string a, string b, bool same) => Assert.Equal(same, SessionManager.SameAccount(a, b));

    [Fact]
    public void KeepsEmojiAsTyped()
    {
        Assert.Equal("dev 🖥️ wylacz", Palette.Fold("DEV 🖥️ Wyłącz"));
        Assert.Equal((PaletteVerb.Start, "🖥️ dev"), Palette.Parse("start 🖥️ dev"));
    }
}
