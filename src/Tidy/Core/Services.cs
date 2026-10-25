using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tidy.Core;

public record ServiceInfo(string Name, string Display, string Description, string StartMode, string State, string Path, bool Protected, string? Hint, bool Changed);

public enum StartMode { Auto, Delayed, Manual, Disabled }

/// <summary>Windows services: list, start/stop and startup type, with the original value saved before every change.</summary>
public static class Services
{
    const string BackupFile = "services-backup.json";
    public record Saved(int Start, int Delayed);

    /// <summary>Never offered: Windows (or its security, sign-in, audio, network or updates) breaks without them.</summary>
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "Appinfo", "AppIDSvc", "AppXSvc", "AudioEndpointBuilder", "Audiosrv", "BFE", "BITS", "BrokerInfrastructure", "camsvc", "ClipSVC",
        "CoreMessagingRegistrar", "CryptSvc", "DcomLaunch", "DeviceInstall", "Dhcp", "DispBrokerDesktopSvc", "Dnscache", "DoSvc", "DsmSvc",
        "EventLog", "EventSystem", "FontCache", "gpsvc", "InstallService", "KeyIso", "LanmanWorkstation", "LSM", "MDCoreSvc", "mpssvc",
        "msiserver", "netprofm", "NlaSvc", "nsi", "PlugPlay", "Power", "ProfSvc", "RpcEptMapper", "RpcSs", "SamSs", "Schedule", "SDRSVC",
        "SecurityHealthService", "Sense", "SENS", "SgrmBroker", "ShellHWDetection", "sppsvc", "StateRepository", "StorSvc", "swprv",
        "SystemEventsBroker", "TextInputManagementService", "Themes", "TimeBrokerSvc", "TokenBroker", "TrustedInstaller", "UserManager",
        "UsoSvc", "VaultSvc", "VSS", "WaaSMedicSvc", "Wcmsvc", "WdNisSvc", "webthreatdefsvc", "webthreatdefusersvc", "WinDefend",
        "WinHttpAutoProxySvc", "Winmgmt", "wlidsvc", "wscsvc", "wuauserv", "CDPSvc", "DusmSvc", "NgcSvc", "NgcCtnrSvc", "seclogon",
    };

    /// <summary>Plain-language advice for services people usually ask about.</summary>
    public static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SysMain"] = "Precarga apps en RAM. Con SSD y mucha RAM se puede desactivar.",
        ["WSearch"] = "Indexa archivos para buscarlos rápido. Sin él, buscar archivos es más lento y Outlook no busca correos.",
        ["Spooler"] = "Cola de impresión. Desactívalo solo si no usas impresora ni \"Imprimir a PDF\".",
        ["Fax"] = "Fax. Se puede desactivar.",
        ["DiagTrack"] = "Telemetría de Windows. Se puede desactivar.",
        ["dmwappushservice"] = "Enrutamiento de mensajes de telemetría. Se puede desactivar.",
        ["MapsBroker"] = "Descarga de mapas sin conexión. Se puede desactivar si no usas la app Mapas.",
        ["lfsvc"] = "Geolocalización. Sin él, las apps no saben dónde estás.",
        ["RetailDemo"] = "Modo demo para tiendas. Se puede desactivar.",
        ["XblAuthManager"] = "Inicio de sesión de Xbox Live. Necesario para Game Pass y juegos de la Microsoft Store.",
        ["XblGameSave"] = "Partidas guardadas en la nube de Xbox. Necesario para Game Pass.",
        ["XboxNetApiSvc"] = "Red de Xbox Live (multijugador de juegos de Microsoft).",
        ["XboxGipSvc"] = "Accesorios Xbox. Necesario para mandos de Xbox por cable o adaptador.",
        ["WMPNetworkSvc"] = "Compartir la biblioteca de Windows Media en red. Se puede desactivar.",
        ["RemoteRegistry"] = "Permite editar tu registro desde otro PC. Mejor desactivado.",
        ["PhoneSvc"] = "Telefonía (llamadas desde Enlace móvil). Se puede desactivar si no lo usas.",
        ["WerSvc"] = "Informes de errores de Windows. Se puede poner en manual.",
        ["wisvc"] = "Programa Insider. Se puede desactivar si no pruebas betas.",
        ["WbioSrvc"] = "Biometría: necesario para Windows Hello con huella o cara.",
        ["SEMgrSvc"] = "Pagos NFC. Se puede desactivar en un sobremesa.",
        ["SCardSvr"] = "Tarjetas inteligentes (DNIe, certificados en tarjeta). Necesario si los usas.",
        ["TermService"] = "Escritorio remoto. Desactívalo si no te conectas a este PC desde fuera.",
        ["icssvc"] = "Zona Wi-Fi móvil (compartir conexión). Se puede desactivar si no la usas.",
        ["bthserv"] = "Bluetooth. Necesario para auriculares, mandos y ratones Bluetooth.",
        ["WlanSvc"] = "Wi-Fi. Necesario si te conectas por Wi-Fi.",
        ["edgeupdate"] = "Actualizador de Edge. Si lo desactivas, Edge no recibe parches de seguridad.",
        ["edgeupdatem"] = "Actualizador de Edge. Si lo desactivas, Edge no recibe parches de seguridad.",
        ["gupdate"] = "Actualizador de Google. Si lo desactivas, Chrome no recibe parches de seguridad.",
        ["gupdatem"] = "Actualizador de Google. Si lo desactivas, Chrome no recibe parches de seguridad.",
        ["SharedAccess"] = "Compartir conexión a Internet (ICS). Se puede desactivar si no la compartes.",
        ["TrkWks"] = "Mantiene accesos directos al mover archivos entre equipos en red. Se puede poner en manual.",
        ["WpcMonSvc"] = "Control parental. Se puede desactivar si no lo usas.",
        ["WdiServiceHost"] = "Diagnóstico de problemas. Se puede poner en manual.",
        ["WdiSystemHost"] = "Diagnóstico de problemas. Se puede poner en manual.",
        ["diagnosticshub.standardcollector.service"] = "Recopilador de diagnóstico para desarrolladores. Se puede desactivar.",
        ["SensorService"] = "Sensores (brillo automático, rotación). En sobremesa no hace nada.",
        ["PcaSvc"] = "Asistente de compatibilidad de programas. Se puede desactivar.",
        ["NvTelemetryContainer"] = "Telemetría de NVIDIA (drivers antiguos). Se puede desactivar.",
        ["NVDisplay.ContainerLocalSystem"] = "Panel de control y overlay de NVIDIA. Necesario para el Panel de control de NVIDIA.",
        ["ssh-agent"] = "Agente SSH para desarrolladores.",
        ["lmhosts"] = "NetBIOS sobre TCP/IP, para redes Windows antiguas.",
    };

    static readonly Regex UserInstance = new(@"_[0-9a-f]{4,}$", RegexOptions.IgnoreCase);
    static readonly Regex SafeName = new(@"^[A-Za-z0-9_ .\-{}$@]+$");

    public static List<ServiceInfo> List()
    {
        var backup = LoadBackup();
        using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, Description, StartMode, State, PathName FROM Win32_Service");
        return s.Get().Cast<ManagementObject>().Select(o =>
        {
            var name = (string)o["Name"];
            var mode = Mode(name, o["StartMode"] as string ?? "");
            return new ServiceInfo(name, o["DisplayName"] as string ?? name, o["Description"] as string ?? "", mode,
                o["State"] as string ?? "", o["PathName"] as string ?? "", IsProtected(name, mode),
                Hints.GetValueOrDefault(name), backup.ContainsKey(name));
        }).OrderBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string Mode(string name, string wmi) => wmi switch
    {
        "Auto" => Equals(Reg(name)?.GetValue("DelayedAutostart"), 1) ? "Automático (retrasado)" : "Automático",
        "Manual" => "Manual",
        "Disabled" => "Deshabilitado",
        "Boot" or "System" => "Sistema",
        _ => wmi,
    };

    static RegistryKey? Reg(string name, bool write = false) =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", write);

    public static bool IsProtected(string name, string mode) =>
        Critical.Contains(name) || UserInstance.IsMatch(name) || mode == "Sistema" || !SafeName.IsMatch(name);

    /// <summary>Changes the startup type with sc.exe (the supported way). The original value is saved first.</summary>
    public static (bool Ok, string Message) SetMode(string name, StartMode mode)
    {
        if (!Validate(name, out var err)) return (false, err);
        using (var k = Reg(name))
        {
            if (k == null) return (false, "El servicio no existe.");
            var backup = LoadBackup();
            if (!backup.ContainsKey(name))
            {
                backup[name] = new((int)(k.GetValue("Start") ?? 3), (int)(k.GetValue("DelayedAutostart") ?? 0));
                Store.WriteJson(BackupFile, backup, secure: true); // saved before touching anything
            }
        }
        var arg = mode switch { StartMode.Auto => "auto", StartMode.Delayed => "delayed-auto", StartMode.Manual => "demand", _ => "disabled" };
        var (code, output) = Store.Run("sc.exe", $"config \"{name}\" start= {arg}");
        Store.Log($"Service {name}: start type -> {arg}: {(code == 0 ? "OK" : output.Trim())}");
        if (code == 0) History.Add(HistoryKind.Service, $"Servicio {name}: {mode switch { StartMode.Auto => "automático", StartMode.Delayed => "automático (retrasado)", StartMode.Manual => "manual", _ => "deshabilitado" }}", "", $"service:{name}");
        return code == 0 ? (true, "") : (false, output.Trim());
    }

    public static (bool Ok, string Message) SetRunning(string name, bool run)
    {
        if (!Validate(name, out var err)) return (false, err);
        try
        {
            using var sc = new ServiceController(name);
            if (run) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
            else { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
            Store.Log($"Service {name}: {(run ? "started" : "stopped")}");
            return (true, "");
        }
        catch (Exception e) { Store.Log($"Service {name}: {(run ? "start" : "stop")} failed: {e.Message}"); return (false, e.InnerException?.Message ?? e.Message); }
    }

    public static string[] Dependents(string name)
    {
        try { using var sc = new ServiceController(name); return sc.DependentServices.Where(d => d.Status != ServiceControllerStatus.Stopped).Select(d => d.DisplayName).ToArray(); }
        catch { return []; }
    }

    static bool Validate(string name, out string error)
    {
        error = "";
        using var k = Reg(name);
        var start = k?.GetValue("Start") as int?;
        if (k == null) error = "El servicio no existe.";
        else if (IsProtected(name, start is 0 or 1 ? "Sistema" : "")) error = "Servicio protegido: Windows lo necesita para funcionar.";
        return error == "";
    }

    public static Dictionary<string, Saved> LoadBackup() => Store.ReadJson<Dictionary<string, Saved>>(BackupFile, secure: true) ?? new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts the startup type back to what it was before Tidy touched it.</summary>
    public static bool Restore(string name)
    {
        var backup = LoadBackup();
        if (!backup.TryGetValue(name, out var s)) return false;
        // the backup is user-writable: only accept real startup values, for services that still pass validation
        if (s.Start is < 2 or > 4 || !Validate(name, out _)) { Store.Log($"Service {name}: invalid backup, ignored"); return false; }
        var mode = s.Start switch { 2 => s.Delayed == 1 ? StartMode.Delayed : StartMode.Auto, 3 => StartMode.Manual, _ => StartMode.Disabled };
        var ok = SetMode(name, mode).Ok;
        if (ok) { backup.Remove(name); Store.WriteJson(BackupFile, backup, secure: true); }
        return ok;
    }

    public static int RestoreAll() => LoadBackup().Keys.ToList().Count(Restore);
}
