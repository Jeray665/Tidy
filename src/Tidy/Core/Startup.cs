using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tidy.Core;

/// <summary>Tidy's own autostart: copied to Program Files (admin-only writable) and started elevated by a logon task.</summary>
public static class Autostart
{
    const string TaskName = "Tidy";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tidy");
    public static string InstalledExe => Path.Combine(InstallDir, "Tidy.exe");

    public static bool IsEnabled => Store.Run("schtasks.exe", $"/query /tn \"{TaskName}\"").Exit == 0;

    public static bool Enable()
    {
        // An elevated logon task must never point at a user-writable exe (anything could replace it and get admin silently),
        // so it only ever starts the installed copy in Program Files. Portable Tidy has no autostart.
        if (Install.IsPortable || !File.Exists(InstalledExe)) { Store.Log("Autostart needs the installed version"); return false; }
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Tidy: optimizador en la bandeja del sistema</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(InstalledExe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        // admin-only folder: a user-level process must not be able to swap the XML before schtasks reads it
        var tmp = Path.Combine(InstallDir, "task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        var (code, output) = Store.Run("schtasks.exe", $"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f");
        File.Delete(tmp);
        Store.Log($"Autostart enabled ({InstalledExe}) -> {code} {output.Trim()}");
        return code == 0;
    }

    public static bool Disable()
    {
        var (code, output) = Store.Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f");
        Store.Log($"Autostart disabled -> {code} {output.Trim()}");
        return code == 0;
    }
}

public class StartupApp
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public string Publisher { get; init; } = "";
    public string? ExePath { get; init; }
    public bool Enabled { get; set; }
    public required RegistryKey ApprovedHive { get; init; }
    public required string ApprovedKey { get; init; }
    public required string ApprovedName { get; init; }
}

/// <summary>
/// Same mechanism as Task Manager's "Startup apps" tab: we only flip the StartupApproved flag,
/// never delete the Run entry or the shortcut, so anything can be re-enabled later from here or Task Manager.
/// </summary>
public static class StartupApps
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> List()
    {
        var list = new List<StartupApp>();
        var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        AddRun(list, hkcu, hkcu, "Run", "Usuario");
        AddRun(list, hklm, hklm, "Run", "Todos los usuarios");
        AddRun(list, hklm32, hklm, "Run32", "Todos los usuarios (32 bits)");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), hkcu, "Carpeta Inicio");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), hklm, "Carpeta Inicio (todos)");
        return list.OrderByDescending(a => a.Enabled).ThenBy(a => a.Name).ToList();
    }

    static void AddRun(List<StartupApp> list, RegistryKey runHive, RegistryKey approvedHive, string approvedKey, string where)
    {
        using var run = runHive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (run == null) return;
        foreach (var name in run.GetValueNames())
        {
            if (run.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            var exe = ExeFromCommand(cmd);
            list.Add(new StartupApp
            {
                Name = Pretty(name, exe), Command = cmd, Location = where, ExePath = exe, Publisher = Publisher(exe),
                Enabled = IsApproved(approvedHive, approvedKey, name),
                ApprovedHive = approvedHive, ApprovedKey = approvedKey, ApprovedName = name,
            });
        }
    }

    static void AddFolder(List<StartupApp> list, string folder, RegistryKey approvedHive, string where)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(f);
            list.Add(new StartupApp
            {
                Name = Path.GetFileNameWithoutExtension(f), Command = f, Location = where, ExePath = f,
                Enabled = IsApproved(approvedHive, "StartupFolder", name),
                ApprovedHive = approvedHive, ApprovedKey = "StartupFolder", ApprovedName = name,
            });
        }
    }

    static bool IsApproved(RegistryKey hive, string key, string name)
    {
        using var k = hive.OpenSubKey(Approved + key);
        // missing = enabled; first byte even (02/06) = enabled, odd (03) = disabled
        return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public static void SetEnabled(StartupApp app, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        var was = app.Enabled;
        using var k = app.ApprovedHive.CreateSubKey(Approved + app.ApprovedKey, true);
        k.SetValue(app.ApprovedName, data, RegistryValueKind.Binary);
        app.Enabled = enabled;
        Store.Log($"Windows startup: {(enabled ? "enabled" : "disabled")} '{app.Name}' ({app.Command})");
        History.Add(HistoryKind.Startup, $"Inicio de Windows: {app.Name} {(enabled ? "activado" : "desactivado")}", "", $"startup:{Key(app)}|{(was ? 1 : 0)}");
    }

    /// <summary>Identifies an entry across restarts: hive, StartupApproved subkey and value name.</summary>
    public static string Key(StartupApp a) => $"{(a.ApprovedHive.Name.Contains("LOCAL_MACHINE") ? "HKLM" : "HKCU")}|{a.ApprovedKey}|{a.ApprovedName}";

    /// <summary>Undo from History: "HKCU|Run|name|1" puts that entry back to enabled (1) or disabled (0).</summary>
    public static bool Restore(string arg)
    {
        var cut = arg.LastIndexOf('|');
        if (cut < 0 || arg[(cut + 1)..] is not ("0" or "1")) return false;
        var app = List().FirstOrDefault(a => Key(a) == arg[..cut]);
        if (app == null) return false;
        SetEnabled(app, arg[^1] == '1');
        return true;
    }

    public static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"')) { var end = cmd.IndexOf('"', 1); return end > 1 ? cmd[1..end] : null; }
        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
    }

    static string Publisher(string? exe)
    {
        try { return exe != null && File.Exists(exe) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "" : ""; }
        catch { return ""; }
    }

    static string Pretty(string valueName, string? exe)
    {
        try
        {
            var desc = exe != null && File.Exists(exe) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription : null;
            return string.IsNullOrWhiteSpace(desc) ? valueName : desc.Trim();
        }
        catch { return valueName; }
    }
}

