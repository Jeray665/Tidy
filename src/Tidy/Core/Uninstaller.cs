using System.Diagnostics;
using System.IO;
using System.Management;
using EnumerationOptions = System.IO.EnumerationOptions;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Tidy.Core;

public class InstalledProgram
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string? Icon { get; init; }
    public long Size { get; init; }
    public DateTime? Installed { get; init; }
    /// <summary>Uninstall entry, e.g. "HKLM64\SOFTWARE\...\Uninstall\{GUID}". null for Store apps.</summary>
    public string? RegKey { get; init; }
    public string? MsiCode { get; init; }
    public string? PackageFullName { get; init; }
    public string? PackageFamily { get; init; }
    public bool IsStore => PackageFullName != null;
    /// <summary>Runtimes other apps depend on (VC++, .NET, DirectX...): allowed, but with a warning.</summary>
    public bool Shared { get; init; }
    public string Source => IsStore ? "Microsoft Store" : RegKey!.StartsWith("HKCU") ? "Solo tu usuario" : RegKey.StartsWith("HKLM32") ? "32 bits" : "Escritorio";
}

public enum LeftoverKind { Folder, File, RegKey, RegValue, Service, Task }

public class Leftover
{
    public required LeftoverKind Kind { get; init; }
    /// <summary>Folder/file path, "HKLM64\..." key, "HKCU\...|ValueName", service name or task path.</summary>
    public required string Target { get; init; }
    public required string Category { get; init; }
    public string Reason { get; init; } = "";
    public long Size { get; init; }
    public bool Selected { get; set; } = true;
    public string Display => Kind switch
    {
        LeftoverKind.RegValue => Target.Replace("|", " › "),
        LeftoverKind.Service => $"Servicio: {Target}",
        LeftoverKind.Task => $"Tarea programada: {Target}",
        _ => Target,
    };
}

public record QuarantineItem(LeftoverKind Kind, string Target, string Stored, string? Data = null, string? DataKind = null);

