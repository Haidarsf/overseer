using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Overseer.Core;

public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, DateTimeOffset? Published, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Checks GitHub for a newer published release and, on Windows installs made with the Overseer installer,
/// downloads the setup program, verifies its SHA-256 and runs it quietly.
/// Only talks to api.github.com and this repository's release downloads; sends no personal data.
/// </summary>
public static class Updater
{
    public const string Repo = "Haidarsf/overseer";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version) ?? new Version(0, 0, 0);

    public static string CurrentText => Text(Current);
    public static string Text(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    static Version? Normalize(Version? v) => v == null ? null : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>"v1.2.3", "1.2", "v1.2.3-beta+5" → 1.2.3 (pre-release suffixes are ignored).</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        return Normalize(v);
    }

    public static ReleaseInfo? Parse(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject;
        if (o == null) return null;
        if (o["draft"]?.GetValue<bool>() == true || o["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = o["tag_name"]?.GetValue<string>();
        var v = ParseTag(tag);
        if (v == null) return null;
        var assets = new List<ReleaseAsset>();
        if (o["assets"] is JsonArray arr)
            foreach (var a in arr.OfType<JsonObject>())
            {
                var name = a["name"]?.GetValue<string>();
                var url = a["browser_download_url"]?.GetValue<string>();
                if (name == null || url == null) continue;
                assets.Add(new ReleaseAsset(name, url, a["size"]?.GetValue<long>() ?? 0, a["digest"]?.GetValue<string>()));
            }
        DateTimeOffset? published = DateTimeOffset.TryParse(o["published_at"]?.GetValue<string>(), out var p) ? p : null;
        return new ReleaseInfo(v, tag!, o["body"]?.GetValue<string>() ?? "", o["html_url"]?.GetValue<string>() ?? ReleasesPage, published, assets);
    }

    public static bool IsNewer(ReleaseInfo r, Version? current = null) => r.Version > (current ?? Current);

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Overseer-updater", CurrentText));
        return http;
    }

    /// <summary>The latest published (not draft, not pre-release) release, or null when there is none yet.</summary>
    public static async Task<ReleaseInfo?> Latest(HttpClient http, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if ((int)res.StatusCode == 403 || (int)res.StatusCode == 429)
            throw new HttpRequestException("GitHub is limiting requests right now. Try again in an hour.");
        res.EnsureSuccessStatusCode();
        return Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>The Windows installer in the release for this machine, if there is one.</summary>
    public static ReleaseAsset? Installer(ReleaseInfo r, Architecture? arch = null)
    {
        var suffix = (arch ?? RuntimeInformation.ProcessArchitecture) switch
        {
            Architecture.X64 => "-x64.exe",
            Architecture.Arm64 => "-arm64.exe",
            _ => null,
        };
        if (suffix == null) return null;
        return r.Assets.FirstOrDefault(a => a.Name.StartsWith("Overseer-Setup-", StringComparison.OrdinalIgnoreCase)
                                         && a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                                         && IsTrustedUrl(a.Url));
    }

    /// <summary>Downloads are only taken from this repository's own release files.</summary>
    public static bool IsTrustedUrl(string url) => url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && !url.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// The expected SHA-256 (lower-case hex) for an asset: GitHub's own digest of the uploaded file,
    /// or the line in a "&lt;name&gt;.sha256" file published next to it.
    /// </summary>
    public static async Task<string?> ExpectedHash(HttpClient http, ReleaseInfo r, ReleaseAsset asset, CancellationToken ct = default)
    {
        if (asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsHex64(d[7..]))
            return d[7..].ToLowerInvariant();
        var sums = r.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase) && IsTrustedUrl(a.Url));
        if (sums == null) return null;
        var text = await http.GetStringAsync(sums.Url, ct).ConfigureAwait(false);
        return HashFromSumsFile(text, asset.Name);
    }

    /// <summary>Reads "&lt;hash&gt;  &lt;file&gt;" lines (sha256sum / shasum format), or a file that holds only the hash.</summary>
    public static string? HashFromSumsFile(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (!IsHex64(parts[0])) continue;
            if (parts.Length == 1 || parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    static bool IsHex64(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>Downloads the installer to a temporary folder and checks its SHA-256. Throws when it can't be verified.</summary>
    public static async Task<string> Download(HttpClient http, ReleaseInfo r, ReleaseAsset asset, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!IsTrustedUrl(asset.Url)) throw new InvalidOperationException("The download isn't from Overseer's GitHub releases.");
        var expected = await ExpectedHash(http, r, asset, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This release has no checksum for the installer, so it can't be checked. Download it from GitHub instead.");

        var dir = Path.Combine(Path.GetTempPath(), "overseer-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(asset.Name));
        var partial = target + ".part";

        using (var res = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
            }
        }

        string actual;
        await using (var f = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(partial);
            throw new InvalidOperationException("The downloaded installer didn't match its checksum, so it was deleted. Try again later.");
        }
        File.Move(partial, target, true);
        return target;
    }

    /// <summary>The folder Overseer runs from.</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>True when this copy was installed by the Overseer installer (it leaves an uninstaller next to the app).</summary>
    public static bool CanInstallItself(string? appDir = null) =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(appDir ?? AppDir, "unins000.exe"));

    /// <summary>
    /// Runs the installer without its wizard. It closes Overseer if it's still open, installs over the
    /// current copy (for this user, or for everyone when it was installed under Program Files) and starts Overseer again.
    /// </summary>
    public static ProcessStartInfo InstallerStart(string installer, string? appDir = null)
    {
        var dir = Path.GetFullPath(appDir ?? AppDir);
        var forAll = IsUnder(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                  || IsUnder(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        var psi = new ProcessStartInfo(installer) { UseShellExecute = true };
        foreach (var a in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH", forAll ? "/ALLUSERS" : "/CURRENTUSER" })
            psi.ArgumentList.Add(a);
        return psi;
    }

    static bool IsUnder(string dir, string root) =>
        root.Length > 0 && dir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain text for showing release notes in the app: no Markdown markers, no trailing link lines.</summary>
    public static string PlainNotes(string body, int max = 1500)
    {
        var lines = new List<string>();
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            var l = raw.TrimEnd();
            if (l.StartsWith("**Full Changelog**", StringComparison.Ordinal)) continue;
            l = l.TrimStart('#').TrimStart();
            if (l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal)) l = "• " + l[2..];
            l = l.Replace("**", "").Replace("`", "");
            if (l.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(l);
        }
        var text = string.Join("\n", lines).Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }
}
