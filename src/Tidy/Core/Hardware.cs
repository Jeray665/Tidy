using System.IO;
using System.Management;
using System.Runtime.InteropServices;
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

        foreach (var gpu in All("SELECT Name, DriverVersion FROM Win32_VideoController"))
        {
            var name = Str(gpu, "Name");
            if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            var items = new List<HwItem> { new("Driver", name.Contains("NVIDIA") ? $"{Drivers.NvidiaVersion(Str(gpu, "DriverVersion"))} ({Str(gpu, "DriverVersion")})" : Str(gpu, "DriverVersion")) };
            if (name.Contains("NVIDIA") && SysMon.Gpu() is { } g) items.Add(new("Memoria de vídeo", $"{g.VramTotalGb:0} GB"));
            s.Add(new("Tarjeta gráfica", "Games24", name, items));
        }

        // Win32_VideoController only knows one mode per card, so 3 monitors on one GPU showed as 1.
        var screens = Displays();
        if (screens.Count > 0)
            s.Add(new("Pantallas", "DesktopMac24", screens.Count == 1 ? "1 pantalla" : $"{screens.Count} pantallas", screens));

        var mods = All("SELECT Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
        var totalGb = mods.Sum(m => Convert.ToDouble(m["Capacity"])) / (1L << 30);
        var speed = mods.Select(m => Convert.ToInt32(m["ConfiguredClockSpeed"] ?? m["Speed"] ?? 0)).DefaultIfEmpty(0).Max();
        s.Add(new("Memoria RAM", "Board24", $"{totalGb:0} GB · {speed} MT/s",
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

    public static List<HwItem> Displays()
    {
        var names = new List<(string Id, string Name)>();
        try
        {
            using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
            foreach (ManagementObject m in q.Get())
                if (m["UserFriendlyName"] is ushort[] u && new string(u.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() is { Length: > 0 } n)
                    names.Add((Str(m, "InstanceName"), n));
        }
        catch { }

        var list = new List<HwItem>();
        var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref ad, 0); i++, ad.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((ad.StateFlags & 1) == 0) continue;   // not part of the desktop
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(ad.DeviceName, -1, ref dm)) continue;   // ENUM_CURRENT_SETTINGS
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            var name = EnumDisplayDevices(ad.DeviceName, 0, ref mon, 1) ? MonitorName(mon, names) : null;
            var primary = (ad.StateFlags & 4) != 0;
            list.Add(new(name ?? $"Pantalla {list.Count + 1}",
                $"{dm.dmPelsWidth}×{dm.dmPelsHeight} · {dm.dmDisplayFrequency} Hz{(primary ? " · " + "principal" : "")}"));
        }
        return list.OrderByDescending(d => d.Value.EndsWith("principal")).ToList();
    }

    // \\?\DISPLAY#GSM5B7F#5&1a2b&0&UID4352#{guid} matches WMI's DISPLAY\GSM5B7F\5&1a2b&0&UID4352_0
    static string? MonitorName(DISPLAY_DEVICE mon, List<(string Id, string Name)> names)
    {
        var id = mon.DeviceID ?? "";
        var start = id.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
        var end = id.LastIndexOf("#{", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var key = id[start..end].Replace('#', '\\');
            foreach (var n in names)
                if (n.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return n.Name;
        }
        return string.IsNullOrWhiteSpace(mon.DeviceString) || mon.DeviceString.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) ? null : mon.DeviceString;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

    static List<ManagementObject> All(string q)
    {
        using var s = new ManagementObjectSearcher(q);
        return s.Get().Cast<ManagementObject>().ToList();
    }

    static ManagementObject? First(string q) => All(q).FirstOrDefault();
    static string Str(ManagementObject? o, string p) => o?[p]?.ToString()?.Trim() ?? "—";
}
