using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.ServiceProcess;
using System.Xml.Linq;

namespace Tidy.Core;

public record BootTime(DateTime When, double Seconds);

public enum BootSlowKind { App, Driver, Service, Device }

/// <param name="File">The exe or driver file as Windows logged it (a service or device has its name here).</param>
/// <param name="Times">How many boots it delayed; AvgSec is the delay per boot.</param>
public record BootSlow(BootSlowKind Kind, string Name, string File, int Times, double AvgSec, DateTime Last);

public record Measurement(DateTime When, string Label, double? BootSec, double RamUsedGb, int Processes, int StartupApps, int ServicesRunning, double FreeCGb);

/// <summary>Before/after numbers: boot time from Windows' own log, plus a snapshot of how loaded the PC is.</summary>
public static class Benchmark
{
    const string FileName = "measurements.json";
    /// <summary>Why Boots() came back empty, if it failed.</summary>
    public static string? BootError { get; private set; }

    /// <summary>Recent boots, newest first. Windows logs each one (event 100) with its duration; it goes back before Tidy existed.</summary>
    public static List<BootTime> Boots(int max = 20)
    {
        var list = new List<BootTime>();
        BootError = null;
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            for (var e = reader.ReadEvent(); e != null && list.Count < max; e = reader.ReadEvent())
                using (e)
                {
                    XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                    var ms = XDocument.Parse(e.ToXml()).Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == "BootTime")?.Value;
                    if (e.TimeCreated is { } when && double.TryParse(ms, out var v) && v > 0) list.Add(new BootTime(when, v / 1000));
                }
        }
        catch (Exception e)
        {
            Store.Log($"Boot times: {e.Message}");
            var admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            BootError = admin ? $"No se pudo leer el registro de arranques de Windows: {e.Message}" : "Windows solo deja leer los tiempos de arranque con permisos de administrador.";
        }
        return list;
    }

    /// <summary>
    /// What made recent boots slower, worst first. Windows logs it itself when a boot is slow: event 101 an app, 102 a driver,
    /// 103 a service, 109 a device, each with how many ms it delayed that boot.
    /// </summary>
    public static List<BootSlow> Slowdowns(int days = 30)
    {
        var seen = new List<(BootSlowKind Kind, string Name, string File, double Sec, DateTime When)>();
        BootError = null;
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                $"*[System[(EventID=101 or EventID=102 or EventID=103 or EventID=109) and TimeCreated[timediff(@SystemTime) <= {days * 86_400_000L}]]]");
            using var reader = new EventLogReader(q);
            for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
                using (e)
                {
                    XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                    var data = XDocument.Parse(e.ToXml()).Descendants(ns + "Data")
                        .GroupBy(d => (string?)d.Attribute("Name") ?? "").ToDictionary(g => g.Key, g => g.First().Value.Trim());
                    var kind = e.Id switch { 101 => BootSlowKind.App, 102 => BootSlowKind.Driver, 103 => BootSlowKind.Service, _ => BootSlowKind.Device };
                    var file = data.GetValueOrDefault("FileName") is { Length: > 0 } f ? f : data.GetValueOrDefault("Name") ?? "";
                    var name = data.GetValueOrDefault("FriendlyName") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file);
                    if (name != "" && double.TryParse(data.GetValueOrDefault("DegradationTime"), out var ms) && ms > 0 && e.TimeCreated is { } when)
                        seen.Add((kind, name, file, ms / 1000, when));
                }
        }
        catch (Exception e)
        {
            Store.Log($"Boot slowdowns: {e.Message}");
            BootError = "Windows solo deja leer qué retrasa el arranque con permisos de administrador.";
        }
        return seen.GroupBy(s => (s.Kind, Key: (s.File != "" ? s.File : s.Name).ToLowerInvariant()))
            .Select(g => new BootSlow(g.Key.Kind, g.First().Name, g.First().File, g.Count(), g.Average(s => s.Sec), g.Max(s => s.When)))
            .OrderByDescending(b => b.Times * b.AvgSec).ToList();
    }

    public static Measurement Measure(string label)
    {
        var ram = SysMon.Ram();
        int procs = Process.GetProcesses().Length, startup = 0, services = 0;
        try { startup = StartupApps.List().Count(a => a.Enabled); } catch { }
        try { services = ServiceController.GetServices().Count(s => { using (s) return s.Status == ServiceControllerStatus.Running; }); } catch { }
        var c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var m = new Measurement(DateTime.Now, label, Boots(1).FirstOrDefault()?.Seconds, Math.Round(ram.UsedGb, 2), procs, startup, services, Math.Round(c.AvailableFreeSpace / (double)(1L << 30), 1));
        var all = All();
        all.Add(m);
        if (all.Count > 100) all.RemoveRange(1, all.Count - 100); // keep the very first one: it's the "before"
        Store.WriteJson(FileName, all);
        Store.Log($"Measurement '{label}': RAM {m.RamUsedGb} GB, {procs} processes, {startup} startup apps, {services} services, boot {m.BootSec?.ToString("0.0") ?? "?"} s");
        return m;
    }

    public static List<Measurement> All() => Store.ReadJson<List<Measurement>>(FileName) ?? [];
}
