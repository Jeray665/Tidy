using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tidy.Core;

public record DnsProvider(string Id, string Name, string Description, string[] Servers);
public record Adapter(string Id, int Index, string Name, string Description, bool Wireless, string[] Dns, bool Static);

public static class Network
{
    // ---------- DNS ----------
    public static readonly DnsProvider[] Providers =
    [
        new("auto", "Automático (router / proveedor)", "Lo que Windows trae por defecto: el DNS que da tu router.", []),
        new("cloudflare", "Cloudflare", "El más rápido en casi toda Europa. No guarda tu historial.", ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"]),
        new("cloudflare-malware", "Cloudflare (bloquea malware)", "Igual de rápido y además bloquea webs de malware y phishing.", ["1.1.1.2", "1.0.0.2", "2606:4700:4700::1112", "2606:4700:4700::1002"]),
        new("cloudflare-family", "Cloudflare (familia)", "Bloquea malware y contenido para adultos.", ["1.1.1.3", "1.0.0.3", "2606:4700:4700::1113", "2606:4700:4700::1003"]),
        new("google", "Google", "Muy fiable y rápido.", ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"]),
        new("quad9", "Quad9", "Bloquea dominios maliciosos. Fundación sin ánimo de lucro, en Suiza.", ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"]),
        new("adguard", "AdGuard", "Bloquea anuncios y rastreadores en todo el PC.", ["94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"]),
        new("opendns", "OpenDNS (Cisco)", "Clásico y estable.", ["208.67.222.222", "208.67.220.220", "2620:119:35::35", "2620:119:53::53"]),
    ];

    const string DnsBackup = "dns-backup.json";
    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    static readonly Regex Guid = new(@"^\{[0-9A-Fa-f-]{36}\}$");

    /// <summary>VPNs and virtual adapters manage their own DNS: touching it would break them.</summary>
    static readonly Regex VirtualAdapter = new(@"VPN|WARP|Tunnel|TAP-|Wintun|WireGuard|Tailscale|ZeroTier|Hamachi|Radmin|Virtual|Hyper-V|VMware|VirtualBox|Loopback|Npcap|Bluetooth", RegexOptions.IgnoreCase);

    public static List<string> ActiveVpns() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && VirtualAdapter.IsMatch(n.Description + " " + n.Name)
                    && Regex.IsMatch(n.Description + " " + n.Name, "VPN|WARP|WireGuard|Tailscale|ZeroTier|Hamachi|Radmin|TAP-|Wintun", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(n.Name, @"Filter|Scheduler|-\d{4}$", RegexOptions.IgnoreCase)) // skip the WFP/QoS sub-interfaces a VPN driver adds
        .Select(n => n.Name).ToList();

    public static List<Adapter> Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                    && n.Supports(NetworkInterfaceComponent.IPv4) && !VirtualAdapter.IsMatch(n.Description + " " + n.Name))
        .Select(n => new Adapter(n.Id, n.GetIPProperties().GetIPv4Properties().Index, n.Name, n.Description,
            n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            n.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToArray(), StaticDns(n.Id).Length > 0))
        .ToList();

    /// <summary>DNS servers typed by hand for an adapter (empty = automatic/DHCP).</summary>
    static string[] StaticDns(string id)
    {
        if (!Guid.IsMatch(id)) return [];
        string Get(string stack)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{stack}\Parameters\Interfaces\{id}");
            return k?.GetValue("NameServer") as string ?? "";
        }
        return (Get("Tcpip") + "," + Get("Tcpip6")).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
    }

    public static string CurrentProvider(Adapter a) =>
        !a.Static ? "auto" : Providers.FirstOrDefault(p => p.Servers.Length > 0 && a.Dns.Any(d => p.Servers.Contains(d)))?.Id ?? "custom";

    public static (bool Ok, string Message) SetDns(Adapter a, string[] servers)
    {
        if (servers.Any(s => !IPAddress.TryParse(s, out _))) return (false, "Dirección no válida.");
        var backup = Store.ReadJson<Dictionary<string, string[]>>(DnsBackup, secure: true) ?? [];
        if (!backup.ContainsKey(a.Id)) { backup[a.Id] = StaticDns(a.Id); Store.WriteJson(DnsBackup, backup, secure: true); }
        var cmd = servers.Length == 0
            ? $"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ResetServerAddresses"
            : $"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses {string.Join(",", servers.Select(s => $"'{s}'"))}";
        var (code, output) = Store.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"{cmd}\"");
        Store.Run("ipconfig.exe", "/flushdns");
        Store.Log($"DNS {a.Name}: {(servers.Length == 0 ? "automatic" : string.Join(", ", servers))} -> {(code == 0 ? "OK" : output.Trim())}");
        if (code == 0) History.Add(HistoryKind.Network, $"DNS de {a.Name}: {(servers.Length == 0 ? "automático" : string.Join(", ", servers))}", "", "dns");
        return code == 0 ? (true, "") : (false, output.Trim());
    }

