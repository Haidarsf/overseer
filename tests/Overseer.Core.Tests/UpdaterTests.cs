using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Overseer.Core;
using Xunit;

namespace Overseer.Core.Tests;

public class UpdaterTests
{
    const string Dl = "https://github.com/Haidarsf/overseer/releases/download/v9.1.0/";

    static string ReleaseJson(string? digest = null, bool withSums = false, string url = Dl + "Overseer-Setup-9.1.0-x64.exe", string tag = "v9.1.0") => $$"""
        {
          "tag_name": "{{tag}}", "name": "Overseer {{tag}}", "draft": false, "prerelease": false,
          "html_url": "https://github.com/Haidarsf/overseer/releases/tag/{{tag}}",
          "published_at": "2026-12-01T10:00:00Z",
          "body": "## Added\r\n- **Updates** from `Settings`\r\n\r\n**Full Changelog**: https://example",
          "assets": [
            { "name": "overseer-win-x64.zip", "size": 10, "browser_download_url": "{{Dl}}overseer-win-x64.zip" },
            { "name": "Overseer-Setup-9.1.0-x64.exe", "size": 11, "browser_download_url": "{{url}}" {{(digest != null ? $", \"digest\": \"{digest}\"" : "")}} }
            {{(withSums ? $", {{ \"name\": \"Overseer-Setup-9.1.0-x64.exe.sha256\", \"size\": 96, \"browser_download_url\": \"{Dl}Overseer-Setup-9.1.0-x64.exe.sha256\" }}" : "")}}
          ]
        }
        """;

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Serves fixed bodies per URL, like GitHub's release downloads.</summary>
    sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v1.4.2-beta.1", "1.4.2")]
    [InlineData("V2", "2.0.0")]
    public void Parses_tags(string tag, string expected) => Assert.Equal(Version.Parse(expected), Updater.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    public void Rejects_bad_tags(string? tag) => Assert.Null(Updater.ParseTag(tag));

    [Fact]
    public void Reads_a_release_and_compares_versions()
    {
        var r = Updater.Parse(ReleaseJson())!;
        Assert.Equal(new Version(9, 1, 0), r.Version);
        Assert.Equal(2, r.Assets.Count);
        Assert.True(Updater.IsNewer(r, new Version(0, 3, 0)));
        Assert.False(Updater.IsNewer(r, new Version(9, 1, 0)));
        Assert.False(Updater.IsNewer(r, new Version(10, 0, 0)));
    }

    [Fact]
    public void Ignores_drafts_and_prereleases()
    {
        Assert.Null(Updater.Parse(ReleaseJson().Replace("\"draft\": false", "\"draft\": true")));
        Assert.Null(Updater.Parse(ReleaseJson().Replace("\"prerelease\": false", "\"prerelease\": true")));
    }

    [Fact]
    public void Picks_the_installer_for_this_machine_from_this_repo_only()
    {
        var r = Updater.Parse(ReleaseJson())!;
        Assert.Equal("Overseer-Setup-9.1.0-x64.exe", Updater.Installer(r, Architecture.X64)?.Name);
        Assert.Null(Updater.Installer(r, Architecture.Arm64));   // no Arm installer: open the download page instead

        var elsewhere = Updater.Parse(ReleaseJson(url: "https://evil.example/Overseer-Setup-9.1.0-x64.exe"))!;
        Assert.Null(Updater.Installer(elsewhere, Architecture.X64));
        Assert.False(Updater.IsTrustedUrl("https://github.com/someone/overseer/releases/download/v1/x.exe"));
        Assert.False(Updater.IsTrustedUrl("https://github.com/Haidarsf/overseer/releases/download/../../x.exe"));
    }

    [Fact]
    public void Reads_checksum_files()
    {
        var h = new string('a', 64);
        Assert.Equal(h, Updater.HashFromSumsFile($"{h.ToUpperInvariant()}  Overseer-Setup-9.1.0-x64.exe\n", "Overseer-Setup-9.1.0-x64.exe"));
        Assert.Equal(h, Updater.HashFromSumsFile($"{h} *Overseer-Setup-9.1.0-x64.exe", "Overseer-Setup-9.1.0-x64.exe"));
        Assert.Equal(h, Updater.HashFromSumsFile(h, "anything"));
        Assert.Null(Updater.HashFromSumsFile($"{h}  other.exe", "Overseer-Setup-9.1.0-x64.exe"));
        Assert.Null(Updater.HashFromSumsFile("not a hash", "x"));
    }

    [Fact]
    public async Task Downloads_and_verifies_with_GitHubs_digest()
    {
        var body = Encoding.UTF8.GetBytes("pretend installer");
        var r = Updater.Parse(ReleaseJson(digest: "sha256:" + Sha(body)))!;
        var fake = new FakeGitHub(new() { [Dl + "Overseer-Setup-9.1.0-x64.exe"] = body });
        using var http = Updater.CreateClient(fake);
        var progress = new List<double>();
        var path = await Updater.Download(http, r, Updater.Installer(r, Architecture.X64)!, new SyncProgress(progress));
        Assert.Equal(body, File.ReadAllBytes(path));
        Assert.Contains(1.0, progress);
        File.Delete(path);
    }

    [Fact]
    public async Task Falls_back_to_the_sha256_file()
    {
        var body = Encoding.UTF8.GetBytes("pretend installer 2");
        var r = Updater.Parse(ReleaseJson(withSums: true))!;
        var fake = new FakeGitHub(new()
        {
            [Dl + "Overseer-Setup-9.1.0-x64.exe"] = body,
            [Dl + "Overseer-Setup-9.1.0-x64.exe.sha256"] = Encoding.ASCII.GetBytes(Sha(body) + "  Overseer-Setup-9.1.0-x64.exe\n"),
        });
        using var http = Updater.CreateClient(fake);
        var path = await Updater.Download(http, r, Updater.Installer(r, Architecture.X64)!);
        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public async Task Refuses_a_tampered_download_and_deletes_it()
    {
        var r = Updater.Parse(ReleaseJson(digest: "sha256:" + Sha(Encoding.UTF8.GetBytes("the real one"))))!;
        var fake = new FakeGitHub(new() { [Dl + "Overseer-Setup-9.1.0-x64.exe"] = Encoding.UTF8.GetBytes("something else") });
        using var http = Updater.CreateClient(fake);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.Download(http, r, Updater.Installer(r, Architecture.X64)!));
        Assert.Contains("didn't match", ex.Message);
        var dir = Path.Combine(Path.GetTempPath(), "overseer-update");
        Assert.False(File.Exists(Path.Combine(dir, "Overseer-Setup-9.1.0-x64.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "Overseer-Setup-9.1.0-x64.exe.part")));
    }

    [Fact]
    public async Task Refuses_a_download_without_any_checksum()
    {
        var r = Updater.Parse(ReleaseJson())!;
        var fake = new FakeGitHub(new() { [Dl + "Overseer-Setup-9.1.0-x64.exe"] = new byte[] { 1 } });
        using var http = Updater.CreateClient(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.Download(http, r, Updater.Installer(r, Architecture.X64)!));
        Assert.DoesNotContain(Dl + "Overseer-Setup-9.1.0-x64.exe", fake.Requested);   // never fetched the file itself
    }

    [Fact]
    public void Runs_the_installer_quietly_and_restarts()
    {
        var psi = Updater.InstallerStart(@"C:\t\setup.exe", @"C:\Users\x\AppData\Local\Programs\Overseer\");
        Assert.Contains("/SILENT", psi.ArgumentList);
        Assert.Contains("/RELAUNCH", psi.ArgumentList);
        Assert.Contains("/CLOSEAPPLICATIONS", psi.ArgumentList);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("/CURRENTUSER", psi.ArgumentList);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Assert.Contains("/ALLUSERS", Updater.InstallerStart(@"C:\t\setup.exe", Path.Combine(pf, "Overseer")).ArgumentList);
        }
    }

    [Fact]
    public void Only_installs_itself_when_the_installer_put_it_there()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.False(Updater.CanInstallItself(dir));
        File.WriteAllText(Path.Combine(dir, "unins000.exe"), "");
        Assert.Equal(OperatingSystem.IsWindows(), Updater.CanInstallItself(dir));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Shows_notes_as_plain_text()
    {
        var notes = Updater.PlainNotes(Updater.Parse(ReleaseJson())!.Notes);
        Assert.Equal("Added\n• Updates from Settings", notes);
    }

    sealed class SyncProgress(List<double> into) : IProgress<double>
    {
        public void Report(double value) => into.Add(value);
    }
}