public class Quarantine
{
    public required string Id { get; init; }
    public required string App { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;
    public List<QuarantineItem> Items { get; init; } = [];
    public long Bytes { get; set; }
    public string Dir => Path.Combine(Uninstaller.QuarantineRoot, Id);
}

/// <summary>Deep uninstall: run the app's own uninstaller, then find what it left behind. Leftovers are never deleted outright:
/// they're moved to a quarantine (files) or exported (registry, services, tasks) so everything can be put back.</summary>
public static class Uninstaller
{
    public static string QuarantineRoot => Path.Combine(Store.SecureDir, "quarantine");
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    static readonly Regex MsiGuid = new(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
    static readonly Regex SafePackage = new(@"^[A-Za-z0-9._~-]+$");
    static readonly Regex SharedRuntime = new(@"Visual C\+\+|\.NET (Runtime|Desktop Runtime|Framework)|ASP\.NET|DirectX|Windows SDK|WebView2|Windows App Runtime|vcredist|Microsoft Edge$", RegexOptions.IgnoreCase);

    // ---------- inventory ----------
    public static List<InstalledProgram> List()
    {
        var list = new List<InstalledProgram>();
        foreach (var (tag, hive, view) in new[] { ("HKLM64", RegistryHive.LocalMachine, RegistryView.Registry64), ("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32), ("HKCU", RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(UninstallPath);
            if (root == null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (Equals(k.GetValue("SystemComponent"), 1) || k.GetValue("ParentKeyName") != null) continue;
                if (k.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update") continue;
                var uninstall = k.GetValue("UninstallString") as string ?? "";
                var isMsi = Equals(k.GetValue("WindowsInstaller"), 1) && MsiGuid.IsMatch(id);
                if (uninstall == "" && !isMsi) continue;
                DateTime? date = k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
                list.Add(new InstalledProgram
                {
                    Name = name.Trim(), Publisher = (k.GetValue("Publisher") as string ?? "").Trim(), Version = k.GetValue("DisplayVersion") as string ?? "",
                    InstallLocation = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"').TrimEnd('\\'),
                    UninstallString = uninstall, Icon = IconPath(k.GetValue("DisplayIcon") as string),
                    Size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0, Installed = date,
                    RegKey = $@"{tag}\{UninstallPath}\{id}", MsiCode = isMsi ? id : null, Shared = SharedRuntime.IsMatch(name),
                });
            }
        }
        list.AddRange(StoreApps());
        // the same product often registers in more than one place
        return list.GroupBy(p => (p.Name.ToLowerInvariant(), p.Version)).Select(g => g.First())
                   .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string? IconPath(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Split(',')[0].Trim().Trim('"');
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    static List<InstalledProgram> StoreApps()
    {
        var (_, json) = Store.Run(PowerShell, "-NoProfile -NonInteractive -Command \"Get-AppxPackage -PackageTypeFilter Main | " +
            "Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -eq 'Store' } | " +
            "Select-Object Name,PackageFullName,PackageFamilyName,Version,InstallLocation | ConvertTo-Json -Compress\"");
        var start = json.IndexOfAny(['[', '{']);
        if (start < 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            return items.Select(e =>
            {
                string S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var loc = S("InstallLocation");
                var (display, publisher) = Manifest(loc);
                return new InstalledProgram
                {
                    Name = display ?? S("Name").Split('.').Last(), Publisher = publisher ?? "", Version = S("Version"), InstallLocation = loc,
                    PackageFullName = S("PackageFullName"), PackageFamily = S("PackageFamilyName"),
                };
            }).Where(p => SafePackage.IsMatch(p.PackageFullName!)).ToList();
        }
        catch (Exception e) { Store.Log($"Store apps unreadable: {e.Message}"); return []; }
    }

    static (string?, string?) Manifest(string dir)
    {
        try
        {
            var x = XDocument.Load(Path.Combine(dir, "AppxManifest.xml"));
            var props = x.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            string? Get(string n) => props?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value is { } v && !v.StartsWith("ms-resource:") ? v : null;
            return (Get("DisplayName"), Get("PublisherDisplayName"));
        }
        catch { return (null, null); }
    }

    // ---------- step 1: the app's own uninstaller ----------
    /// <summary>Runs the vendor uninstaller and waits for it. Returns false if it couldn't be started.</summary>
    public static async Task<(bool Ok, string Message)> RunUninstaller(InstalledProgram p)
    {
        Store.Log($"Uninstall: {p.Name} {p.Version} ({p.RegKey ?? p.PackageFullName})");
        if (p.IsStore)
        {
            var (code, output) = await Task.Run(() => Store.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{p.PackageFullName}'\""));
            return code == 0 ? (true, "") : (false, output.Trim());
        }
        string exe, args;
        var msi = p.MsiCode ?? (p.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ? MsiGuid.Match(p.UninstallString).Value : "");
        if (msi != "") { exe = Path.Combine(Environment.SystemDirectory, "msiexec.exe"); args = $"/x {msi}"; }
        else if (!SplitCommand(p.UninstallString, out exe, out args)) return (false, "No se encuentra el desinstalador del programa (puede que ya esté borrado). Puedes eliminar sus restos igualmente.");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
            await proc.WaitForExitAsync();
            Store.Log($"Uninstaller of {p.Name} exited with code {proc.ExitCode}");
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>"C:\x\unins000.exe" /SILENT  →  exe + args. The exe must exist.</summary>
    public static bool SplitCommand(string cmd, out string exe, out string args)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            exe = end > 0 ? cmd[1..end] : cmd.Trim('"');
            args = end > 0 ? cmd[(end + 1)..].Trim() : "";
        }
        else
        {
            var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = i > 0 ? cmd[..(i + 4)] : cmd;
            args = i > 0 ? cmd[(i + 4)..].Trim() : "";
        }
        return Path.IsPathRooted(exe) && File.Exists(exe);
    }

    public static bool StillInstalled(InstalledProgram p) => p.IsStore
        ? Store.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"if (Get-AppxPackage | Where-Object PackageFullName -eq '{p.PackageFullName}') {{ exit 0 }} else {{ exit 1 }}\"").Exit == 0
        : OpenKey(p.RegKey!) is { } k && Dispose(k);

    static bool Dispose(IDisposable d) { d.Dispose(); return true; }

    // ---------- step 2: leftovers ----------
    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>"7-Zip 24.08 (x64)" → "7-Zip". Removes versions, architectures and editions in brackets.</summary>
    public static string BaseName(string name)
    {
        var n = Regex.Replace(name, @"\s*[\(\[].*?[\)\]]", "");
        n = Regex.Replace(n, @"\s+v?\d+([.,]\d+)+.*$", "");
        n = Regex.Replace(n, @"\s+(x64|x86|64-bit|32-bit|\d{4})$", "", RegexOptions.IgnoreCase);
        return n.Trim(' ', '-', '–');
    }

    public static string CleanPublisher(string p) =>
        Regex.Replace(p, @"[,.]?\s*(Inc|Ltd|LLC|GmbH|S\.?A|S\.?L|Corp|Corporation|Co|Limited|AB|AG|BV|Pty)\.?$", "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');

    /// <summary>Names that can never be treated as "this app's folder/key", however the app is called.</summary>
    static readonly HashSet<string> Generic =
    [
        "microsoft", "windows", "common", "commonfiles", "temp", "tmp", "packages", "programs", "program", "system", "system32", "shared", "users",
        "public", "default", "desktop", "documents", "downloads", "appdata", "local", "roaming", "locallow", "cache", "caches", "config", "data",
        "settings", "setup", "update", "updater", "installer", "install", "app", "apps", "application", "applications", "bin", "lib", "tools", "games",
        "microsoftcorporation", "intel", "nvidia", "nvidiacorporation", "amd", "realtek", "google", "mozilla", "adobe", "apple", "oracle", "classes",
        "policies", "wow6432node", "clients", "software", "drivers", "driver", "steamapps", "crashdumps", "logs", "log", "fonts", "help",
    ];

    static IEnumerable<string> Names(InstalledProgram p)
    {
        var names = new List<string> { BaseName(p.Name) };
        if (p.InstallLocation != "") names.Add(Path.GetFileName(p.InstallLocation.TrimEnd('\\')));
        if (p.Icon != null) names.Add(Path.GetFileNameWithoutExtension(p.Icon));
        return names.Select(Norm).Where(n => n.Length >= 3 && !Generic.Contains(n)).Distinct();
    }

    static string? PublisherNorm(InstalledProgram p) => Norm(CleanPublisher(p.Publisher)) is { Length: >= 3 } n && !Generic.Contains(n) ? n : null;

    static string Up(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    static IEnumerable<(string Root, string Category)> FolderRoots() =>
    [
        (Up(Environment.SpecialFolder.ProgramFiles), "Archivos del programa"),
        (Up(Environment.SpecialFolder.ProgramFilesX86), "Archivos del programa"),
        (Path.Combine(Up(Environment.SpecialFolder.LocalApplicationData), "Programs"), "Archivos del programa"),
        (Up(Environment.SpecialFolder.CommonApplicationData), "Datos y configuración"),
        (Up(Environment.SpecialFolder.ApplicationData), "Datos y configuración"),
        (Up(Environment.SpecialFolder.LocalApplicationData), "Datos y configuración"),
        (Path.Combine(Up(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"), "Datos y configuración"),
        (Path.Combine(Up(Environment.SpecialFolder.MyDocuments), "My Games"), "Partidas y documentos"),
        (Up(Environment.SpecialFolder.MyDocuments), "Partidas y documentos"),
        (Path.Combine(Up(Environment.SpecialFolder.UserProfile), "Saved Games"), "Partidas y documentos"),
        (Path.Combine(Up(Environment.SpecialFolder.StartMenu), "Programs"), "Accesos directos"),
        (Path.Combine(Up(Environment.SpecialFolder.CommonStartMenu), "Programs"), "Accesos directos"),
    ];

    /// <summary>Folders that must never be removed as a whole, whatever an uninstall entry says (some write InstallLocation = "C:\" or Program Files).</summary>
    public static bool IsProtectedPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return true; }
        if (full.Length <= 3) return true; // drive roots
        var critical = new[]
        {
            Up(Environment.SpecialFolder.Windows), Up(Environment.SpecialFolder.ProgramFiles), Up(Environment.SpecialFolder.ProgramFilesX86),
            Up(Environment.SpecialFolder.CommonApplicationData), Up(Environment.SpecialFolder.UserProfile), Up(Environment.SpecialFolder.ApplicationData),
            Up(Environment.SpecialFolder.LocalApplicationData), Up(Environment.SpecialFolder.MyDocuments), Up(Environment.SpecialFolder.Desktop),
            Up(Environment.SpecialFolder.CommonDesktopDirectory), Up(Environment.SpecialFolder.StartMenu), Up(Environment.SpecialFolder.CommonStartMenu),
            Path.Combine(Up(Environment.SpecialFolder.UserProfile), "Downloads"), Path.GetDirectoryName(Up(Environment.SpecialFolder.UserProfile))!,
            Store.SecureDir, Store.Dir, AppContext.BaseDirectory.TrimEnd('\\'),
        }.Where(c => c != "").Select(c => c.TrimEnd('\\'));
        foreach (var c in critical)
        {
            if (full.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;
            // anything that contains a critical folder (e.g. C:\Users) is off limits too
            if (c.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        var win = Up(Environment.SpecialFolder.Windows);
        if (full.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in new[] { "Common Files", "WindowsApps", "Microsoft", "Windows Defender", "Windows NT", "dotnet" })
            if (critical.Take(3).Skip(1).Any(pf => full.Equals(Path.Combine(pf, c), StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    public static List<Leftover> FindLeftovers(InstalledProgram p)
    {
        var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
        void Add(Leftover l) { if (!found.ContainsKey(l.Target)) found[l.Target] = l; }
        var names = Names(p).ToHashSet();
        var publisher = PublisherNorm(p);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        // install folder itself
        if (p.InstallLocation != "" && Directory.Exists(p.InstallLocation) && !IsProtectedPath(p.InstallLocation) && !IsLink(p.InstallLocation))
            Add(new() { Kind = LeftoverKind.Folder, Target = Path.GetFullPath(p.InstallLocation), Category = "Archivos del programa", Reason = "Carpeta de instalación", Size = DirSize(p.InstallLocation) });

        // name matches under the usual roots, and Publisher\App
        foreach (var (root, cat) in FolderRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root, opts))
            {
                var n = Norm(Path.GetFileName(dir));
                if (names.Contains(n)) AddDir(dir, cat, "Mismo nombre que la app");
                else if (publisher != null && n == publisher)
                {
                    var subs = SafeDirs(dir, opts).ToList();
                    var mine = subs.Where(s => names.Contains(Norm(Path.GetFileName(s)))).ToList();
                    foreach (var s in mine) AddDir(s, cat, $"Carpeta de {CleanPublisher(p.Publisher)}");
                    // the publisher folder goes too only if nothing else of that publisher lives there
                    if (mine.Count > 0 && mine.Count == subs.Count && !Directory.EnumerateFiles(dir, "*", opts).Any())
                        AddDir(dir, cat, "Carpeta del fabricante que se quedaría vacía");
                }
            }
            // start-menu shortcuts that point into the install folder
            if (cat == "Accesos directos")
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    if (PointsInto(lnk, p) || names.Contains(Norm(Path.GetFileNameWithoutExtension(lnk))))
                        Add(new() { Kind = LeftoverKind.File, Target = lnk, Category = cat, Reason = "Acceso directo", Size = new FileInfo(lnk).Length });
        }
        foreach (var desk in new[] { Up(Environment.SpecialFolder.Desktop), Up(Environment.SpecialFolder.CommonDesktopDirectory) })
            if (Directory.Exists(desk))
                foreach (var lnk in Directory.EnumerateFiles(desk, "*.lnk", opts))
                    if (PointsInto(lnk, p)) Add(new() { Kind = LeftoverKind.File, Target = lnk, Category = "Accesos directos", Reason = "Acceso directo del escritorio", Size = new FileInfo(lnk).Length });

        // Store app data
        if (p.PackageFamily != null)
        {
            var pkg = Path.Combine(Up(Environment.SpecialFolder.LocalApplicationData), "Packages", p.PackageFamily);
            if (Directory.Exists(pkg)) Add(new() { Kind = LeftoverKind.Folder, Target = pkg, Category = "Datos y configuración", Reason = "Datos de la app de la Store", Size = DirSize(pkg) });
        }

        // registry
        if (p.RegKey != null && OpenKey(p.RegKey) is { } uk) { uk.Dispose(); Add(new() { Kind = LeftoverKind.RegKey, Target = p.RegKey, Category = "Registro", Reason = "Entrada de \"Programas instalados\"" }); }
        foreach (var root in new[] { @"HKCU\Software", @"HKLM64\SOFTWARE", @"HKLM32\SOFTWARE" })
        {
            using var k = OpenKey(root);
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                var n = Norm(sub);
                if (names.Contains(n)) Add(new() { Kind = LeftoverKind.RegKey, Target = $@"{root}\{sub}", Category = "Registro", Reason = "Configuración de la app" });
                else if (publisher != null && n == publisher)
                {
                    using var pk = k.OpenSubKey(sub);
                    var subs = pk?.GetSubKeyNames() ?? [];
                    var mine = subs.Where(s => names.Contains(Norm(s))).ToList();
                    foreach (var s in mine) Add(new() { Kind = LeftoverKind.RegKey, Target = $@"{root}\{sub}\{s}", Category = "Registro", Reason = $"Configuración en {CleanPublisher(p.Publisher)}" });
                    if (mine.Count > 0 && mine.Count == subs.Length && pk!.ValueCount == 0)
                        Add(new() { Kind = LeftoverKind.RegKey, Target = $@"{root}\{sub}", Category = "Registro", Reason = "Clave del fabricante que se quedaría vacía" });
                }
            }
        }

        // things that would keep trying to run a program that's gone
        var exeDir = p.InstallLocation != "" && !IsProtectedPath(p.InstallLocation) ? p.InstallLocation.TrimEnd('\\') + "\\" : null;
        bool Mine(string cmd) => exeDir != null && cmd.Contains(exeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        foreach (var run in new[] { @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM64\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM32\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" })
        {
            using var k = OpenKey(run);
            foreach (var v in k?.GetValueNames() ?? [])
                if (k!.GetValue(v) is string cmd && (Mine(cmd) || names.Contains(Norm(v))))
                    Add(new() { Kind = LeftoverKind.RegValue, Target = $"{run}|{v}", Category = "Inicio y tareas", Reason = "Arranque automático" });
        }
        if (exeDir != null)
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, PathName FROM Win32_Service");
                foreach (var o in s.Get().Cast<ManagementObject>())
                    if (o["PathName"] is string path && Mine(path) && !Services.IsProtected((string)o["Name"], ""))
                        Add(new() { Kind = LeftoverKind.Service, Target = (string)o["Name"], Category = "Inicio y tareas", Reason = "Servicio del programa" });
            }
            catch { }
            try
            {
                foreach (var t in ScheduledTasks.List().Where(t => t.CanChange && Mine(t.Action)))
                    Add(new() { Kind = LeftoverKind.Task, Target = t.Path, Category = "Inicio y tareas", Reason = "Tarea programada del programa" });
            }
            catch { }
        }

        // never offer a folder inside another offered folder
        var folders = found.Values.Where(l => l.Kind == LeftoverKind.Folder).Select(l => l.Target.TrimEnd('\\') + "\\").ToList();
        return found.Values.Where(l => l.Kind is not (LeftoverKind.Folder or LeftoverKind.File)
                                       || !folders.Any(f => l.Target.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                           .OrderBy(l => l.Category).ThenBy(l => l.Target).ToList();

        void AddDir(string dir, string cat, string reason)
        {
            // saved games and documents are personal: listed, but unticked by default
            if (!IsProtectedPath(dir) && !IsLink(dir))
                Add(new() { Kind = LeftoverKind.Folder, Target = dir, Category = cat, Reason = reason, Size = DirSize(dir), Selected = cat != "Partidas y documentos" });
        }
    }

    static IEnumerable<string> SafeDirs(string root, EnumerationOptions o)
    {
        try { return Directory.EnumerateDirectories(root, "*", o).ToList(); } catch { return []; }
    }

    static bool IsLink(string p) { try { return new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint); } catch { return true; } }

    public static long DirSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    static bool PointsInto(string lnk, InstalledProgram p)
    {
        if (p.InstallLocation == "" || IsProtectedPath(p.InstallLocation)) return false;
        var target = LinkTarget(lnk);
        return target != null && target.StartsWith(p.InstallLocation.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static string? LinkTarget(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            return (string)sh.CreateShortcut(lnk).TargetPath;
        }
        catch { return null; }
    }

    // ---------- registry helpers ----------
    static (RegistryKey Base, string Sub)? Split(string key)
    {
        var i = key.IndexOf('\\');
        if (i < 0) return null;
        RegistryKey? b = key[..i] switch
        {
            "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKLM64" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKLM32" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => null,
        };
        return b == null ? null : (b, key[(i + 1)..]);
    }

    static RegistryKey? OpenKey(string key, bool write = false) => Split(key) is { } s ? s.Base.OpenSubKey(s.Sub, write) : null;

    /// <summary>Only HKCU\Software\X…, HKLM\SOFTWARE\X… (not Microsoft/Classes/Policies...) or an Uninstall entry.</summary>
    public static bool IsAllowedKey(string key)
    {
        var parts = key.Split('\\');
        if (parts.Length < 3 || parts[0] is not ("HKCU" or "HKLM64" or "HKLM32") || !parts[1].Equals("Software", StringComparison.OrdinalIgnoreCase)) return false;
        if (key.Contains(@$"\{UninstallPath}\", StringComparison.OrdinalIgnoreCase) && parts.Length == 7) return true;
        return !Generic.Contains(Norm(parts[2])) && parts.All(p => p != "" && p != "..");
    }

    static string RegExe => Path.Combine(Environment.SystemDirectory, "reg.exe");
    static string RegPath(string key) => key.Replace("HKLM64", "HKLM").Replace("HKLM32", "HKLM");
    static string RegView(string key) => key.StartsWith("HKLM32") ? "/reg:32" : "/reg:64";

    // ---------- step 3: remove into quarantine ----------
    public static Quarantine Remove(string app, IEnumerable<Leftover> items, Action<string>? progress = null)
    {
        var q = new Quarantine { Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Regex.Replace(app, @"[^A-Za-z0-9]+", "-").Trim('-')}", App = app };
        Directory.CreateDirectory(q.Dir);
        var n = 0;
        foreach (var l in items)
        {
            progress?.Invoke(l.Display);
            var stored = Path.Combine(q.Dir, (++n).ToString());
            try
            {
                switch (l.Kind)
                {
                    case LeftoverKind.Folder or LeftoverKind.File:
                        if (IsProtectedPath(l.Target) || (l.Kind == LeftoverKind.Folder && IsLink(l.Target))) { Store.Log($"  BLOCKED (protected path): {l.Target}"); continue; }
                        MovePath(l.Target, stored);
                        q.Bytes += l.Size;
                        q.Items.Add(new(l.Kind, l.Target, stored));
                        break;
                    case LeftoverKind.RegKey:
                        if (!IsAllowedKey(l.Target)) { Store.Log($"  BLOCKED (protected key): {l.Target}"); continue; }
                        if (Store.Run(RegExe, $"export \"{RegPath(l.Target)}\" \"{stored}.reg\" /y {RegView(l.Target)}").Exit != 0) throw new IOException("no se pudo exportar");
                        var s = Split(l.Target)!.Value;
                        s.Base.DeleteSubKeyTree(s.Sub, false);
                        q.Items.Add(new(l.Kind, l.Target, stored + ".reg"));
                        break;
                    case LeftoverKind.RegValue:
                        var (key, name) = (l.Target.Split('|')[0], l.Target.Split('|')[1]);
                        using (var k = OpenKey(key, true))
                        {
                            if (k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string data) continue;
                            var kind = k.GetValueKind(name).ToString();
                            k.DeleteValue(name, false);
                            q.Items.Add(new(l.Kind, l.Target, "", data, kind));
                        }
                        break;
                    case LeftoverKind.Service:
                        if (Services.IsProtected(l.Target, "")) continue;
                        // no backup, no delete
                        if (Store.Run(RegExe, $"export \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{l.Target}\" \"{stored}.reg\" /y /reg:64").Exit != 0) throw new IOException("no se pudo exportar");
                        Store.Run("sc.exe", $"stop \"{l.Target}\"");
                        if (Store.Run("sc.exe", $"delete \"{l.Target}\"").Exit != 0) throw new IOException("sc delete falló");
                        q.Items.Add(new(l.Kind, l.Target, stored + ".reg"));
                        break;
                    case LeftoverKind.Task:
                        if (!ScheduledTasks.CanChange(l.Target)) continue;
                        if (ScheduledTasks.Xml(l.Target) is not { } xml || !xml.Contains("<Task")) throw new IOException("no se pudo leer la tarea");
                        File.WriteAllText(stored + ".xml", xml, System.Text.Encoding.Unicode);
                        if (Store.Run("schtasks.exe", $"/delete /tn \"{l.Target}\" /f").Exit != 0) throw new IOException("schtasks /delete falló");
                        q.Items.Add(new(l.Kind, l.Target, stored + ".xml"));
                        break;
                }
                Store.Log($"  quarantined: {l.Display}");
            }
            catch (Exception e) { Store.Log($"  could NOT remove {l.Display}: {e.Message}"); }
        }
        Store.WriteJson(Path.Combine("quarantine", q.Id, "manifest.json"), q, secure: true);
        Store.Log($"Deep uninstall of {app}: {q.Items.Count} items quarantined ({q.Dir})");
        History.Add(HistoryKind.Uninstall, $"Restos de {app} a la cuarentena", $"{q.Items.Count} elementos", $"quarantine:{q.Id}");
        return q;
    }

    static void MovePath(string from, string to)
    {
        var sameVolume = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        if (File.Exists(from))
        {
            File.Move(from, to);
            return;
        }
        if (sameVolume) { Directory.Move(from, to); return; }
        CopyTree(from, to);
        // the full copy is safe in quarantine; if something in the original is in use, it just stays where it was
        try { Cleaner.DeleteTree(from); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Store.Log($"  copied, but part of {from} was in use and stays: {e.Message}"); }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ---------- quarantine management ----------
    public static List<Quarantine> Quarantines()
    {
        if (!Directory.Exists(QuarantineRoot)) return [];
        return Directory.EnumerateDirectories(QuarantineRoot)
            .Select(d => Store.ReadJson<Quarantine>(Path.Combine("quarantine", Path.GetFileName(d), "manifest.json"), secure: true))
            .Where(q => q != null && Path.GetFileName(q.Dir) == q.Id).Cast<Quarantine>()
            .OrderByDescending(q => q.Created).ToList();
    }

    /// <summary>After the quarantine folder moved (old Spanish names), point each stored path at its new place.</summary>
    public static void RebaseQuarantines()
    {
        foreach (var q in Quarantines())
        {
            var marker = $"\\{q.Id}\\";
            for (var n = 0; n < q.Items.Count; n++)
                if (q.Items[n].Stored.IndexOf(marker, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
                    q.Items[n] = q.Items[n] with { Stored = Path.Combine(q.Dir, q.Items[n].Stored[(at + marker.Length)..]) };
            Store.WriteJson(Path.Combine("quarantine", q.Id, "manifest.json"), q, secure: true);
        }
    }

    /// <summary>Puts everything back where it was. Items whose original place is taken again are left in quarantine.</summary>
    public static (int Restored, int Failed) Restore(Quarantine q)
    {
        int ok = 0, failed = 0;
        foreach (var i in q.Items.AsEnumerable().Reverse())
        {
            try
            {
                // stored paths must live inside this quarantine (the manifest is admin-only, but double-check)
                if (i.Stored != "" && !Path.GetFullPath(i.Stored).StartsWith(q.Dir + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("ruta de cuarentena no válida");
                switch (i.Kind)
                {
                    case LeftoverKind.Folder or LeftoverKind.File:
                        if (File.Exists(i.Target) || Directory.Exists(i.Target)) throw new IOException("ya existe algo en esa ruta");
                        Directory.CreateDirectory(Path.GetDirectoryName(i.Target)!);
                        MovePath(i.Stored, i.Target);
                        break;
                    case LeftoverKind.RegKey or LeftoverKind.Service:
                        if (Store.Run(RegExe, $"import \"{i.Stored}\" {(i.Target.StartsWith("HKLM32") ? "/reg:32" : "/reg:64")}").Exit != 0) throw new IOException("reg import falló");
                        break;
                    case LeftoverKind.RegValue:
                        var (key, name) = (i.Target.Split('|')[0], i.Target.Split('|')[1]);
                        var s = Split(key)!.Value;
                        using (var k = s.Base.CreateSubKey(s.Sub, true))
                            k.SetValue(name, i.Data!, i.DataKind == nameof(RegistryValueKind.ExpandString) ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                        break;
                    case LeftoverKind.Task:
                        if (Store.Run("schtasks.exe", $"/create /tn \"{i.Target}\" /xml \"{i.Stored}\" /f").Exit != 0) throw new IOException("schtasks /create falló");
                        break;
                }
                ok++;
            }
            catch (Exception e) { failed++; Store.Log($"Restore {i.Target}: {e.Message}"); }
        }
        Store.Log($"Quarantine {q.Id} restored: {ok} ok, {failed} failed");
        // everything is back; an empty quarantine folder that can't go yet is harmless
        if (failed == 0) try { Cleaner.DeleteTree(q.Dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Store.Log($"Quarantine {q.Id}: folder stays: {e.Message}"); }
        return (ok, failed);
    }

    public static void Purge(Quarantine q)
    {
        if (!Path.GetFullPath(q.Dir).StartsWith(QuarantineRoot + "\\", StringComparison.OrdinalIgnoreCase)) return;
        Cleaner.DeleteTree(q.Dir);
        Store.Log($"Quarantine {q.Id} ({q.App}) permanently deleted");
    }
}