    public static bool HasDnsBackup => (Store.ReadJson<Dictionary<string, string[]>>(DnsBackup, secure: true)?.Count ?? 0) > 0;

    public static int RestoreDns()
    {
        var backup = Store.ReadJson<Dictionary<string, string[]>>(DnsBackup, secure: true) ?? [];
        var n = 0;
        foreach (var a in Adapters().Where(a => backup.ContainsKey(a.Id)))
            if (SetDns(a, backup[a.Id]).Ok) { backup.Remove(a.Id); n++; }
        Store.WriteJson(DnsBackup, backup, secure: true);
        return n;
    }

    public static async Task<long?> Latency(string host)
    {
        using var p = new Ping();
        var times = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            try { var r = await p.SendPingAsync(host, 1500); if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); }
            catch { }
        }
        return times.Count > 0 ? (long)times.Average() : null;
    }

    // ---------- hosts ----------
    public static string HostsPath => Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
    const string BlockStart = "# >>> Tidy: bloqueo de anuncios (no editar a mano) >>>", BlockEnd = "# <<< Tidy: bloqueo de anuncios <<<";
    const string BlockListUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";
    static readonly Regex BlockLine = new(@"^0\.0\.0\.0 [a-z0-9][a-z0-9._-]*[a-z0-9]$", RegexOptions.IgnoreCase);

    public static string ReadHosts() => File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
    public static bool HostsReadOnly => File.Exists(HostsPath) && File.GetAttributes(HostsPath).HasFlag(FileAttributes.ReadOnly);
    public static bool HasBlockList => ReadHosts().Contains(BlockStart);
    static string HostsBackupDir => Path.Combine(Store.SecureDir, "hosts");

    /// <summary>Saves a dated copy first, then writes. Every version can be restored from Copias de seguridad.</summary>
    public static string WriteHosts(string content)
    {
        Directory.CreateDirectory(HostsBackupDir);
        var copy = Path.Combine(HostsBackupDir, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (File.Exists(HostsPath)) File.Copy(HostsPath, copy, true);
        var ro = HostsReadOnly;
        if (ro) File.SetAttributes(HostsPath, File.GetAttributes(HostsPath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(HostsPath, content, new UTF8Encoding(false));
        if (ro) File.SetAttributes(HostsPath, File.GetAttributes(HostsPath) | FileAttributes.ReadOnly);
        Store.Run("ipconfig.exe", "/flushdns");
        Store.Log($"Hosts file saved ({content.Length:N0} characters). Previous copy: {copy}");
        History.Add(HistoryKind.Network, "Archivo hosts modificado", $"Copia anterior: {Path.GetFileName(copy)}", File.Exists(copy) ? $"hosts:{Path.GetFileName(copy)}" : null);
        return copy;
    }

    public static List<FileInfo> HostsBackups() => Directory.Exists(HostsBackupDir)
        ? new DirectoryInfo(HostsBackupDir).GetFiles("hosts-*.txt").OrderByDescending(f => f.CreationTime).ToList() : [];

    public static void SetHostsReadOnly(bool on)
    {
        var a = File.GetAttributes(HostsPath);
        File.SetAttributes(HostsPath, on ? a | FileAttributes.ReadOnly : a & ~FileAttributes.ReadOnly);
        Store.Log($"Hosts file {(on ? "locked (read-only)" : "unlocked")}");
    }

    /// <summary>Downloads the StevenBlack list and keeps ONLY lines of the form "0.0.0.0 dominio": nothing in it can redirect a site elsewhere.</summary>
    public static async Task<(bool Ok, string Message)> AddBlockList()
    {
        string text;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            text = await http.GetStringAsync(BlockListUrl);
        }
        catch (Exception e) { return (false, $"No se pudo descargar la lista: {e.Message}"); }
        var lines = text.Split('\n').Select(l => l.Split('#')[0].Trim())
            .Where(l => BlockLine.IsMatch(l) && l != "0.0.0.0 0.0.0.0").Distinct().ToList();
        if (lines.Count < 1000) return (false, "La lista descargada no parece válida; no se ha tocado nada.");
        var current = RemoveBlock(ReadHosts()).TrimEnd();
        WriteHosts($"{current}\r\n\r\n{BlockStart}\r\n{string.Join("\r\n", lines)}\r\n{BlockEnd}\r\n");
        return (true, $"{lines.Count:N0} dominios de anuncios y rastreo bloqueados.");
    }

    public static void RemoveBlockList() => WriteHosts(RemoveBlock(ReadHosts()).TrimEnd() + "\r\n");

    static string RemoveBlock(string s)
    {
        var a = s.IndexOf(BlockStart, StringComparison.Ordinal);
        var b = s.IndexOf(BlockEnd, StringComparison.Ordinal);
        return a >= 0 && b > a ? s[..a] + s[(b + BlockEnd.Length)..] : s;
    }
}

/// <summary>One-click Windows repairs and system tools. Everything runs by absolute path, nothing by association.</summary>
public static class Repairs
{
    public record Tool(string Name, string Description, string Icon, string Exe, string Args = "");

    static string Sys(string f) => Path.Combine(Environment.SystemDirectory, f);
    static string Win(string f) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), f);

    public static readonly Tool[] Tools =
    [
        new("Editor del Registro", "Ver y editar el registro de Windows a mano.", "Code24", Win("regedit.exe")),
        new("Servicios", "La consola clásica de servicios.", "Settings24", Sys("mmc.exe"), Sys("services.msc")),
        new("Administrador de dispositivos", "Drivers y hardware.", "DeveloperBoard24", Sys("mmc.exe"), Sys("devmgmt.msc")),
        new("Administración de discos", "Particiones, letras de unidad, formatear.", "Storage24", Sys("mmc.exe"), Sys("diskmgmt.msc")),
        new("Visor de eventos", "Errores y avisos de Windows y apps.", "DocumentSearch24", Sys("mmc.exe"), Sys("eventvwr.msc")),
        new("Programador de tareas", "Todas las tareas programadas.", "CalendarClock24", Sys("mmc.exe"), Sys("taskschd.msc")),
        new("Directivas de grupo", "Editor de directivas (Windows Pro).", "Shield24", Sys("mmc.exe"), Sys("gpedit.msc")),
        new("Configuración del sistema", "msconfig: arranque y diagnóstico.", "WrenchScrewdriver24", Sys("msconfig.exe")),
        new("Monitor de recursos", "CPU, disco, red y memoria por proceso.", "DataUsage24", Sys("resmon.exe")),
        new("Información del sistema", "msinfo32: todo sobre tu hardware y software.", "Info24", Sys("msinfo32.exe")),
        new("Administrador de tareas", "El de siempre.", "TaskListLtr24", Sys("taskmgr.exe")),
        new("Opciones de rendimiento", "Efectos visuales y memoria virtual.", "TopSpeed24", Sys("SystemPropertiesPerformance.exe")),
        new("Variables de entorno", "Editar PATH y otras variables.", "Braces24", Sys("rundll32.exe"), "sysdm.cpl,EditEnvironmentVariables"),
        new("Conexiones de red", "Adaptadores de red clásicos.", "Wifi124", Sys("control.exe"), "ncpa.cpl"),
        new("Opciones de energía", "Planes de energía clásicos.", "Battery1024", Sys("control.exe"), "powercfg.cpl"),
        new("Programas y características", "Desinstalador clásico de Windows.", "AppsList24", Sys("control.exe"), "appwiz.cpl"),
        new("Liberador de espacio", "La limpieza clásica de Windows.", "Broom24", Sys("cleanmgr.exe")),
        new("Símbolo del sistema (admin)", "Consola con permisos de administrador.", "WindowConsole20", Sys("cmd.exe")),
        new("PowerShell (admin)", "PowerShell con permisos de administrador.", "WindowConsole20", Sys(@"WindowsPowerShell\v1.0\powershell.exe")),
    ];

    public record Fix(string Id, string Name, string Description, string Icon, bool NeedsRestart, bool Console);

    public static readonly Fix[] Fixes =
    [
        new("flushdns", "Vaciar la caché DNS", "Arregla webs que no cargan tras cambiar de red o de DNS. Inofensivo.", "ArrowSync24", false, false),
        new("iconcache", "Reconstruir caché de iconos", "Arregla iconos en blanco o equivocados.", "Image24", false, false),
        new("explorer", "Reiniciar el Explorador", "Aplica cambios del Explorador y la barra de tareas. Se cerrarán las ventanas de carpetas abiertas.", "ArrowClockwise24", false, false),
        new("netreset", "Restablecer red (Winsock e IP)", "Arregla Internet cuando nada más funciona. Necesita reiniciar; las VPN quizá haya que reinstalarlas.", "Globe24", true, false),
        new("sfc", "Reparar archivos de Windows (SFC)", "Comprueba los archivos del sistema y repara los dañados. Tarda 10-20 min en una ventana aparte.", "ShieldCheckmark24", false, true),
        new("dism", "Reparar la imagen de Windows (DISM)", "Repara el almacén de componentes desde Windows Update. Ejecútalo antes de SFC si SFC no arregla todo.", "Wrench24", false, true),
        new("chkdsk", "Analizar el disco C: (CHKDSK)", "Busca errores del sistema de archivos sin reiniciar (solo lectura).", "Storage24", false, true),
        new("policies", "Desbloquear Regedit, Administrador de tareas y CMD", "Si un virus o un programa los bloqueó con directivas, esto los vuelve a habilitar.", "LockOpen24", false, false),
        new("advboot", "Reiniciar en opciones avanzadas", "Reinicia en el menú de recuperación (modo seguro, restaurar sistema, UEFI…). No cambia nada de forma permanente.", "Power24", false, false),
    ];

    public static (bool Ok, string Message) Run(Fix f)
    {
        Store.Log($"Repair: {f.Name}");
        switch (f.Id)
        {
            case "flushdns": return Result(Store.Run("ipconfig.exe", "/flushdns"));
            case "iconcache": return Result(Store.Run("ie4uinit.exe", "-show"));
            case "explorer": RestartExplorer(); return (true, "");
            case "netreset":
                var a = Store.Run("netsh.exe", "winsock reset");
                var b = Store.Run("netsh.exe", "int ip reset");
                return (a.Exit == 0 && b.Exit == 0, (a.Output + b.Output).Trim());
            case "sfc": OpenConsole("sfc /scannow"); return (true, "");
            case "dism": OpenConsole("DISM /Online /Cleanup-Image /RestoreHealth"); return (true, "");
            case "chkdsk":
                if (CanStart("chkdsk.exe")) { OpenConsole("chkdsk C: /scan"); return (true, ""); }
                // some PCs (security software) won't let chkdsk.exe start at all, not even as admin:
                // the same read-only online scan through the Storage service instead
                Store.Log("Repair: chkdsk.exe can't start on this PC, using Repair-Volume -Scan");
                Elevated(Sys("conhost.exe"), $"\"{Sys(@"WindowsPowerShell\v1.0\powershell.exe")}\" -NoExit -NoProfile -EncodedCommand {ScanScript()}");
                return (true, "");
            case "policies":
                // each removed value goes into the history entry, so "undo" can put back exactly that
                var removed = new List<string>();
                foreach (var (hive, key, name) in Policies)
                {
                    using var k = hive.OpenSubKey(key, true);
                    if (k?.GetValue(name) is not int v) continue;
                    k.DeleteValue(name, false);
                    removed.Add($"{hive.Name}|{key}|{name}|{v}");
                    Store.Log($"  removed {hive.Name}\\{key}\\{name} = {v}");
                }
                if (removed.Count > 0)
                    History.Add(HistoryKind.Tweak, "Herramientas desbloqueadas", string.Join(", ", removed.Select(r => r.Split('|')[2]).Distinct()), "policies:" + string.Join(";", removed));
                return (true, "");
            case "advboot": return Result(Store.Run("shutdown.exe", "/r /o /t 5"));
        }
        return (false, "Desconocido");
        static (bool, string) Result((int Exit, string Output) r) => (r.Exit == 0, r.Output.Trim());
    }

    static readonly (RegistryKey Hive, string Key, string Name)[] Policies =
    [
        (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools"),
        (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr"),
        (Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\System", "DisableCMD"),
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools"),
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr"),
        (Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "DisableCMD"),
    ];

    /// <summary>Undo of "policies": "HKEY_CURRENT_USER|key|name|value;…". Only the six known values are ever written back.</summary>
    public static bool RestorePolicies(string arg)
    {
        var all = true;
        foreach (var part in arg.Split(';'))
        {
            var p = part.Split('|');
            var known = Policies.FirstOrDefault(x => p.Length == 4 && x.Hive.Name == p[0] && x.Key == p[1] && x.Name == p[2]);
            if (known.Hive == null || !int.TryParse(p[3], out var v)) { Store.Log($"Policies undo: ignored {part}"); all = false; continue; }
            using var k = known.Hive.CreateSubKey(known.Key, true);
            k.SetValue(known.Name, v, RegistryValueKind.DWord);
            Store.Log($"  restored {p[0]}\\{p[1]}\\{p[2]} = {v}");
        }
        return all;
    }

    /// <summary>Kills the shell; Winlogon restarts it un-elevated by itself (starting it from here would run it as admin).</summary>
    public static void RestartExplorer()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
            try { p.Kill(); } catch { }
    }

    // "runas" keeps the tool elevated (no prompt when Tidy already is). Consoles go through conhost.exe
    // so Windows Terminal, the default terminal, can't take them over in a window without admin rights.
    static void Elevated(string exe, string args) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory });

    /// <summary>Set by the app to its translator; Core itself doesn't know the UI language.</summary>
    public static Func<string, string> Translate { get; set; } = s => s;

    /// <summary>Repair-Volume's own progress says "Repairing volume" in English and its stage messages come out with broken
    /// accents, so the console shows Tidy's messages instead: what it does, then the result in plain words.</summary>
    public static string ScanScript()
    {
        string Ps(string s) => "'" + Translate(s).Replace("'", "''") + "'";
        string start = Ps("Analizando el disco C: en modo solo lectura (no cambia nada). Puede tardar unos minutos…"),
               ok = Ps("Listo: el disco C: no tiene errores."),
               found = Ps("Windows ha encontrado errores en C: ({0}). Para repararlos: Explorador de archivos > clic derecho en C: > Propiedades > Herramientas > Comprobar."),
               failed = Ps("No se pudo analizar el disco: {0}"),
               close = Ps("Ya puedes cerrar esta ventana.");
        var script = $$"""
            $global:ProgressPreference = 'SilentlyContinue'   # global: Repair-Volume's module only reads that one
            Write-Host {{start}}
            try {
                $r = "$(Repair-Volume -DriveLetter C -Scan -ErrorAction Stop)"
                if ($r -eq 'NoErrorsFound') { Write-Host {{ok}} -ForegroundColor Green }
                else { Write-Host ({{found}} -f $r) -ForegroundColor Yellow }
            } catch { Write-Host ({{failed}} -f $_.Exception.Message) -ForegroundColor Red }
            Write-Host {{close}}
            """;
        return Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
    }

    static bool CanStart(string exe)
    {
        try { Store.Run(exe, "/?", 10_000); return true; }
        catch (System.ComponentModel.Win32Exception e) { Store.Log($"  {exe} can't start: {e.Message}"); return false; }
    }

    static void OpenConsole(string command) => Elevated(Sys("conhost.exe"), $"\"{Sys("cmd.exe")}\" /k {command}");

    public static void Launch(Tool t)
    {
        if (t.Exe.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) || t.Exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            Elevated(Sys("conhost.exe"), $"\"{t.Exe}\" {t.Args}".TrimEnd());
        else Elevated(t.Exe, t.Args);
    }
}
