using System.IO;
using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace Tidy.Core;

public enum CheckState { Ok, Info, Warning, Bad, Unknown }

/// <param name="Fix">ms-settings:/windowsdefender: URI, a System32 tool (ends in .exe) or a Tidy page name ("StoragePage"). Null = nothing to open.</param>
public record SecurityCheck(string Id, string Title, CheckState State, string Detail, string Advice, string? Fix = null, string? FixLabel = null);

public record DiskHealth(string Name, string Media, string Bus, string Health, long Size, string Letters,
    int? TempC, int? WearPct, int? PowerOnHours, long? ReadErrors, long? WriteErrors)
{
    public CheckState State => Health switch
    {
        "Healthy" when WearPct >= 90 || ReadErrors > 0 || WriteErrors > 0 => CheckState.Warning,
        "Healthy" => CheckState.Ok,
        "Warning" => CheckState.Warning,
        "Unhealthy" => CheckState.Bad,
        _ => CheckState.Unknown,
    };
}

/// <summary>
/// Read-only security review. Nothing here changes a setting: every check only reads the registry/WMI and,
/// when something is off, points to the Windows page where the user fixes it themselves.
/// </summary>
public static class SecurityStatus
{
    static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static List<SecurityCheck> Run()
    {
        var checks = new List<Func<SecurityCheck>> { Antivirus, Exclusions, Firewall, Uac, SmartScreen, Updates, SecureBoot, Tpm, MemoryIntegrity, BitLocker, Smb1, RemoteDesktop, Disks };
        // independent read-only checks: in parallel, so the slow ones (Windows Update history, disks) don't add up
        return checks.AsParallel().AsOrdered().Select(c =>
        {
            try { return c(); }
            catch (Exception e)
            {
                Store.Log($"Security: {c.Method.Name}: {e.Message}");
                var title = c.Method.Name switch { "Tpm" => "Chip de seguridad (TPM)", "BitLocker" => "Cifrado del disco (BitLocker)", "Antivirus" => "Antivirus", "Updates" => "Windows Update", var n => n };
                var denied = e is UnauthorizedAccessException || e is ManagementException { ErrorCode: ManagementStatus.AccessDenied };
                return new SecurityCheck(c.Method.Name, title, CheckState.Unknown, denied ? "Windows solo deja leerlo con permisos de administrador." : "No se pudo comprobar.", denied ? "" : e.Message);
            }
        }).ToList();
    }

