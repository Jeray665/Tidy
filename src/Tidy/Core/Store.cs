using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Tidy.Core;

public class Settings
{
    public bool AutoRam { get; set; } = true;
    /// <summary>Purge standby only when truly free memory (zero+free lists) drops below this.</summary>
    public int FreeThresholdMb { get; set; } = 1024;
    /// <summary>...and only if there's at least this much standby cache to reclaim.</summary>
    public int MinStandbyMb { get; set; } = 1024;
    public DateTime? LastClean { get; set; }
    public long LastCleanBytes { get; set; }
    public DateTime? LastDriverScan { get; set; }
    public int LastDriverUpdates { get; set; }
    public bool AutostartDismissed { get; set; }

    public bool GameModeAuto { get; set; }
    public bool GameModePlan { get; set; } = true;
    public bool GameModeRam { get; set; } = true;
    public bool GameModePriority { get; set; } = true;
    public List<string> GameExes { get; set; } = [];
    /// <summary>Plan to go back to when the game closes. Saved so a crash mid-game still restores it on the next start.</summary>
    public Guid? GameModePrevPlan { get; set; }

    /// <summary>0 = off; otherwise when this many days passed since the last cleaning, remind (or clean by itself if AutoClean).</summary>
    public int CleanReminderDays { get; set; } = 30;
    /// <summary>Clean on schedule without asking: only the chosen categories, never the recycle bin. Off = only remind.</summary>
    public bool AutoClean { get; set; }
    /// <summary>Cleanup category ids the schedule covers. Null = the ones ticked by default on the Cleanup page.</summary>
    public List<string>? CleanCategories { get; set; }
    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>"es", "en" or "" for the same as Windows.</summary>
    public string Language { get; set; } = "";
    /// <summary>Screen off / sleep / hibernate in seconds (0 = never), re-applied when Tidy changes the power plan.
    /// Only the ones the user chose in Tidy are here.</summary>
    public Dictionary<string, int> PowerTimeouts { get; set; } = [];

    public bool AutoUpdateCheck { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The version whose "new version" banner the user closed; a newer one shows it again.</summary>
    public string? DismissedUpdate { get; set; }
    /// <summary>Version whose "what's new" was already shown. Null on a fresh install.</summary>
    public string? LastSeenVersion { get; set; }
    public string? LastNotifiedVersion { get; set; }
    public DateTime? LastCleanReminder { get; set; }
}

/// <summary>Settings, audit log and the single place for paths. Everything lives in %LocalAppData%\Tidy.</summary>
public static class Store
{
    public static string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tidy");
    public static string LogPath => Path.Combine(Dir, "tidy.log");
    /// <summary>Admin-only folder (under Program Files) for backups that an elevated Tidy later acts on: services, tasks, DNS, quarantine.
    /// A normal-user process can't tamper with them there.</summary>
    public static string SecureDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tidy", "data");
    public static string CleanupsDir => Path.Combine(Dir, "cleanups");
    /// <summary>Driver installers and updates: admin-only, so nothing can swap them between the check and the launch.</summary>
    public static string DownloadsDir => Path.Combine(SecureDir, "downloads");
    /// <summary>Where each "Mover" in Almacenamiento records what went where, so it can be put back.</summary>
    public static string MovesDir => Path.Combine(SecureDir, "moves");
    static string SettingsPath => Path.Combine(Dir, "settings.json");
    static readonly object _lock = new();
    static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static Settings Settings { get; private set; } = new();
    public static bool FirstRun { get; private set; }

    public static void Load()
    {
        Directory.CreateDirectory(Dir);
        FirstRun = !File.Exists(SettingsPath);
        try { if (!FirstRun) Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) { Log($"settings.json unreadable, using defaults: {e.Message}"); }
    }

    /// <summary>1.0.0 used Spanish folder names; move them once to the English ones. Nothing is deleted, and a
    /// folder that exists under both names is merged without overwriting anything.</summary>
    public static void MigrateFolders()
    {
        var root = Path.GetDirectoryName(SecureDir)!;   // Program Files\Tidy
        MoveDir(Path.Combine(Dir, "limpiezas"), CleanupsDir);
        MoveDir(Path.Combine(root, "datos"), SecureDir);
        MoveDir(Path.Combine(root, "descargas"), DownloadsDir);
        if (MoveDir(Path.Combine(SecureDir, "cuarentena"), Uninstaller.QuarantineRoot)) Uninstaller.RebaseQuarantines();
    }

    /// <summary>Installers Tidy downloaded itself (NVIDIA ~900 MB each, its own updates) are only needed for a few days.</summary>
    public static void CleanDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadsDir)) return;
            foreach (var f in new DirectoryInfo(DownloadsDir).GetFiles().Where(f => f.LastWriteTime < DateTime.Now.AddDays(-7)))
                try { f.Delete(); Log($"Old download removed: {f.Name}"); } catch (IOException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static bool MoveDir(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return false;
            if (!Directory.Exists(to)) Directory.Move(from, to);
            else
            {
                foreach (var e in Directory.EnumerateFileSystemEntries(from))
                {
                    var dest = Path.Combine(to, Path.GetFileName(e));
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    if (Directory.Exists(e)) Directory.Move(e, dest); else File.Move(e, dest);
                }
                if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
            }
            Log($"Migrated folder {from} -> {to}");
            return true;
        }
        catch (Exception e) { Log($"Folder migration {from} -> {to} failed: {e.Message}"); return false; }
    }

    public static void Save()
    {
        lock (_lock) WriteAtomic(SettingsPath, JsonSerializer.Serialize(Settings, _json));
    }

    public static T? ReadJson<T>(string name, bool secure = false) where T : class
    {
        var p = Path.Combine(secure ? SecureDir : Dir, name);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p)) : null; }
        catch (Exception e) { Log($"{name} unreadable: {e.Message}"); return null; }
    }

    public static void WriteJson<T>(string name, T value, bool secure = false)
    {
        lock (_lock) WriteAtomic(Path.Combine(secure ? SecureDir : Dir, name), JsonSerializer.Serialize(value, _json));
    }

    // write-then-rename so a crash never leaves a half-written backup/settings file
    static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, Encoding.UTF8);
        File.Move(tmp, path, true);
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000)
                    File.Move(LogPath, Path.Combine(Dir, "tidy.old.log"), true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* logging must never crash the app */ }
    }

    /// <summary>Run a System32 tool by absolute path (no PATH lookup) and capture its output.</summary>
    public static (int Exit, string Output) Run(string exe, string args, int timeoutMs = 120_000)
    {
        var full = Path.IsPathRooted(exe) ? exe : Path.Combine(Environment.SystemDirectory, exe);
        var psi = new ProcessStartInfo(full, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Like Run, but as the signed-in user without admin rights (some installers refuse to run elevated).
    /// cmd only redirects the output to a temp file; /d skips any cmd AutoRun.</summary>
    public static (int Exit, string Output) RunUnelevated(string exe, string args, int timeoutMs = 120_000)
    {
        var outFile = Path.Combine(Path.GetTempPath(), "tidy-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var (code, error) = Native.RunAsShellUser(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{exe}\" {args} > \"{outFile}\" 2>&1\"", timeoutMs);
            if (code == null) return (-1, $"No se pudo abrir sin permisos de administrador: {error}.");
            return (code.Value, File.Exists(outFile) ? File.ReadAllText(outFile, Encoding.UTF8) : "");
        }
        finally { try { File.Delete(outFile); } catch { } }
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}
