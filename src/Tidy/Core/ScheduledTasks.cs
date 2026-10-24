namespace Tidy.Core;

public record TaskInfo(string Path, string Name, string Folder, bool Enabled, string Author, string Description, string Action,
                       DateTime? LastRun, DateTime? NextRun, bool CanChange, string? Hint, bool Changed);

/// <summary>Task Scheduler via its COM API. Only enable/disable, always reversible (original state saved).</summary>
public static class ScheduledTasks
{
    const string BackupFile = "tasks-backup.json";

    public static readonly string[] TelemetryTasks =
    [
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Autochk\Proxy",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
    ];

    /// <summary>Windows tasks that may be touched besides the telemetry ones; everything else under \Microsoft\Windows is read-only.</summary>
    static readonly Dictionary<string, string> WindowsOptional = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"\Microsoft\Windows\Maps\MapsUpdateTask"] = "Actualiza mapas sin conexión.",
        [@"\Microsoft\Windows\Maps\MapsToastTask"] = "Avisos de la app Mapas.",
        [@"\Microsoft\Windows\Windows Media Sharing\UpdateLibrary"] = "Comparte la biblioteca multimedia en red.",
        [@"\Microsoft\Windows\XblGameSave\XblGameSaveTask"] = "Sincroniza partidas de Xbox. Necesaria para Game Pass.",
        [@"\Microsoft\Windows\Shell\FamilySafetyMonitor"] = "Control parental.",
        [@"\Microsoft\Windows\Shell\FamilySafetyRefreshTask"] = "Control parental.",
        [@"\Microsoft\Windows\Application Experience\StartupAppTask"] = "Revisa qué apps arrancan con Windows.",
        [@"\Microsoft\Windows\Application Experience\PcaPatchDbTask"] = "Base de datos de compatibilidad.",
        [@"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask"] = "Programa de mejora (telemetría).",
        [@"\Microsoft\Windows\Device Information\Device"] = "Envía información del dispositivo (telemetría).",
        [@"\Microsoft\Windows\Device Information\Device User"] = "Envía información del dispositivo (telemetría).",
    };

    static dynamic Service()
    {
        dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        s.Connect();
        return s;
    }

    static dynamic? Get(string path)
    {
        try
        {
            var i = path.LastIndexOf('\\');
            return Service().GetFolder(i <= 0 ? "\\" : path[..i]).GetTask(path[(i + 1)..]);
        }
        catch { return null; }
    }

    public static bool Exists(string path) => Get(path) != null;
    public static bool IsEnabled(string path) => Get(path) is { } t && (bool)t.Enabled;

    public static bool CanChange(string path) =>
        !path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase)
        || TelemetryTasks.Contains(path, StringComparer.OrdinalIgnoreCase) || WindowsOptional.ContainsKey(path);

    static string? Hint(string path) =>
        TelemetryTasks.Contains(path, StringComparer.OrdinalIgnoreCase) ? "Telemetría: se puede desactivar." : WindowsOptional.GetValueOrDefault(path);

    public static List<TaskInfo> List()
    {
        var backup = LoadBackup();
        var list = new List<TaskInfo>();
        void Walk(dynamic folder)
        {
            foreach (dynamic t in folder.GetTasks(1)) // 1 = include hidden
            {
                try
                {
                    string path = t.Path;
                    dynamic def = t.Definition;
                    var actions = new List<string>();
                    foreach (dynamic a in def.Actions)
                        actions.Add((int)a.Type == 0 ? $"{a.Path} {a.Arguments}".Trim() : "(acción COM / correo)");
                    DateTime last = t.LastRunTime, next = t.NextRunTime;
                    list.Add(new TaskInfo(path, (string)t.Name, (string)folder.Path, (bool)t.Enabled,
                        def.RegistrationInfo.Author as string ?? "", def.RegistrationInfo.Description as string ?? "", string.Join(" · ", actions),
                        last.Year > 2000 ? last : null, next.Year > 2000 ? next : null, CanChange(path), Hint(path), backup.ContainsKey(path)));
                }
                catch { /* tasks we can't read are simply not listed */ }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub);
        }
        Walk(Service().GetFolder("\\"));
        return list.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static (bool Ok, string Message) SetEnabled(string path, bool enabled)
    {
        if (!CanChange(path)) return (false, "Tarea protegida de Windows.");
        var t = Get(path);
        if (t == null) return (false, "La tarea no existe.");
        var backup = LoadBackup();
        if (!backup.ContainsKey(path)) { backup[path] = (bool)t.Enabled; Store.WriteJson(BackupFile, backup, secure: true); }
        try
        {
            t.Enabled = enabled;
            Store.Log($"Tarea {path}: {(enabled ? "activada" : "desactivada")}");
            History.Add(HistoryKind.Task, $"Tarea {(enabled ? "activada" : "desactivada")}: {path[(path.LastIndexOf('\\') + 1)..]}", path, $"task:{path}");
            return (true, "");
        }
        catch (Exception e) { Store.Log($"Tarea {path}: cambio falló: {e.Message}"); return (false, e.Message); }
    }

    public static Dictionary<string, bool> LoadBackup() => Store.ReadJson<Dictionary<string, bool>>(BackupFile, secure: true) ?? new(StringComparer.OrdinalIgnoreCase);

    public static bool RestoreOriginal(string path)
    {
        var backup = LoadBackup();
        if (!backup.TryGetValue(path, out var original)) return false;
        var ok = !Exists(path) || SetEnabled(path, original).Ok;
        if (ok) { backup = LoadBackup(); backup.Remove(path); Store.WriteJson(BackupFile, backup, secure: true); }
        return ok;
    }

    public static int RestoreAll() => LoadBackup().Keys.ToList().Count(RestoreOriginal);
}
