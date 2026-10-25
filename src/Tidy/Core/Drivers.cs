using System.IO;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tidy.Core;

public class InstalledDriver
{
    public required string Device { get; init; }
    public required string Class { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string[] HardwareIds { get; init; } = [];
    public string DateText => Date?.ToString("dd/MM/yyyy") ?? "—";
    public bool Old => Date < DateTime.Now.AddYears(-3);
}

public class DriverUpdate
{
    public required string Device { get; init; }
    public required string Source { get; init; } // "Windows Update" | "NVIDIA"
    public string CurrentVersion { get; set; } = "—";
    public required string NewVersion { get; init; }
    public DateTime? NewDate { get; init; }
    public string Size { get; init; } = "";
    public string Class { get; init; } = "";
    public bool Selected { get; set; } = true;
    internal object? WuUpdate { get; init; }
    internal string? DownloadUrl { get; init; }
    public string NewDateText => NewDate?.ToString("dd/MM/yyyy") ?? "";
}

public static class Drivers
{
    static readonly string[] InterestingClasses =
        ["DISPLAY", "NET", "MEDIA", "BLUETOOTH", "SCSIADAPTER", "HDC", "SYSTEM", "USB", "HIDCLASS", "MONITOR", "CAMERA", "IMAGE", "BIOMETRIC", "SOFTWARECOMPONENT"];

    public static List<InstalledDriver> Inventory()
    {
        using var s = new ManagementObjectSearcher("SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate, HardWareID, DriverProviderName FROM Win32_PnPSignedDriver");
        var list = new List<InstalledDriver>();
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var name = o["DeviceName"] as string;
                var cls = (o["DeviceClass"] as string ?? "").ToUpperInvariant();
                var provider = o["DriverProviderName"] as string ?? "";
                if (string.IsNullOrWhiteSpace(name) || !InterestingClasses.Contains(cls)) continue;
                // inbox Microsoft drivers are updated by Windows itself; hiding them keeps the list readable
                if (provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime? date = o["DriverDate"] is string d && d.Length >= 8 ? ManagementDateTimeConverter.ToDateTime(d) : null;
                list.Add(new InstalledDriver
                {
                    Device = name.Trim(), Class = ClassName(cls), Manufacturer = o["Manufacturer"] as string ?? provider,
                    Version = o["DriverVersion"] as string ?? "", Date = date,
                    HardwareIds = o["HardWareID"] is string hw ? [hw] : [],
                });
            }
        }
        return list.DistinctBy(d => (d.Device, d.Version)).OrderBy(d => d.Class).ThenBy(d => d.Device).ToList();
    }

    public static string ClassName(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" => "Gráficos", "NET" => "Red", "MEDIA" => "Audio", "BLUETOOTH" => "Bluetooth",
        "SCSIADAPTER" or "HDC" => "Almacenamiento", "SYSTEM" => "Chipset y sistema", "USB" => "USB",
        "HIDCLASS" => "Periféricos", "MONITOR" => "Monitores", "CAMERA" or "IMAGE" => "Cámaras",
        "BIOMETRIC" => "Biometría", "SOFTWARECOMPONENT" => "Componentes", "OTHERHARDWARE" => "Otros", "FIRMWARE" => "Firmware",
        "PRINTER" => "Impresoras", "KEYBOARD" => "Teclados", "MOUSE" => "Ratones", _ => cls,
    };

