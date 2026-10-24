using System.IO;
using System.Management;
using Microsoft.Win32;

namespace Tidy.Core;

public record HwItem(string Label, string Value);
public record HwSection(string Title, string Icon, string Headline, List<HwItem> Items);

public static class Hardware
{
    public static List<HwSection> Read()
    {
        var s = new List<HwSection>();

        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        s.Add(new("Procesador", "DeveloperBoard24", Str(cpu, "Name"),
        [
            new("Núcleos / hilos", $"{cpu?["NumberOfCores"]} / {cpu?["NumberOfLogicalProcessors"]}"),
            new("Frecuencia base", $"{Convert.ToDouble(cpu?["MaxClockSpeed"] ?? 0) / 1000:0.0#} GHz"),
        ]));

        foreach (var gpu in All("SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var name = Str(gpu, "Name");
            if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var items = new List<HwItem> { new("Driver", name.Contains("NVIDIA") ? $"{Drivers.NvidiaVersion(Str(gpu, "DriverVersion"))} ({Str(gpu, "DriverVersion")})" : Str(gpu, "DriverVersion")) };
            if (gpu["CurrentHorizontalResolution"] != null)
                items.Add(new("Pantalla", $"{gpu["CurrentHorizontalResolution"]}×{gpu["CurrentVerticalResolution"]} @ {gpu["CurrentRefreshRate"]} Hz"));
            if (name.Contains("NVIDIA") && SysMon.Gpu() is { } g) items.Add(new("Memoria de vídeo", $"{g.VramTotalGb:0} GB"));
            s.Add(new("Tarjeta gráfica", "Games24", name, items));
        }

        var mods = All("SELECT Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
        var totalGb = mods.Sum(m => Convert.ToDouble(m["Capacity"])) / (1L << 30);
        var speed = mods.Select(m => Convert.ToInt32(m["ConfiguredClockSpeed"] ?? m["Speed"] ?? 0)).DefaultIfEmpty(0).Max();
        s.Add(new("Memoria RAM", "Board24", $"{totalGb:0} GB a {speed} MT/s",
            mods.Select(m => new HwItem(Str(m, "DeviceLocator"),
                $"{Convert.ToDouble(m["Capacity"]) / (1L << 30):0} GB · {Str(m, "Manufacturer")} {Str(m, "PartNumber").Trim()}")).ToList()));

        var board = First("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var bios = First("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        s.Add(new("Placa base", "DeveloperBoard24", $"{Str(board, "Manufacturer")} {Str(board, "Product")}",
        [
            new("BIOS", Str(bios, "SMBIOSBIOSVersion")),
            new("Fecha BIOS", bios?["ReleaseDate"] is string bd ? ManagementDateTimeConverter.ToDateTime(bd).ToString("dd/MM/yyyy") : "—"),
        ]));

        var disks = new List<HwItem>();
        try
        {
            using var ds = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
            foreach (ManagementObject d in ds.Get())
            {
                var kind = Convert.ToInt32(d["BusType"]) == 17 ? "NVMe" : Convert.ToInt32(d["MediaType"]) switch { 4 => "SSD", 3 => "HDD", _ => "Disco" };
                var health = Convert.ToInt32(d["HealthStatus"]) switch { 0 => "Saludable", 1 => "Atención", 2 => "Mal estado", _ => "Desconocido" };
                disks.Add(new(Str(d, "FriendlyName"), $"{kind} · {Convert.ToDouble(d["Size"]) / 1e9:0} GB · {health}"));
            }
        }
        catch { }
        foreach (var v in DriveInfo.GetDrives().Where(v => v.DriveType == DriveType.Fixed && v.IsReady))
            disks.Add(new($"Unidad {v.Name.TrimEnd('\\')}", $"{Store.FormatBytes(v.AvailableFreeSpace)} libres de {Store.FormatBytes(v.TotalSize)}"));
        s.Add(new("Almacenamiento", "Storage24", $"{disks.Count(d => !d.Label.StartsWith("Unidad"))} discos", disks));

        var os = First("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        s.Add(new("Sistema", "Desktop24", Str(os, "Caption"),
        [
            new("Versión", $"{cv?.GetValue("DisplayVersion")} (compilación {Str(os, "BuildNumber")}.{cv?.GetValue("UBR")})"),
            new("Equipo", Environment.MachineName),
        ]));
        return s;
    }

    static List<ManagementObject> All(string q)
    {
        using var s = new ManagementObjectSearcher(q);
        return s.Get().Cast<ManagementObject>().ToList();
    }

    static ManagementObject? First(string q) => All(q).FirstOrDefault();
    static string Str(ManagementObject? o, string p) => o?[p]?.ToString()?.Trim() ?? "—";
}