/// <summary>Info: shown for context with the reason Tidy leaves it alone; never ticked or applied.</summary>
public enum PresetKind { App, Service, Task, Process, Info }

/// <summary>One thing a startup preset would change. Level 1 = Ligero, 2 = Equilibrado, 3 = Máximo.</summary>
public record PresetItem(PresetKind Kind, string Name, string Detail, string Why, int Level, Func<(bool Ok, string Message)> Apply, bool On = true);

/// <summary>
/// Windows' own services and tasks that are on for nothing on most PCs, plus the third-party telemetry the startup presets find.
/// Only what's on right now is offered. Never here: search, printing, Bluetooth, Wi-Fi, Xbox/Game Pass, location, Hello, updates.
/// </summary>
public static class Unneeded
{
    public static readonly (string Name, StartMode Mode, bool On, string Why)[] WindowsServices =
    [
        ("DiagTrack", StartMode.Disabled, true, "Telemetría: envía datos de uso a Microsoft. Nada depende de él."),
        ("dmwappushservice", StartMode.Disabled, true, "Reparte los mensajes de telemetría. Nada depende de él."),
        ("RetailDemo", StartMode.Disabled, true, "Modo demo de las tiendas. No sirve en un PC de casa."),
        ("RemoteRegistry", StartMode.Disabled, true, "Deja editar tu registro desde otro PC. Apagado es más seguro."),
        ("NvTelemetryContainer", StartMode.Disabled, true, "Telemetría de NVIDIA. Los drivers y los juegos van igual."),
        ("MapsBroker", StartMode.Manual, true, "Descarga mapas sin conexión. La app Mapas lo arranca si la usas."),
        ("WMPNetworkSvc", StartMode.Manual, true, "Comparte la biblioteca de Windows Media en la red."),
        ("TrkWks", StartMode.Manual, true, "Sigue accesos directos a archivos movidos por redes de empresa."),
        ("PcaSvc", StartMode.Manual, true, "Asistente de compatibilidad para programas antiguos. Arranca solo si hace falta."),
        ("WerSvc", StartMode.Manual, true, "Informes de errores. Arranca solo cuando algo falla."),
        ("Fax", StartMode.Manual, true, "Fax."),
        ("wisvc", StartMode.Manual, true, "Programa Windows Insider. Arranca solo cuando Windows lo necesita."),
        ("diagnosticshub.standardcollector.service", StartMode.Manual, true, "Diagnóstico para programadores (Visual Studio)."),
        ("SysMain", StartMode.Manual, false, "Precarga en RAM las apps que más usas. Con SSD apenas se nota; sin él, algunas apps tardan un poco más la primera vez."),
    ];

    // needed for Game Pass even though it's in Tidy's optional list
    const string GamePassTask = @"\Microsoft\Windows\XblGameSave\XblGameSaveTask";

    public static List<PresetItem> Candidates()
    {
        var list = new List<PresetItem>();
        var on = Services.List().Where(s => !s.Protected && s.StartMode.StartsWith("Automático")).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, mode, check, why) in WindowsServices)
            if (on.TryGetValue(name, out var s))
                list.Add(new(PresetKind.Service, s.Display, mode == StartMode.Disabled ? "Servicio de Windows · se deshabilita" : "Servicio de Windows · pasa a manual", why,
                    1, () => Services.SetMode(name, mode), check));
        foreach (var t in ScheduledTasks.List().Where(t => t.Enabled && t.CanChange && t.Hint != null && !t.Path.Equals(GamePassTask, StringComparison.OrdinalIgnoreCase)))
            list.Add(new(PresetKind.Task, t.Name, $"Tarea de Windows · {t.Folder}", t.Hint!, 1, () => ScheduledTasks.SetEnabled(t.Path, false)));
        // third-party services and tasks the startup presets would turn off (their startup apps stay on the startup page)
        list.AddRange(StartupPresets.Candidates().Where(i => i.Kind != PresetKind.App));
        return list;
    }
}

