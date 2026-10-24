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

    /// <summary>0 = off; otherwise remind (never clean) when this many days passed since the last cleaning.</summary>
    public int CleanReminderDays { get; set; } = 30;
    public DateTime? LastCleanReminder { get; set; }
}

/// <summary>Settings, audit log and the single place for paths. Everything lives in %LocalAppData%\Tidy.</summary>
public static class Store
{
    public static string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tidy");
    public static string LogPath => Path.Combine(Dir, "tidy.log");
    /// <summary>Admin-only folder (under Program Files) for backups that an elevated Tidy later acts on: services, tasks, DNS, quarantine.
    /// A normal-user process can't tamper with them there.</summary>
    public static string SecureDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tidy", "datos");
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
        catch (Exception e) { Log($"settings.json ilegible, se usan valores por defecto: {e.Message}"); }
    }

    public static void Save()
    {
        lock (_lock) WriteAtomic(SettingsPath, JsonSerializer.Serialize(Settings, _json));
    }

    public static T? ReadJson<T>(string name, bool secure = false) where T : class
    {
        var p = Path.Combine(secure ? SecureDir : Dir, name);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p)) : null; }
        catch (Exception e) { Log($"{name} ilegible: {e.Message}"); return null; }
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

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}
