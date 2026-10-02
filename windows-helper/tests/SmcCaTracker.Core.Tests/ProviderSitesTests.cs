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
}
