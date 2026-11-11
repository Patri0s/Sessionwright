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
}
