using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tidy.Core;

public record Release(Version Version, string Notes, string DownloadUrl, long Size, string? Sha256, string? Sha256Url);

/// <summary>New versions come from the GitHub releases of Jeray665/Tidy. A download is only used if its size, SHA-256
/// (published by GitHub for each asset), product name and version all match; otherwise it's deleted.</summary>
public static class Updater
{
    const string Api = "https://api.github.com/repos/Jeray665/Tidy/releases/latest";
    public const string Asset = "Tidy-Setup.exe";
    public const string ReleasesPage = Install.Repo + "/releases";
    public static Version Current => Trim(typeof(Updater).Assembly.GetName().Version!);
    public static Release? Available { get; private set; }
    public static event Action<Release>? Found;
    static Timer? _timer;

    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(5) };
    static Updater() => Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Tidy/{Current}");

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Once a day in the background, if the user didn't turn it off in Settings.</summary>
    public static void StartAuto() => _timer = new Timer(async _ =>
    {
        if (!Store.Settings.AutoUpdateCheck || DateTime.Now - (Store.Settings.LastUpdateCheck ?? DateTime.MinValue) < TimeSpan.FromHours(20)) return;
        try { await Check(); } catch (Exception e) { Store.Log($"Update check failed: {e.Message}"); }
    }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(3));

    public static async Task<Release?> Check()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Api));
        Store.Settings.LastUpdateCheck = DateTime.Now;
        Store.Save();
        var r = Parse(doc.RootElement);
        Available = r != null && r.Version > Current ? r : null;
        Store.Log($"Update check: latest {r?.Version.ToString() ?? "?"}, running {Current}");
        if (Available != null) Found?.Invoke(Available);
        return Available;
    }

    public static Release? Parse(JsonElement root)
    {
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean() || root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var v)) return null;
        JsonElement? exe = null; string? shaUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            if (name == Asset) exe = a;
            if (name == Asset + ".sha256") shaUrl = a.GetProperty("browser_download_url").GetString();
        }
        if (exe is not { } e) return null;
        var digest = e.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString()!.StartsWith("sha256:")
            ? dg.GetString()![7..] : null;
        var url = e.GetProperty("browser_download_url").GetString()!;
        // only ever download from GitHub itself
        if (!url.StartsWith(Install.Repo + "/releases/download/", StringComparison.Ordinal)) return null;
        return new(Trim(v), root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", url, e.GetProperty("size").GetInt64(), digest, shaUrl);
    }

    /// <summary>Downloads into the admin-only data folder and verifies it. Returns the verified file.</summary>
    public static async Task<string> Download(Release r, IProgress<double>? progress = null)
    {
        var sha = r.Sha256;
        if (sha == null && r.Sha256Url != null) sha = (await Http.GetStringAsync(r.Sha256Url)).Trim().Split(' ', '\t', '\n')[0];
        if (sha is not { Length: 64 }) throw new InvalidDataException("La versión nueva no publica su huella SHA-256; no se instala.");

        Directory.CreateDirectory(Store.DownloadsDir);
        var path = Path.Combine(Store.DownloadsDir, $"Tidy-{r.Version}.exe");
        var part = path + ".part";
        using (var resp = await Http.GetAsync(r.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(part);
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (done > r.Size) throw new InvalidDataException("La descarga es más grande de lo anunciado.");
                progress?.Report((double)done / r.Size);
            }
        }
        try { Verify(part, r, sha); }
        catch { File.Delete(part); throw; }
        File.Move(part, path, true);
        Store.Log($"Update {r.Version} downloaded and verified ({path})");
        return path;
    }

    public static void Verify(string file, Release r, string sha256)
    {
        if (new FileInfo(file).Length != r.Size) throw new InvalidDataException("El tamaño del archivo no coincide.");
        using (var f = File.OpenRead(file))
            if (!Convert.ToHexString(SHA256.HashData(f)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La huella SHA-256 no coincide: el archivo no es el publicado.");
        var info = FileVersionInfo.GetVersionInfo(file);
        if (info.ProductName != "Tidy" || !Version.TryParse(info.FileVersion, out var fv) || Trim(fv) != r.Version)
            throw new InvalidDataException("El archivo no es Tidy o su versión no coincide.");
    }

    /// <summary>Puts the verified exe in place of the running one. The caller restarts Tidy.</summary>
    public static void Apply(string verified)
    {
        Install.ReplaceExe(verified, Install.Current);
        try { File.Delete(verified); } catch { }
        Store.Log($"Updated {Install.Current} from {Current}");
    }
}

/// <summary>CHANGELOG.md, built into the exe: "## 1.1.0" headers followed by "- item" lines.</summary>
public static class Changelog
{
    public static List<(Version Version, List<string> Items)> Parse(string text)
    {
        var list = new List<(Version, List<string>)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ") && Version.TryParse(line[3..].Trim().TrimStart('v'), out var v)) list.Add((v, []));
            else if (line.StartsWith("- ") && list.Count > 0) list[^1].Item2.Add(line[2..].Trim());
        }
        return list;
    }

}
