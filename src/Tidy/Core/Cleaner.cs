using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace Tidy.Core;

public class CleanItem
{
    public required string Path { get; init; }
    public required string Display { get; init; }
    public long Size { get; init; }
    public bool IsDir { get; init; }
    /// <summary>Recycle bin: the $I metadata file that goes with the $R payload.</summary>
    public string? Companion { get; init; }
    public string SizeText => Store.FormatBytes(Size);
    public string FileName => System.IO.Path.GetFileName(Display);
    public string Folder => System.IO.Path.GetDirectoryName(Display) ?? "";
}

public class CleanCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Icon { get; init; }
    public bool DefaultOn { get; init; }
    /// <summary>Files newer than this are never touched (an installer may be using them right now).</summary>
    public TimeSpan MinAge { get; init; }
    public required Func<IEnumerable<string>> Roots { get; init; }
    public bool RecycleBin { get; init; }
    public bool StopsUpdateServices { get; init; }
}

public class ScanResult
{
    public required CleanCategory Category { get; init; }
    public List<CleanItem> Items { get; } = [];
    public string? Note { get; set; }
    public List<string> Roots { get; } = [];
    public long Bytes => Items.Sum(i => i.Size);
}

public static class Cleaner
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string ProgData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static readonly List<CleanCategory> Categories =
    [
        new()
        {
            Id = "temp", Name = "Archivos temporales", Icon = "Document24", DefaultOn = true, MinAge = TimeSpan.FromHours(24),
            Description = "Restos que dejan programas e instaladores en las carpetas Temp. Se ignora todo lo de las últimas 24 h.",
            Roots = () => [Path.GetTempPath(), Path.Combine(Win, "Temp")],
        },
        new()
        {
            Id = "wer", Name = "Informes de errores", Icon = "ErrorCircle24", DefaultOn = true,
            Description = "Informes de cuelgues ya enviados o archivados y volcados de memoria de aplicaciones.",
            Roots = () => [Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue"),
                           Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER")],
        },
        new()
        {
            Id = "wu", Name = "Caché de Windows Update", Icon = "ArrowSync24", DefaultOn = true, StopsUpdateServices = true,
            Description = "Descargas de actualizaciones ya instaladas. Windows las vuelve a bajar si alguna vez las necesita.",
            Roots = () => [Path.Combine(Win, @"SoftwareDistribution\Download")],
        },
        new()
        {
            Id = "browser", Name = "Caché de navegadores", Icon = "Globe24",
            Description = "Solo caché de Chrome, Edge, Brave, Opera y Firefox. Nunca toca contraseñas, cookies, historial ni pestañas. Cierra el navegador para limpiar más.",
            Roots = BrowserRoots,
        },
        new()
        {
            Id = "shader", Name = "Caché de shaders (GPU)", Icon = "Games24",
            Description = "Shaders precompilados de NVIDIA/AMD/DirectX. Útil tras actualizar el driver; los juegos los regeneran (los primeros minutos pueden ir a tirones).",
            Roots = () => [Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, "D3DSCache"),
                           Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\GLCache"), Path.Combine(Local, @"Intel\ShaderCache"),
                           Path.Combine(ProgData, @"NVIDIA Corporation\NV_Cache")],
        },
        new()
        {
            Id = "recycle", Name = "Papelera de reciclaje", Icon = "Delete24", RecycleBin = true,
            Description = "Vacía la papelera. Esto es definitivo: revisa la lista antes de confirmar.",
            Roots = () => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", WindowsIdentity.GetCurrent().User!.Value)),
        },
    ];

    static IEnumerable<string> BrowserRoots()
    {
        string[] chromium = [@"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data"];
        foreach (var ud in chromium.Select(c => Path.Combine(Local, c)).Where(Directory.Exists))
        {
            foreach (var shared in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" }) yield return Path.Combine(ud, shared);
            foreach (var profile in Directory.EnumerateDirectories(ud))
            {
                var n = Path.GetFileName(profile);
                if (n != "Default" && !n.StartsWith("Profile ") && n != "Guest Profile") continue;
                foreach (var c in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" }) yield return Path.Combine(profile, c);
            }
        }
        foreach (var opera in new[] { "Opera Stable", "Opera GX Stable" })
            yield return Path.Combine(Local, "Opera Software", opera, @"Cache\Cache_Data");
        var ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
            foreach (var p in Directory.EnumerateDirectories(ff)) yield return Path.Combine(p, "cache2");
    }

    // Never descend into junctions/symlinks: a link inside Temp pointing at C:\Users would otherwise be followed.
    static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static bool UpdateRebootPending() =>
        Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null;

    public static ScanResult Scan(CleanCategory c)
    {
        var r = new ScanResult { Category = c };
        r.Roots.AddRange(c.Roots().Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists));
        if (c.StopsUpdateServices && UpdateRebootPending())
        {
            r.Note = "Hay una actualización esperando reinicio; se omite hasta que reinicies.";
            return r;
        }
        if (c.RecycleBin) { ScanRecycle(r); return r; }

        var cutoff = DateTime.Now - c.MinAge;
        foreach (var root in r.Roots)
        {
            if (IsReparse(root)) continue;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", Walk))
            {
                try
                {
                    if (Newest(f) > cutoff) continue;
                    r.Items.Add(new CleanItem { Path = f.FullName, Display = f.FullName, Size = f.Length });
                }
                catch (IOException) { }
            }
        }
        return r;
    }

    static void ScanRecycle(ScanResult r)
    {
        foreach (var root in r.Roots)
        foreach (var info in Directory.EnumerateFiles(root, "$I*"))
        {
            try
            {
                var payload = Path.Combine(root, "$R" + Path.GetFileName(info)[2..]);
                var (size, original) = ParseRecycleInfo(File.ReadAllBytes(info));
                r.Items.Add(new CleanItem
                {
                    Path = payload, Companion = info, Size = size, IsDir = Directory.Exists(payload),
                    Display = $"{original}  (en la papelera)",
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>$I file: v1 = 24-byte header + fixed 520-byte path; v2 = 24-byte header + int32 length + UTF-16 path.</summary>
    public static (long Size, string Original) ParseRecycleInfo(byte[] b)
    {
        long version = BitConverter.ToInt64(b, 0), size = BitConverter.ToInt64(b, 8);
        string path = version >= 2
            ? Encoding.Unicode.GetString(b, 28, Math.Min(BitConverter.ToInt32(b, 24) * 2, b.Length - 28))
            : Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
        return (size, path.TrimEnd('\0'));
    }

    public record CleanReport(int Deleted, long Bytes, int Skipped, string ListFile);

    /// <summary>Deletes exactly the confirmed items. Each one is re-validated against its category roots right before deletion.</summary>
    public static CleanReport Clean(IEnumerable<ScanResult> confirmed)
    {
        int deleted = 0, skipped = 0; long bytes = 0;
        var list = new StringBuilder();
        foreach (var r in confirmed.Where(r => r.Items.Count > 0))
        {
            var restart = r.Category.StopsUpdateServices ? StopUpdateServices() : null;
            if (r.Category.StopsUpdateServices && restart == null) { skipped += r.Items.Count; continue; }
            try
            {
                foreach (var item in r.Items)
                {
                    if (TryDelete(item, r.Roots)) { deleted++; bytes += item.Size; list.AppendLine(item.Display); }
                    else skipped++;
                }
                if (!r.Category.RecycleBin) RemoveEmptyDirs(r.Roots, DateTime.Now - r.Category.MinAge);
            }
            finally { if (restart != null) StartServices(restart); }
        }
        var logDir = Store.CleanupsDir;
        Directory.CreateDirectory(logDir);
        var listFile = Path.Combine(logDir, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(listFile, list.ToString(), Encoding.UTF8);
        Store.Log($"Cleanup: {deleted} items deleted ({Store.FormatBytes(bytes)}), {skipped} skipped (in use or protected). List: {listFile}");
        if (deleted > 0) History.Add(HistoryKind.Clean, $"Limpieza: {Store.FormatBytes(bytes)} liberados", $"{deleted} archivos temporales (no se pueden recuperar). Lista: {Path.GetFileName(listFile)}");
        return new CleanReport(deleted, bytes, skipped, listFile);
    }

    public static bool IsUnder(string path, IEnumerable<string> roots)
    {
        var full = Path.GetFullPath(path);
        return roots.Any(root =>
        {
            var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && full.Length > r.Length;
        });
    }

    /// <summary>Copied/extracted files keep an old modified date; the creation date shows they just arrived.</summary>
    static DateTime Newest(FileSystemInfo f) => f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime;

    static bool IsReparse(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    static bool TryDelete(CleanItem item, List<string> roots)
    {
        try
        {
            if (!IsUnder(item.Path, roots)) { Store.Log($"Cleanup: BLOCKED outside the allow list: {item.Path}"); return false; }
            if (item.IsDir)
            {
                if (Directory.Exists(item.Path) && !IsReparse(item.Path)) DeleteTree(item.Path);
            }
            else if (File.Exists(item.Path))
            {
                var attrs = File.GetAttributes(item.Path);
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(item.Path, attrs & ~FileAttributes.ReadOnly);
                File.Delete(item.Path);
            }
            if (item.Companion != null && IsUnder(item.Companion, roots)) File.Delete(item.Companion);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Recursive delete that unlinks junctions/symlinks instead of entering them.
    /// (Directory.Delete(recursive) calls DeleteVolumeMountPoint on junctions, which can throw.)</summary>
    public static void DeleteTree(string dir)
    {
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (e is DirectoryInfo) Directory.Delete(e.FullName, false); else File.Delete(e.FullName);
                continue;
            }
            if (e is DirectoryInfo d) { DeleteTree(d.FullName); continue; }
            if (e.Attributes.HasFlag(FileAttributes.ReadOnly)) e.Attributes &= ~FileAttributes.ReadOnly;
            e.Delete();
        }
        Directory.Delete(dir, false);
    }

    static void RemoveEmptyDirs(List<string> roots, DateTime cutoff)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || IsReparse(root)) continue;
            var dirs = new DirectoryInfo(root).EnumerateDirectories("*", Walk).OrderByDescending(d => d.FullName.Length).ToList();
            foreach (var d in dirs)
            {
                try
                {
                    if (Newest(d) > cutoff || d.EnumerateFileSystemInfos().Any()) continue;
                    d.Delete(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    static List<ServiceController>? StopUpdateServices()
    {
        var stopped = new List<ServiceController>();
        try
        {
            foreach (var name in new[] { "wuauserv", "bits" })
            {
                var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Stopped) continue;
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                stopped.Add(sc);
            }
            return stopped;
        }
        catch (Exception e)
        {
            Store.Log($"Cleanup: could not stop Windows Update ({e.Message}); its cache is skipped.");
            StartServices(stopped);
            return null;
        }
    }

    static void StartServices(List<ServiceController> services)
    {
        foreach (var sc in services)
            try { sc.Start(); } catch (Exception e) { Store.Log($"Cleanup: could not restart {sc.ServiceName}: {e.Message}"); }
    }
}
