using SmcCaTracker.App.Services;

namespace SmcCaTracker.Core.Tests;

public sealed class ProviderSitesTests
{
    private static readonly Uri RfiList = new("https://project.helixteam.com/Projects/42/RFI");

    [Theory]
    [InlineData("https://project.helixteam.com/Account/Login?ReturnUrl=%2FProjects%2F42%2FRFI")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/authorize")]
    [InlineData("https://project.helixteam.com/Projects/42/RFI")]
    [InlineData("https://project.helixteam.com/projects/42/rfi/")]
    public void ItemPanelReopensTheItemAfterSignInOrAReload(string destination) =>
        Assert.True(ProviderSites.ClosesHelixItem(new Uri(destination), RfiList));

    [Theory]
    [InlineData("https://project.helixteam.com/Projects/42/Submittals")]
    [InlineData("https://project.helixteam.com/Projects/42/RFI/Download/7")]
    [InlineData("https://project.helixteam.com/")]
    public void ItemPanelLeavesYourOwnHelixPagesAlone(string destination) =>
        Assert.False(ProviderSites.ClosesHelixItem(new Uri(destination), RfiList));

    [Theory]
    [InlineData("https://project.helixteam.com/Account/Login?ReturnUrl=%2F", true)]
    [InlineData("https://project.helixteam.com/login", true)]
    [InlineData("https://project.helixteam.com/sign-in/", true)]
    [InlineData("https://app.procore.com/auth", true)]
    [InlineData("https://project.helixteam.com/sso/start", true)]
    [InlineData("https://project.helixteam.com/Login.aspx", true)]
    [InlineData("https://project.helixteam.com/Projects/42/Associated", false)]
    [InlineData("https://project.helixteam.com/Projects/42/Author", false)]
    [InlineData("https://app.procore.com/12/company/Accounts", false)]
    [InlineData("https://app.procore.com/rest/v1.0/me", false)]
    public void SignInPagesAreWholePathSegments(string address, bool signIn) =>
        Assert.Equal(signIn, ProviderSites.LooksLikeSignIn(new Uri(address)));

    [Fact]
    public void HelixsNewAppIsToldApartFromTheOldOneAndFromSignIn()
    {
        // Where Helix sent a project's Submittals list (seen 2026-10-03).
        var moved = new Uri("https://app.helixteam.com/projects/67a3688e17038d35d95b54fb/submittals");
        Assert.True(ProviderSites.IsHelixNewApp(moved));
        Assert.False(ProviderSites.LooksLikeSignIn(moved));
        Assert.False(ProviderSites.IsHelixNewApp(new Uri("https://project.helixteam.com/Projects/42/Submittals")));
        Assert.False(ProviderSites.IsHelixNewApp(new Uri("http://app.helixteam.com/")));
    }
}
