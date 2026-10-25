using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tidy.Core;

/// <summary>
/// Automatic game mode: while a game from a known library (or the user's list) is open, switch to the Ultimate plan,
/// purge standby RAM and favour the game's priority. When the last game closes everything goes back as it was.
/// Nothing here is permanent: the plan to return to is saved in settings, priorities die with the processes.
/// </summary>
public static class GameMode
{
    public static event Action<string?>? Changed;
    public static string? Current { get; private set; }
    public static DateTime Since { get; private set; }

    static int _gamePid;
    static DateTime _lastTick = DateTime.MinValue, _rootsAt = DateTime.MinValue;
    static List<(string Launcher, string Root)> _roots = [];
    static readonly Dictionary<int, string?> _paths = [];
    static readonly Dictionary<int, ProcessPriorityClass> _lowered = [];

    /// <summary>Background apps that never need foreground speed while you play. Only lowered, never closed.</summary>
    static readonly string[] Background = ["OneDrive", "Dropbox", "GoogleDriveFS", "MEGAsync", "iCloudDrive", "ms-teams", "Teams", "Slack", "CCXProcess", "Creative Cloud"];
    static readonly Regex NotAGame = new(@"crash|report|launcher|helper|updater|setup|redist|unins|anticheat|easyanticheat|beservice|battleye|prereq|dxsetup|vc_?redist|ue\d?prereq|cefprocess|webhelper",
        RegexOptions.IgnoreCase);

    public static string BackgroundList => string.Join(", ", Background.Take(3)) + ", Teams…";

    /// <summary>Game folders from Steam, Epic, GOG, Ubisoft, EA, Riot and Xbox, found from their own config files.</summary>
    public static List<(string Launcher, string Root)> Libraries()
    {
        var list = new List<(string, string)>();
        void Add(string launcher, string? path) { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) list.Add((launcher, Path.GetFullPath(path).TrimEnd('\\') + "\\")); }
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            var vdf = steam == null ? null : Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (vdf != null && File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                    Add("Steam", Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common"));
        }
        catch { }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.item"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var p)) Add("Epic Games", p.GetString());
                }
        }
        catch { }
        try
        {
            using var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            foreach (var id in gog?.GetSubKeyNames() ?? []) { using var k = gog!.OpenSubKey(id); Add("GOG", k?.GetValue("path") as string); }
        }
        catch { }
        var ubi = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir", null) as string;
        if (ubi != null) Add("Ubisoft", Path.Combine(ubi, "games"));
        Add("EA", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"));
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            Add("Xbox", Path.Combine(d.RootDirectory.FullName, "XboxGames"));
            Add("Riot", Path.Combine(d.RootDirectory.FullName, "Riot Games"));
        }
        return list.Distinct().ToList();
    }

    /// <summary>Game name if this exe belongs to a game, else null.</summary>
    public static string? GameName(string path)
    {
        if (Store.Settings.GameExes.Contains(path, StringComparer.OrdinalIgnoreCase))
            return FileVersionInfo.GetVersionInfo(path).ProductName is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(path);
        if (NotAGame.IsMatch(Path.GetFileName(path))) return null;
        foreach (var (_, root) in _roots)
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[root.Length..];
                var i = rest.IndexOf('\\');
                return i > 0 ? rest[..i] : Path.GetFileNameWithoutExtension(path); // the game's own folder name
            }
        return null;
    }

    /// <summary>Called from the background monitor. Cheap: only new processes get their path looked up.</summary>
    public static void Tick()
    {
        var s = Store.Settings;
        if (Current == null && s.GameModePrevPlan is { } stale) RestorePlan(stale); // left over from a crash
        if (!s.GameModeAuto) { if (Current != null) Stop(); return; }
        if (DateTime.Now - _lastTick < TimeSpan.FromSeconds(4)) return;
        _lastTick = DateTime.Now;

        if (Current != null)
        {
            if (Alive(_gamePid)) return;
            if (FindGame() is { } next) { _gamePid = next.Pid; Current = next.Name; Changed?.Invoke(Current); return; } // switched games
            Stop();
            return;
        }
        if (FindGame() is { } g) Start(g.Pid, g.Name);
    }

    static (int Pid, string Name)? FindGame()
    {
        if (DateTime.Now - _rootsAt > TimeSpan.FromMinutes(10)) { _roots = Libraries(); _rootsAt = DateTime.Now; }
        var procs = Process.GetProcesses();
        try
        {
            var alive = procs.Select(p => p.Id).ToHashSet();
            foreach (var pid in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(pid);
            foreach (var p in procs)
            {
                if (p.SessionId == 0) continue;
                if (!_paths.TryGetValue(p.Id, out var path)) _paths[p.Id] = path = Native.ProcessPath(p.Id);
                if (path == null || GameName(path) is not { } name) continue;
                try { if (p.MainWindowHandle != IntPtr.Zero) return (p.Id, name); } catch { }
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }
        return null;
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static void Start(int pid, string name)
    {
        var s = Store.Settings;
        _gamePid = pid;
        Current = name;
        Since = DateTime.Now;
        var done = new List<string>();
        if (s.GameModePlan)
        {
            var active = PowerPlan.All().FirstOrDefault(p => p.Active);
            if (active != null && !PowerPlan.IsUltimate(active))
            {
                s.GameModePrevPlan = active.Id;
                Store.Save(); // saved before switching: a crash still gets the user's plan back
                if (PowerPlan.Activate(PowerPlan.Ultimate, record: false)) done.Add("plan Máximo rendimiento");
            }
        }
        if (s.GameModeRam && Memory.PurgeStandby($"modo juego: {name}") > 0) done.Add("RAM liberada");
        if (s.GameModePriority)
        {
            try { using var g = Process.GetProcessById(pid); if (g.PriorityClass == ProcessPriorityClass.Normal) g.PriorityClass = ProcessPriorityClass.AboveNormal; done.Add("prioridad al juego"); } catch { }
            foreach (var n in Background)
                foreach (var p in Process.GetProcessesByName(n))
                    using (p)
                        try { if (p.PriorityClass == ProcessPriorityClass.Normal) { _lowered[p.Id] = p.PriorityClass; p.PriorityClass = ProcessPriorityClass.BelowNormal; } }
                        catch { }
        }
        Store.Log($"Game mode: started ({name}): {string.Join(", ", done)}");
        Changed?.Invoke(Current);
    }

    public static void Stop()
    {
        var name = Current;
        foreach (var (pid, prio) in _lowered)
            try { using var p = Process.GetProcessById(pid); p.PriorityClass = prio; } catch { }
        _lowered.Clear();
        if (Store.Settings.GameModePrevPlan is { } plan) RestorePlan(plan);
        Current = null;
        _gamePid = 0;
        if (name != null)
        {
            var mins = (int)(DateTime.Now - Since).TotalMinutes;
            Store.Log($"Game mode: ended ({name}, {mins} min). Everything restored.");
            History.Add(HistoryKind.Game, $"Modo juego: {name}", $"{mins} min · al cerrar el juego todo volvió a como estaba");
            Changed?.Invoke(null);
        }
    }

    static void RestorePlan(Guid plan)
    {
        // only if we're still on the plan game mode set; if the user picked another one meanwhile, theirs wins
        var active = PowerPlan.All().FirstOrDefault(p => p.Active);
        if (active == null || PowerPlan.IsUltimate(active)) PowerPlan.Activate(plan, record: false);
        Store.Settings.GameModePrevPlan = null;
        Store.Save();
    }
}