    // ---------- Windows Update (Microsoft-signed, WHQL drivers only) ----------
    public static List<DriverUpdate> SearchWindowsUpdate(List<InstalledDriver> installed)
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        session.ClientApplicationID = "Tidy";
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        var list = new List<DriverUpdate>();
        foreach (dynamic u in result.Updates)
        {
            string provider = u.DriverProvider ?? "", cls = u.DriverClass ?? "";
            // NVIDIA display drivers come straight from NVIDIA (newer); WU's copy is usually older.
            if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase) && provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
            string hwid = u.DriverHardwareID ?? "";
            var match = installed.FirstOrDefault(d => d.HardwareIds.Any(h => h.Equals(hwid, StringComparison.OrdinalIgnoreCase)));
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch { }
            list.Add(new DriverUpdate
            {
                Device = match?.Device ?? (string)(u.DriverModel ?? u.Title),
                Source = "Windows Update", Class = ClassName(cls),
                CurrentVersion = match?.Version ?? "—",
                NewVersion = VersionFromTitle((string)u.Title) ?? date?.ToString("dd/MM/yyyy") ?? "nueva",
                NewDate = date, Size = Store.FormatBytes((long)(decimal)u.MaxDownloadSize), WuUpdate = u,
            });
        }
        Store.Log($"Drivers: Windows Update offers {list.Count} updates.");
        return list;
    }

    static string? VersionFromTitle(string title) => Regex.Match(title, @"\d+(\.\d+){2,}") is { Success: true } m ? m.Value : null;

    /// <summary>Downloads and installs through the Windows Update agent itself. Returns true if a reboot is needed.</summary>
    public static bool InstallWindowsUpdate(IEnumerable<DriverUpdate> updates, Action<string> progress)
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        session.ClientApplicationID = "Tidy";
        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        foreach (var u in updates)
        {
            dynamic wu = u.WuUpdate!;
            if (!(bool)wu.EulaAccepted) wu.AcceptEula(); // driver EULAs are the vendor's WHQL terms
            coll.Add(wu);
        }
        if ((int)coll.Count == 0) return false;
        progress("Descargando desde Windows Update…");
        dynamic dl = session.CreateUpdateDownloader();
        dl.Updates = coll;
        dl.Download();
        progress("Instalando drivers…");
        dynamic inst = session.CreateUpdateInstaller();
        inst.Updates = coll;
        dynamic r = inst.Install();
        Store.Log($"Drivers: Windows Update install -> ResultCode {(int)r.ResultCode}, reboot {(bool)r.RebootRequired}");
        return (bool)r.RebootRequired;
    }

    // ---------- NVIDIA (official nvidia.com APIs + Authenticode check) ----------
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>"32.0.16.1742" -> "617.42": last 5 digits of the last two fields.</summary>
    public static string NvidiaVersion(string wmiVersion)
    {
        var parts = wmiVersion.Split('.');
        if (parts.Length < 4) return wmiVersion;
        var digits = (parts[2] + parts[3])[^5..];
        return $"{int.Parse(digits[..3])}.{digits[3..]}";
    }

    public static async Task<DriverUpdate?> CheckNvidia()
    {
        using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
        var gpu = s.Get().Cast<ManagementObject>().FirstOrDefault(o => (o["Name"] as string ?? "").Contains("NVIDIA"));
        if (gpu == null) return null;
        string name = (string)gpu["Name"], current = NvidiaVersion((string)gpu["DriverVersion"]);

        var xml = await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3");
        var m = Regex.Match(xml, $@"<LookupValue[^>]*ParentID=""(\d+)""[^>]*>\s*<Name>\s*{Regex.Escape(name)}\s*</Name>\s*<Value>(\d+)</Value>");
        if (!m.Success)
        {
            var at = xml.IndexOf("RTX", StringComparison.Ordinal);
            Store.Log($"Drivers: GPU '{name}' not found in the NVIDIA catalog. ({xml.Length} bytes) {(at >= 0 ? xml.Substring(at, Math.Min(150, xml.Length - at)) : xml[..Math.Min(150, xml.Length)])}");
            return null;
        }
        string psid = m.Groups[1].Value, pfid = m.Groups[2].Value;

        var json = await Http.GetStringAsync($"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1");
        using var doc = JsonDocument.Parse(json);
        var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
        var latest = info.GetProperty("Version").GetString()!;
        var url = info.GetProperty("DownloadURL").GetString()!;
        DateTime? date = DateTime.TryParse(info.GetProperty("ReleaseDateTime").GetString()?[4..], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        Store.Log($"Drivers: NVIDIA {name} installed {current}, latest WHQL {latest}");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (decimal.Parse(latest, inv) <= decimal.Parse(current, inv)) return null;
        return new DriverUpdate
        {
            Device = name, Source = "NVIDIA", Class = "Gráficos", CurrentVersion = current, NewVersion = latest,
            NewDate = date, Size = "~900 MB", DownloadUrl = url,
        };
    }

    /// <summary>Downloads the installer from NVIDIA's CDN and refuses to return it unless it's validly signed by NVIDIA.</summary>
    public static async Task<string> DownloadNvidia(DriverUpdate u, IProgress<double> progress, CancellationToken ct)
    {
        var uri = new Uri(u.DownloadUrl!);
        if (uri.Scheme != "https" || !uri.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URL de descarga no oficial rechazada: {uri.Host}");
        // admin-only folder (Program Files): nothing running as the normal user can swap the file after we verify it
        var dir = Store.DownloadsDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(uri.LocalPath));

        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[1 << 20]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(done * 100.0 / total);
            }
        }
        catch
        {
            // cancelled or cut off: no half-downloaded 900 MB file left behind
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }

        if (!IsSignedBy(path, "NVIDIA Corporation"))
        {
            File.Delete(path);
            Store.Log($"Drivers: INVALID signature on {path}! Download deleted.");
            throw new InvalidOperationException("El instalador descargado no tiene una firma válida de NVIDIA. Se ha borrado por seguridad.");
        }
        Store.Log($"Drivers: {path} downloaded and verified (valid NVIDIA Corporation signature).");
        return path;
    }

    /// <summary>Re-verifies while holding a no-write/no-delete lock, then launches it directly (no shell associations).</summary>
    public static void RunVerifiedInstaller(string path, string organization)
    {
        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsSignedBy(path, organization)) throw new InvalidOperationException("La firma del instalador ya no es válida. No se ejecuta.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        Store.Log($"Drivers: started verified installer {path}");
    }

    public static bool IsSignedBy(string path, string organization)
    {
        if (!Native.HasValidSignature(path)) return false;
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return cert.GetNameInfo(X509NameType.SimpleName, false).Contains(organization, StringComparison.OrdinalIgnoreCase)
            || cert.Subject.Contains($"O={organization}", StringComparison.OrdinalIgnoreCase);
    }
}