/// <summary>
/// Third-party things that start with Windows and only cost performance, grouped by how far to go. Everything goes through
/// the normal reversible paths (StartupApproved flag, service start type with backup, task enable flag with backup), so each
/// change lands in History with its undo. Services only ever go to Manual, never Disabled.
/// </summary>
public static class StartupPresets
{
    public static readonly string[] Names = ["Ligero", "Equilibrado", "Máximo"];
    public static readonly string[] Descriptions =
    [
        "Solo telemetría e informes de errores de otros programas. No hacen falta para que nada funcione.",
        "Además, lanzadores de juegos y apps de chat, música o navegador: se abren igual cuando los necesitas, pero no al encender.",
        "Además, el resto de apps de terceros que arrancan solas y algunos servicios conocidos que no hacen falta al encender (pasan a manual).",
    ];

    // never offered at any level: security patches, protection, licences, VPN and remote access, servers, backups, passwords
    static readonly Regex Protect = new(@"update|updater|upgrade|patch|defender|security|antivirus|anti-?virus|malware|avast|\bavg\b|kaspersky|eset|bitdefender|norton|mcafee|sophos|firewall|vpn|wireguard|cloudflare|warp|tailscale|zerotier|zscaler|globalprotect|forticlient|proxy|license|licencia|flexnet|sentinel|hasp|codemeter|teamviewer|anydesk|rustdesk|parsec|remote|backup|veeam|acronis|sql|database|mysql|postgres|apache|nginx|server|docker|vmware|virtualbox|wsl|password|bitwarden|1password|keepass|lastpass|dashlane|yubi|authenticator|claude|anthropic|tidy", RegexOptions.IgnoreCase);
    static readonly Regex Telemetry = new(@"telemetry|crash|reporter|diagnostic|feedback|ceip|analytics|metrics", RegexOptions.IgnoreCase);
    // hardware and things people expect to work right after logging in
    static readonly Regex Hardware = new(@"audio|sound|realtek|nahimic|dolby|sonic|goxlr|elgato|stream ?deck|wave ?link|voicemeeter|equalizer|logi|razer|corsair|icue|steelseries|hyperx|asus|armoury|msi|gigabyte|nzxt|rgb|fan|cooler|hardware|afterburner|rivatuner|rtss|hwinfo|synaptics|elan|touchpad|keyboard|mouse|wacom|tobii|controller|xbox|ds4|dualsense|8bitdo|oculus|steamvr|vive|webcam|camera|bluetooth|nvidia|geforce|\bamd\b|radeon|intel|onedrive|dropbox|google ?drive|icloud|nextcloud|\bime\b|input|print|scanner|epson|canon|brother|\bhp\b", RegexOptions.IgnoreCase);
    // third-party services that are safe on Manual: they start on demand when their program needs them
    static readonly Regex IdleServices = new(@"bonjour|mdnsresponder|wondershare|iobit|driver ?booster|drivereasy|ccleaner|glary|wise ?care|advanced ?systemcare|adobe ?genuine|agsservice", RegexOptions.IgnoreCase);
    static readonly Regex Launchers = new(@"steam|epic|eadesktop|\bea app\b|origin|ubisoft|uplay|battle\.net|gog|riot|discord|spotify|teams|skype|zoom|whatsapp|telegram|slack|messenger|opera|brave|vivaldi|\bedge\b|msedge|chrome|firefox|cortana|itunes|helper", RegexOptions.IgnoreCase);

    static readonly Regex Squirrel = new(@"\S*Update\.exe""?\s+--processStart", RegexOptions.IgnoreCase);