    static object? Reg(string key, string name) => Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + key, name, null);

    static List<ManagementBaseObject> Wmi(string scope, string query)
    {
        using var s = new ManagementObjectSearcher(scope, query);
        return s.Get().Cast<ManagementBaseObject>().ToList();
    }

    static SecurityCheck Antivirus()
    {
        // SecurityCenter2 productState: bits 12-15 = 1 when real-time scanning is on, bits 4-7 = 0 when definitions are current
        var products = Wmi(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct")
            .Select(o => (Name: (string)o["displayName"], State: Convert.ToInt32(o["productState"]))).ToList();
        var on = products.Where(p => (p.State >> 12 & 0xF) == 1).ToList();
        if (on.Count == 0)
            return new("av", "Antivirus", CheckState.Bad, products.Count == 0 ? "No hay ningún antivirus registrado." : $"{string.Join(", ", products.Select(p => p.Name))} está apagado.",
                "Activa la protección en tiempo real. Sin ella cualquier archivo que abras puede infectar el PC.", "windowsdefender://threat", "Abrir Seguridad de Windows");
        var names = string.Join(", ", on.Select(p => p.Name));
        if (on.Any(p => (p.State >> 4 & 0xF) != 0))
            return new("av", "Antivirus", CheckState.Warning, $"{names}: firmas desactualizadas.", "Actualiza las definiciones para que detecte las amenazas nuevas.", "windowsdefender://threat", "Actualizar");
        if (on.Any(p => p.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase)))
        {
            var d = Wmi(@"root\Microsoft\Windows\Defender", "SELECT AntivirusSignatureAge, IsTamperProtected FROM MSFT_MpComputerStatus").FirstOrDefault();
            if (d != null && d["IsTamperProtected"] is false)
                return new("av", "Antivirus", CheckState.Warning, "Microsoft Defender activo, pero la Protección contra alteraciones está apagada.",
                    "Actívala: impide que un programa malicioso apague Defender a escondidas.", "windowsdefender://threatsettings", "Abrir configuración");
        }
        return new("av", "Antivirus", CheckState.Ok, $"{names} activo y al día.", "");
    }

    static SecurityCheck Exclusions()
    {
        var pref = Wmi(@"root\Microsoft\Windows\Defender", "SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference").FirstOrDefault();
        var list = new[] { "ExclusionPath", "ExclusionProcess", "ExclusionExtension" }
            .SelectMany(p => pref?[p] as string[] ?? []).Where(s => !s.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)).ToList();
        return list.Count == 0
            ? new("excl", "Exclusiones del antivirus", CheckState.Ok, "No hay nada excluido del análisis.", "")
            : new("excl", "Exclusiones del antivirus", CheckState.Info, $"{list.Count} elemento(s) excluido(s): {string.Join(", ", list.Take(4))}{(list.Count > 4 ? "…" : "")}",
                "Si no las pusiste tú (o un programa de confianza), quítalas: el malware suele excluirse a sí mismo.", "windowsdefender://threatsettings", "Revisar exclusiones");
    }

    static SecurityCheck Firewall()
    {
        const string fw = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        var off = new[] { ("DomainProfile", "dominio"), ("StandardProfile", "privada"), ("PublicProfile", "pública") }
            .Where(p => Reg(fw + p.Item1, "EnableFirewall") is 0).Select(p => p.Item2).ToList();
        if (off.Count == 0) return new("fw", "Firewall", CheckState.Ok, "Activo en todas las redes.", "");
        var thirdParty = Wmi(@"root\SecurityCenter2", "SELECT displayName FROM FirewallProduct").Select(o => (string)o["displayName"]).ToList();
        if (thirdParty.Count > 0) return new("fw", "Firewall", CheckState.Ok, $"Lo gestiona {string.Join(", ", thirdParty)}.", "");
        return new("fw", "Firewall", CheckState.Bad, $"Apagado en la red {string.Join(", ", off)}.",
            "Enciéndelo: bloquea conexiones entrantes que nadie ha pedido.", "windowsdefender://network", "Abrir firewall");
    }

    static SecurityCheck Uac()
    {
        const string k = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        if (Reg(k, "EnableLUA") is 0)
            return new("uac", "Control de cuentas (UAC)", CheckState.Bad, "Desactivado: cualquier programa puede hacer cambios de administrador sin preguntarte.",
                "Vuelve a activarlo (requiere reiniciar).", "UserAccountControlSettings.exe", "Abrir UAC");
        if (Reg(k, "ConsentPromptBehaviorAdmin") is 0)
            return new("uac", "Control de cuentas (UAC)", CheckState.Warning, "Configurado en \"No notificar nunca\".",
                "Súbelo al nivel por defecto para que Windows te avise cuando un programa pida permisos.", "UserAccountControlSettings.exe", "Abrir UAC");
        return new("uac", "Control de cuentas (UAC)", CheckState.Ok, "Te avisa antes de que un programa haga cambios de administrador.", "");
    }

    static SecurityCheck SmartScreen()
    {
        var off = Reg(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen") is 0
                  || Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled") is "Off";
        return off
            ? new("ss", "SmartScreen", CheckState.Warning, "Desactivado para aplicaciones y archivos.", "Actívalo: avisa antes de abrir programas descargados desconocidos o maliciosos.", "windowsdefender://appbrowser", "Abrir control de aplicaciones")
            : new("ss", "SmartScreen", CheckState.Ok, "Comprueba los programas descargados antes de abrirlos.", "");
    }

    static SecurityCheck Updates()
    {
        if (Reg(@"SYSTEM\CurrentControlSet\Services\wuauserv", "Start") is 4)
            return new("wu", "Windows Update", CheckState.Bad, "El servicio está deshabilitado (algún optimizador lo apagó).",
                "Sin él no recibes parches de seguridad. Reactívalo en Servicios y tareas → wuauserv → Volver a como estaba / Manual.", "ServicesPage", "Ir a Servicios");
        if (Cleaner.UpdateRebootPending())
            return new("wu", "Windows Update", CheckState.Warning, "Hay actualizaciones instaladas esperando un reinicio.", "Reinicia cuando puedas para que se apliquen.", "ms-settings:windowsupdate", "Abrir Windows Update");
        DateTime? last = null;
        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
            dynamic searcher = session.CreateUpdateSearcher();
            int count = searcher.GetTotalHistoryCount();
            if (count > 0) foreach (dynamic h in searcher.QueryHistory(0, Math.Min(count, 20))) if ((int)h.ResultCode == 2) { DateTime d = h.Date; if (last == null || d > last) last = d; }
        }
        catch { }
        if (last is { } l && DateTime.Now - l > TimeSpan.FromDays(45))
            return new("wu", "Windows Update", CheckState.Warning, $"La última actualización instalada es del {l:dd/MM/yyyy}.", "Busca actualizaciones: llevas más de mes y medio sin parches.", "ms-settings:windowsupdate", "Abrir Windows Update");
        return new("wu", "Windows Update", CheckState.Ok, last is { } x ? $"Última actualización instalada: {x.ToLocalTime():dd/MM/yyyy}." : "Activo.", "");
    }

    static SecurityCheck SecureBoot() => Reg(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") switch
    {
        1 => new("sb", "Arranque seguro (Secure Boot)", CheckState.Ok, "Activado: impide que se cargue malware antes que Windows.", ""),
        0 => new("sb", "Arranque seguro (Secure Boot)", CheckState.Warning, "Desactivado en la BIOS/UEFI.",
            "Se activa en la BIOS (Boot → Secure Boot). Algunos juegos con anti-trampas (Valorant, Battlefield 6, CoD) lo exigen. Hazlo solo si sabes moverte por la BIOS."),
        _ => new("sb", "Arranque seguro (Secure Boot)", CheckState.Info, "El PC arranca en modo BIOS antiguo (no UEFI).", "No se puede activar sin convertir el disco a GPT; no lo toques si todo funciona."),
    };

    static SecurityCheck Tpm()
    {
        var tpm = Wmi(@"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm").FirstOrDefault();
        if (tpm == null) return new("tpm", "Chip de seguridad (TPM)", CheckState.Warning, "No se detecta TPM.", "Suele venir apagado en la BIOS (Intel PTT / AMD fTPM). Lo necesitan Windows 11, BitLocker y algunos anti-trampas.");
        var ver = ((tpm["SpecVersion"] as string) ?? "").Split(',')[0].Trim();
        return tpm["IsEnabled_InitialValue"] is true && tpm["IsActivated_InitialValue"] is true
            ? new("tpm", "Chip de seguridad (TPM)", CheckState.Ok, $"TPM {ver} activo.", "")
            : new("tpm", "Chip de seguridad (TPM)", CheckState.Warning, $"TPM {ver} presente pero desactivado.", "Actívalo en la BIOS (Intel PTT / AMD fTPM).");
    }

    static SecurityCheck MemoryIntegrity() =>
        Reg(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") is 1
            ? new("hvci", "Integridad de memoria", CheckState.Ok, "Activada: bloquea drivers maliciosos o vulnerables.", "")
            : new("hvci", "Integridad de memoria", CheckState.Info, "Desactivada.",
                "Protege contra drivers maliciosos. Microsoft la recomienda; en algunos juegos cuesta un pequeño porcentaje de FPS, por eso es decisión tuya.", "windowsdefender://coreisolation", "Abrir aislamiento del núcleo");

    static SecurityCheck BitLocker()
    {
        var c = Wmi(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'").FirstOrDefault();
        return c?["ProtectionStatus"] is 1u or 1
            ? new("bl", "Cifrado del disco (BitLocker)", CheckState.Ok, "El disco C: está cifrado.", "")
            : new("bl", "Cifrado del disco (BitLocker)", CheckState.Info, "El disco C: no está cifrado.",
                "Opcional en un sobremesa. Protege tus datos si te roban el PC o el disco. Si lo activas, guarda bien la clave de recuperación.",
                Path.Combine(Environment.SystemDirectory, "control.exe") + "|/name Microsoft.BitLockerDriveEncryption", "Abrir BitLocker");
    }

    static SecurityCheck Smb1()
    {
        var on = Reg(@"SYSTEM\CurrentControlSet\Services\mrxsmb10", "Start") is int s && s != 4;
        return on
            ? new("smb1", "SMBv1", CheckState.Warning, "El protocolo de red antiguo SMBv1 está activo.", "Es el que usó WannaCry. Desactívalo en Optimizaciones → \"Desactivar SMBv1\".", "TweaksPage", "Ir a Optimizaciones")
            : new("smb1", "SMBv1", CheckState.Ok, "Desactivado (protocolo antiguo e inseguro).", "");
    }

    static SecurityCheck RemoteDesktop() =>
        Reg(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") is 0
            ? new("rdp", "Escritorio remoto", CheckState.Info, "Activado: se puede entrar a este PC desde la red.", "Si no lo usas, desactívalo.", "ms-settings:remotedesktop", "Abrir escritorio remoto")
            : new("rdp", "Escritorio remoto", CheckState.Ok, "Desactivado.", "");

    static SecurityCheck Disks()
    {
        var disks = DiskStatus();
        if (disks.Count == 0) return new("disks", "Salud de los discos", CheckState.Unknown, "No se pudo leer.", "");
        var bad = disks.Where(d => d.State is CheckState.Bad or CheckState.Warning).ToList();
        return bad.Count == 0
            ? new("disks", "Salud de los discos", CheckState.Ok, $"{disks.Count} disco(s) sanos.", "")
            : new("disks", "Salud de los discos", bad.Any(d => d.State == CheckState.Bad) ? CheckState.Bad : CheckState.Warning,
                $"Revisa: {string.Join(", ", bad.Select(d => d.Name))}.", "Haz copia de tus archivos importantes de ese disco cuanto antes.", "StoragePage", "Ver discos");
    }

    // ---------- disks ----------
    static List<DiskHealth>? _disks;
    static DateTime _disksAt;

    /// <summary>Physical disks with SMART-style counters (temperature, wear, errors). Cached 1 min: it costs ~2 s.</summary>
    public static List<DiskHealth> DiskStatus(bool fresh = false)
    {
        if (!fresh && _disks != null && DateTime.Now - _disksAt < TimeSpan.FromMinutes(1)) return _disks;
        const string script =
            "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; [pscustomobject]@{" +
            "Name=$_.FriendlyName; Media=[string]$_.MediaType; Bus=[string]$_.BusType; Health=[string]$_.HealthStatus; Size=[long]$_.Size;" +
            "Letters=(($_ | Get-Disk | Get-Partition | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter }) -join ', ');" +
            "TempC=$r.Temperature; WearPct=$r.Wear; PowerOnHours=$r.PowerOnHours; ReadErrors=$r.ReadErrorsUncorrected; WriteErrors=$r.WriteErrorsUncorrected } } | ConvertTo-Json -Compress";
        var (_, json) = Store.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"{script}\"", 60_000);
        var list = new List<DiskHealth>();
        try
        {
            json = json.Trim();
            var start = json.IndexOfAny(['[', '{']);
            if (start >= 0)
            {
                using var doc = JsonDocument.Parse(json[start..]);
                var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
                foreach (var e in items)
                {
                    int? I(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                    long? L(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
                    string S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    // a temperature of 0 means "not reported", not a frozen disk
                    list.Add(new DiskHealth(S("Name"), S("Media"), S("Bus"), S("Health"), L("Size") ?? 0, S("Letters"),
                        I("TempC") is int t && t > 0 ? t : null, I("WearPct"), I("PowerOnHours"), L("ReadErrors"), L("WriteErrors")));
                }
            }
        }
        catch (Exception e) { Store.Log($"Disk health: {e.Message}"); }
        _disksAt = DateTime.Now;
        return _disks = list;
    }
}
