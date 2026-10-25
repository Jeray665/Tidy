using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Tidy.Core;

/// <summary>A program running with no window, its helper processes added in.</summary>
/// <param name="Exes">Every distinct exe name in the group, so the list shows exactly what would be closed.</param>
public record BackgroundApp(string Name, string Path, string Root, string Exes, int[] Pids, double Cpu, long Memory, StartupApp? Startup, int Level);

/// <summary>
/// What keeps running with no window (tray apps, launchers, overlays) and how much it costs, measured over a few seconds.
/// Only programs the startup presets would also touch are listed: security, drivers, hardware, audio and VPNs never are.
/// </summary>
public static class Background
{
    record Sample(int Pid, string Path, TimeSpan Cpu, long Memory, bool Window);

    public static List<BackgroundApp> Analyze(TimeSpan span)
    {
        var before = Snapshot().ToDictionary(s => s.Pid);
        Thread.Sleep(span);
        var now = Snapshot();
        var cores = Environment.ProcessorCount;
        double Cpu(Sample s) => before.TryGetValue(s.Pid, out var b) && b.Path == s.Path ? Math.Max(0, (s.Cpu - b.Cpu).TotalMilliseconds / span.TotalMilliseconds / cores * 100) : 0;

        var startup = StartupApps.List().Where(a => a.Enabled).ToList();
        var list = new List<BackgroundApp>();
        foreach (var g in now.GroupBy(s => AppRoot(s.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (g.Any(s => s.Window)) continue; // it has a window open: you're using it
            var main = g.OrderBy(s => s.Path.Length).ThenByDescending(s => s.Memory).First().Path;
            var info = Info(main);
            if (StartupPresets.Level($"{info.Name} {main} {info.Company}") is not { } level) continue;
            double cpu = g.Sum(Cpu); long mem = g.Sum(s => s.Memory);
            if (cpu < 0.1 && mem < 150L << 20) continue; // costs next to nothing
            var paths = g.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entry = startup.FirstOrDefault(a => a.ExePath is { } e && (paths.Contains(e) || AppRoot(e).Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                                                     || paths.Any(p => a.Command.Contains(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)));
            var exes = string.Join(", ", g.Select(s => Path.GetFileName(s.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
            list.Add(new(info.Name, main, g.Key, exes, g.Select(s => s.Pid).ToArray(), cpu, mem, entry, level));
        }
        return list.OrderByDescending(a => a.Cpu).ThenByDescending(a => a.Memory).ToList();
    }

    /// <summary>
    /// The program's own folder: the topmost one below a folder that holds many programs (Program Files, AppData\Local, C:\Tools…).
    /// Steam\bin\cef joins Steam and Medal\recorder joins Medal, but two apps never end up together.
    /// </summary>
    internal static string AppRoot(string exe)
    {
        var root = Path.GetDirectoryName(exe)!;
        for (var up = Path.GetDirectoryName(root); up != null && !Shared(up); up = Path.GetDirectoryName(root)) root = up;
        return root;
    }

    static bool Shared(string dir) =>
        dir.Count(c => c == '\\') < 2 || Uninstaller.IsProtectedPath(dir)
        || Path.GetFileName(dir).ToLowerInvariant() is "windowsapps" or "programs" or "temp" or "locallow" or "packages";
    /// <summary>Shared runtimes other apps run inside (WebView2 hosts widgets, search, Outlook…) and interpreters running your own scripts: closing them breaks something else.</summary>
    internal static readonly Regex Runtimes = new(@"\\(EdgeWebView|dotnet|Java|Common Files|nodejs|Python\d*|PowerShell)\\|\\(msedgewebview2|node|python\w*|javaw?|pwsh|VBCSCompiler)\.exe$",
        RegexOptions.IgnoreCase);

    static List<Sample> Snapshot()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var session = Process.GetCurrentProcess().SessionId;
        var list = new List<Sample>();
        foreach (var p in Process.GetProcesses())
            using (p)
                try
                {
                    if (p.Id == Environment.ProcessId || p.SessionId != session || Processes.IsCritical(p)) continue;
                    var path = Native.ProcessPath(p.Id);
                    if (path == null || path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || Runtimes.IsMatch(path)) continue; // Windows' own
                    list.Add(new(p.Id, path, p.TotalProcessorTime, p.PrivateMemorySize64, p.MainWindowHandle != IntPtr.Zero));
                }
                catch { /* exited or not readable */ }
        return list;
    }

    static (string Name, string Company) Info(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return (v.ProductName is { Length: > 0 } n ? n : v.FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(path), v.CompanyName?.Trim() ?? "");
        }
        catch { return (Path.GetFileNameWithoutExtension(path), ""); }
    }

    /// <summary>Closes only the processes measured, and only if each pid is still that same exe (ids get reused).</summary>
    public static (bool Ok, string Message) Close(BackgroundApp app)
    {
        int n = 0; var err = "";
        foreach (var pid in app.Pids)
            try
            {
                using var p = Process.GetProcessById(pid);
                if (Native.ProcessPath(pid) is not { } path || !AppRoot(path).Equals(app.Root, StringComparison.OrdinalIgnoreCase) || Processes.IsCritical(p)) continue;
                p.Kill(); n++;
            }
            catch (ArgumentException) { } // already gone
            catch (Exception e) { err = e.Message; }
        Store.Log($"Background: closed {app.Name} ({n} processes){(err != "" ? $", error: {err}" : "")}");
        return (err == "", err);
    }
}