    /// <summary>
    /// What delayed the boot, as a review list: an app that starts with Windows and the presets would turn off comes ticked;
    /// everything else (drivers, services, devices, protected apps) is listed with why Tidy leaves it alone.
    /// </summary>
    public static List<PresetItem> BootItems(List<BootSlow> slow, List<StartupApp> startup)
    {
        var list = new List<PresetItem>();
        foreach (var s in slow)
        {
            var exe = Path.GetFileName(s.File);
            var app = s.Kind == BootSlowKind.App && exe != ""
                ? startup.FirstOrDefault(a => a.Enabled && (a.ExePath is { } e && Path.GetFileName(e).Equals(exe, StringComparison.OrdinalIgnoreCase)
                                                             || a.Command.Contains(exe, StringComparison.OrdinalIgnoreCase)))
                : null;
            var level = app != null ? Level($"{app.Name} {Squirrel.Replace(app.Command, "")} {app.Publisher}") : null;
            var times = s.Times == 1 ? "retrasó 1 arranque" : $"retrasó {s.Times} arranques";
            var kind = s.Kind switch { BootSlowKind.App => "App", BootSlowKind.Driver => "Driver", BootSlowKind.Service => "Servicio", _ => "Dispositivo" };
            var detail = $"{kind} · {times} · {s.AvgSec:0.0} s cada vez";
            if (app != null && level is { } l)
                list.Add(new(PresetKind.App, app.Name, detail, "Arranca sola con Windows. Sigue instalada y se abre normal cuando la necesites.",
                    l, () => { StartupApps.SetEnabled(app, false); return (true, ""); }));
            else
                list.Add(new(PresetKind.Info, s.Name, detail, s.Kind switch
                {
                    BootSlowKind.App when app != null => "Es de seguridad, audio, drivers o hardware: Tidy no la quita del inicio.",
                    BootSlowKind.App => "Ya no arranca sola con Windows, o la abre otro programa: no hay nada que quitar del inicio.",
                    BootSlowKind.Driver => "Es un driver: lo necesita tu hardware. Si retrasa mucho, mira si hay versión nueva en Drivers.",
                    BootSlowKind.Service => "Es un servicio: si sabes que no lo usas, míralo en Servicios y tareas.",
                    _ => "Es un dispositivo: a veces lo retrasa un USB o un disco externo conectado al encender.",
                }, 0, () => (false, ""), On: false));
        }
        return list;
    }

    /// <summary>Lower level = safer to turn off. Null = never touched.</summary>
    public static int? Level(string text, bool launchersAllowed = true) =>
        Protect.IsMatch(text) ? null
        : Telemetry.IsMatch(text) ? 1
        : Hardware.IsMatch(text) ? null
        : launchersAllowed && Launchers.IsMatch(text) ? 2
        : 3;

    /// <summary>Everything the "Máximo" preset would change on this PC right now (lower presets take the items up to their level).</summary>
    public static List<PresetItem> Candidates()
    {
        var list = new List<PresetItem>();
        foreach (var a in StartupApps.List().Where(a => a.Enabled))
            // Squirrel apps (Discord, Slack…) start through "Update.exe --processStart App.exe": that's a launcher, not an updater
            if (Level($"{a.Name} {Squirrel.Replace(a.Command, "")} {a.Publisher}") is { } l)
                list.Add(new(PresetKind.App, a.Name, string.IsNullOrEmpty(a.Publisher) ? "App de inicio" : $"App de inicio · {a.Publisher}",
                    l == 1 ? "Envía estadísticas o informes de errores a su fabricante." :
                    l == 2 ? "Se abre sola al encender. Puedes abrirla cuando la vayas a usar." :
                             "Arranca sola con Windows. Sigue instalada y se abre normal cuando la necesites.",
                    l, () => { StartupApps.SetEnabled(a, false); return (true, ""); }));

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var s in Services.List().Where(s => !s.Protected && s.StartMode.StartsWith("Automático")))
        {
            var exe = StartupApps.ExeFromCommand(s.Path);
            if (exe == null || exe.StartsWith(win, StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) continue;
            var company = FileVersionInfo(exe);
            var text = $"{s.Name} {s.Display} {exe} {company}";
            if (text.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            // services break programs more easily than startup apps: only telemetry and a short list of known idle ones
            if (Level(text, launchersAllowed: false) is { } l && (l == 1 || IdleServices.IsMatch(text)))
                list.Add(new(PresetKind.Service, s.Display, string.IsNullOrEmpty(company) ? "Servicio" : $"Servicio · {company}",
                    l == 1 ? "Servicio de telemetría. Pasa a manual: deja de arrancar con Windows."
                           : "Servicio de terceros que arranca siempre con Windows. En manual arranca solo cuando su programa lo pide.",
                    l, () => Services.SetMode(s.Name, StartMode.Manual)));
        }

        foreach (var t in ScheduledTasks.List().Where(t => t.Enabled && t.CanChange && !t.Path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase)))
            if (!Protect.IsMatch($"{t.Path} {t.Action}") && Telemetry.IsMatch($"{t.Path} {t.Action}"))
                list.Add(new(PresetKind.Task, t.Name, StartupApps.ExeFromCommand(t.Action) is { } exe && FileVersionInfo(exe) is { Length: > 0 } c ? $"Tarea programada · {c}" : "Tarea programada",
                    "Tarea de telemetría o informes de errores. Se puede desactivar sin perder nada.", 1, () => ScheduledTasks.SetEnabled(t.Path, false)));

        return list.OrderBy(i => i.Level).ThenBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string FileVersionInfo(string exe)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() ?? ""; }
        catch { return ""; }
    }
}
