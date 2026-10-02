using System.Security.Cryptography;
using SmcCaTracker.Core.Updates;

namespace SmcCaTracker.Core.Tests;

public class UpdateFeedTests
{
    private static readonly Version Current = new(3, 0, 0);
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Latest(string installer, string version = "3.0.1", string? sha256 = Sha) =>
        sha256 is null
            ? $$"""{ "version": "{{version}}", "installer": "{{installer}}", "notes": "n" }"""
            : $$"""{ "version": "{{version}}", "installer": "{{installer}}", "sha256": "{{sha256}}", "notes": "n" }""";

    [Fact]
    public void ABareInstallerNameIsResolvedNextToLatestJson()
    {
        var update = UpdateFeed.Parse(@"\\server\apps\ca-tracker", Latest("SMC-CA-Tracker-Setup-3.0.1.exe"), Current, out var reason);
        Assert.Null(reason);
        Assert.NotNull(update);
        Assert.Equal(Path.Combine(@"\\server\apps\ca-tracker", "SMC-CA-Tracker-Setup-3.0.1.exe"), update!.Installer);
        Assert.Equal(new Version(3, 0, 1), update.Version);
        Assert.Equal(Sha, update.Sha256);

        var web = UpdateFeed.Parse("https://apps.example.com/ca-tracker/", Latest("Setup 3.0.1.exe"), Current, out _);
        Assert.Equal("https://apps.example.com/ca-tracker/Setup%203.0.1.exe", web!.Installer);
    }

    [Theory]
    [InlineData(@"C:\Users\Public\evil.exe")]
    [InlineData(@"\\evil\share\setup.exe")]
    [InlineData(@"..\setup.exe")]
    [InlineData("sub/setup.exe")]
    [InlineData("https://evil.example.com/setup.exe")]
    [InlineData("http://evil.example.com/setup.exe")]
    [InlineData("")]
    public void AnInstallerThatIsNotAFileNextToLatestJsonIsRefused(string installer)
    {
        var update = UpdateFeed.Parse(@"\\server\apps\ca-tracker", Latest(installer.Replace(@"\", @"\\")), Current, out var reason);
        Assert.Null(update);
        Assert.NotNull(reason);
        Assert.Throws<NotSupportedException>(() => UpdateFeed.Resolve(@"\\server\apps\ca-tracker", installer));
    }

    [Fact]
    public void APlainHttpFeedIsRefused()
    {
        Assert.Null(UpdateFeed.Parse("http://apps.example.com/ca-tracker", Latest("setup.exe"), Current, out var reason));
        Assert.Contains("http", reason);
        Assert.Throws<NotSupportedException>(() => UpdateFeed.Resolve("http://apps.example.com/ca-tracker", "latest.json"));
    }

    [Fact]
    public void AMissingOrMalformedSha256MeansNoUpdate()
    {
        Assert.Null(UpdateFeed.Parse(@"\\server\apps", Latest("setup.exe", sha256: null), Current, out var reason));
        Assert.Contains("sha256", reason);
        Assert.Null(UpdateFeed.Parse(@"\\server\apps", Latest("setup.exe", sha256: "abc"), Current, out _));
        Assert.Null(UpdateFeed.Parse(@"\\server\apps", Latest("setup.exe", sha256: Sha.Replace('0', 'g')), Current, out _));
    }

    [Fact]
    public void VersionsCompareOnThreePartsOnly()
    {
        Assert.False(UpdateFeed.IsNewer(new Version(3, 0, 0, 0), new Version(3, 0, 0)));
        Assert.False(UpdateFeed.IsNewer(new Version(3, 0, 0, 5), new Version(3, 0, 0)));
        Assert.True(UpdateFeed.IsNewer(new Version(3, 0, 1), new Version(3, 0, 0, 9)));
        Assert.True(UpdateFeed.IsNewer(new Version(3, 1), new Version(3, 0, 7)));
        Assert.Null(UpdateFeed.Parse(@"\\server\apps", Latest("setup.exe", version: "3.0.0.0"), Current, out var reason));
        Assert.Null(reason);
        Assert.Null(UpdateFeed.Parse(@"\\server\apps", Latest("setup.exe", version: "2.9.9"), Current, out _));
    }

    [Fact]
    public async Task TheDownloadedFileMustMatchThePublishedSha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ca-tracker-test-{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = new byte[1234];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.True(await UpdateFeed.MatchesAsync(path, expected));
            Assert.True(await UpdateFeed.MatchesAsync(path, expected.ToUpperInvariant()));
            Assert.False(await UpdateFeed.MatchesAsync(path, Sha));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
