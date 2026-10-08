using SmcCaTracker.Core.Links;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Tests;

/// <summary>smc-ca-tracker:// links from Workload Tracker's web list: read strictly, never run.</summary>
public class ItemLinkTests
{
    private const string Valid =
        "smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=abc-123&number=000303%20A&action=details";

    [Fact]
    public void AnItemLinkIsRead()
    {
        var link = ItemLink.Parse(Valid);

        Assert.Equal(new ItemLink("helix", "project.helixteam.com", "H-1", "rfi", "abc-123", "000303 A", "details"), link);
        Assert.Equal("RFI 000303 A", link!.ItemLabel);
        Assert.Equal(link, ItemLink.Parse(link.ToUri()));
    }

    [Theory]
    [InlineData("details")]
    [InlineData("open")]
    [InlineData("folder")]
    [InlineData("newest")]
    [InlineData("download")]
    [InlineData("verify")]
    [InlineData("discipline")]
    public void EveryButtonOfTheWebListIsAnAction(string action)
    {
        Assert.Equal(action, ItemLink.Parse(Valid.Replace("action=details", $"action={action}"))?.Action);
    }

    [Fact]
    public void WhatBrowsersAndTheWebPageMayVaryIsAccepted()
    {
        // A slash after "item", capitals, "+" for a space, other parameters (ignored), Procore ids with dots and colons.
        var link = ItemLink.Parse(
            "SMC-CA-TRACKER://ITEM/?provider=Procore&host=US02.procore.com&project=562949953&type=Submittal&id=1.2:3_4&number=03+30+00-1&action=Download&v=2");

        Assert.Equal(new ItemLink("procore", "us02.procore.com", "562949953", "submittal", "1.2:3_4", "03 30 00-1", "download"), link);
        Assert.Equal("Submittal 03 30 00-1", link!.ItemLabel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example/?provider=helix")]
    [InlineData("smc-ca-tracker:item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://items?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item/x?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open#run")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open&action=delete")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=delete")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open%20folder")]
    [InlineData("smc-ca-tracker://item?provider=github&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=change&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=..%5C..%5CWindows&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=a/b&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=C:%5Ctemp&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=evil.example%2Fpath&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=localhost&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=-bad.example&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=a..example&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=user%40evil.example&project=H-1&type=rfi&id=1&number=1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=RFI%0A1&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=RFI%001&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=%E2%80%AE1000&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=%ZZ&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=%C3&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=%&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=café&action=open")]
    [InlineData("smc-ca-tracker://item?provider=helix&host=project.helixteam.com&project=H-1&type=rfi&id=1&number=1&action=open&flag")]
    public void AnythingElseIsRefused(string? text)
    {
        Assert.Null(ItemLink.Parse(text));
    }

    [Fact]
    public void LongValuesAndLongLinksAreRefused()
    {
        var id = new string('a', 64);
        Assert.NotNull(ItemLink.Parse(Valid.Replace("id=abc-123", $"id={id}")));
        Assert.Null(ItemLink.Parse(Valid.Replace("id=abc-123", $"id={id}a")));
        Assert.Null(ItemLink.Parse(Valid.Replace("number=000303%20A", $"number={new string('9', 65)}")));
        Assert.NotNull(ItemLink.Parse(Valid.Replace("number=000303%20A", $"number={new string('9', 64)}")));
        Assert.Null(ItemLink.Parse(Valid + "&x=" + new string('x', 2048)));
        Assert.Null(ItemLink.Parse(Valid + string.Concat(Enumerable.Range(0, 10).Select(n => $"&p{n}=1"))));
    }

    [Fact]
    public void TheLinkIsFoundAmongTheArguments()
    {
        Assert.Equal(Valid.ToUpperInvariant(), ItemLink.FindInArguments(["--data-folder", @"C:\Temp\x", Valid.ToUpperInvariant()]));
        Assert.Null(ItemLink.FindInArguments(["--data-folder", @"C:\Temp\x"]));
    }
}

public class InstanceNamesTests
{
    [Fact]
    public void TheUsualCopyKeepsItsMutexAndADataFolderCopyHasItsOwnNames()
    {
        var usual = new AppPaths(AppPaths.Default.Root);
        var copy = new AppPaths(@"C:\Temp\ca-tracker-test");

        // Unchanged from earlier versions, so an old and a new copy still exclude each other.
        Assert.Equal(@"Local\SMC-CA-Tracker", InstanceNames.Mutex(usual));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(copy.Root.ToLowerInvariant())))[..16];
        Assert.Equal($@"Local\SMC-CA-Tracker-{hash}", InstanceNames.Mutex(copy));

        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var pipe = InstanceNames.Pipe(usual, sid, 1);
        Assert.StartsWith("SMC-CA-Tracker-1-", pipe);
        Assert.DoesNotContain(sid, pipe);
        Assert.Equal(pipe, InstanceNames.Pipe(new AppPaths(AppPaths.Default.Root), sid, 1));
        Assert.NotEqual(pipe, InstanceNames.Pipe(usual, sid, 2));
        Assert.NotEqual(pipe, InstanceNames.Pipe(usual, "S-1-5-21-1111111111-2222222222-3333333333-1002", 1));
        Assert.Equal($"{pipe}-{hash}", InstanceNames.Pipe(copy, sid, 1));
    }
}

public class ProtocolRegistrationTests
{
    private const string Exe = @"C:\Users\pat\AppData\Local\Programs\SMC CA Tracker\SmcCaTracker.exe";

    [Fact]
    public void TheCommandOpensTheAppWithTheLink()
    {
        Assert.Equal($"\"{Exe}\" \"%1\"", ProtocolRegistration.CommandFor(Exe));
        Assert.Equal($"\"{Exe}\",0", ProtocolRegistration.IconFor(Exe));
        Assert.Equal(@"Software\Classes\smc-ca-tracker", ProtocolRegistration.KeyPath);
        Assert.Equal(Exe, ProtocolRegistration.ProgramOf(ProtocolRegistration.CommandFor(Exe)));
        Assert.Equal(@"C:\Tools\app.exe", ProtocolRegistration.ProgramOf(@"C:\Tools\app.exe %1"));
        Assert.Null(ProtocolRegistration.ProgramOf("  "));
    }

    [Fact]
    public void ItRegistersWhenNothingIsOrTheRegisteredAppIsGone()
    {
        const string old = @"C:\Old\SmcCaTracker.exe";
        Assert.True(ProtocolRegistration.NeedsRegistering(null, Exe, _ => true));
        Assert.True(ProtocolRegistration.NeedsRegistering("", Exe, _ => true));
        Assert.True(ProtocolRegistration.NeedsRegistering("\"\" \"%1\"", Exe, _ => true));
        Assert.True(ProtocolRegistration.NeedsRegistering(ProtocolRegistration.CommandFor(old), Exe, _ => false));

        // This app already, or another copy that is still there (the installed one, while a build runs): left alone.
        Assert.False(ProtocolRegistration.NeedsRegistering(ProtocolRegistration.CommandFor(Exe.ToUpperInvariant()), Exe, _ => false));
        Assert.False(ProtocolRegistration.NeedsRegistering(ProtocolRegistration.CommandFor(old), Exe, path => path == old));
        Assert.False(ProtocolRegistration.NeedsRegistering(null, "", _ => true));
    }
}
