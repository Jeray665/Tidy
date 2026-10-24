using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.ServiceProcess;
using System.Xml.Linq;

namespace Tidy.Core;

public record BootTime(DateTime When, double Seconds);

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
            Store.Log($"Tiempos de arranque: {e.Message}");
            var admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            BootError = admin ? $"No se pudo leer el registro de arranques de Windows: {e.Message}" : "Windows solo deja leer los tiempos de arranque con permisos de administrador.";
        }
        return list;
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
        Store.Log($"Medición '{label}': RAM {m.RamUsedGb} GB, {procs} procesos, {startup} apps al inicio, {services} servicios, arranque {m.BootSec?.ToString("0.0") ?? "?"} s");
        return m;
    }

    public static List<Measurement> All() => Store.ReadJson<List<Measurement>>(FileName) ?? [];
}
