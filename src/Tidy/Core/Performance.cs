using System.Runtime.InteropServices;

namespace Tidy.Core;

public record GpuStat(uint TempC, uint UsagePct, double VramUsedGb, double VramTotalGb, uint? FanPct, double? PowerW);

/// <summary>Cheap live readings. Every call is a couple of syscalls; no WMI, no perf counters.</summary>
public static class SysMon
{
    static long _idle, _kernel, _user;
    static readonly object _cpuLock = new();

    /// <summary>CPU busy % since the previous call (first call returns 0).</summary>
    public static double Cpu()
    {
        lock (_cpuLock)
        {
            if (!Native.GetSystemTimes(out var i, out var k, out var u)) return 0;
            long di = i - _idle, dk = k - _kernel, du = u - _user; // kernel time includes idle
            bool first = _kernel == 0;
            (_idle, _kernel, _user) = (i, k, u);
            var total = dk + du;
            return first || total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
        }
    }

    public static (int Pct, double UsedGb, double TotalGb) Ram()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        const double gb = 1L << 30;
        return ((int)m.dwMemoryLoad, (m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    static IntPtr _gpu;
    static bool _nvmlTried;

    /// <summary>First NVIDIA GPU via NVML, or null when there's no NVIDIA driver.</summary>
    public static GpuStat? Gpu()
    {
        try
        {
            if (!_nvmlTried)
            {
                _nvmlTried = true;
                if (Native.nvmlInit_v2() != 0 || Native.nvmlDeviceGetHandleByIndex_v2(0, out _gpu) != 0) _gpu = IntPtr.Zero;
            }
            if (_gpu == IntPtr.Zero) return null;
            Native.nvmlDeviceGetTemperature(_gpu, 0, out var t);
            Native.nvmlDeviceGetUtilizationRates(_gpu, out var u);
            Native.nvmlDeviceGetMemoryInfo(_gpu, out var mem);
            uint? fan = Native.nvmlDeviceGetFanSpeed(_gpu, out var f) == 0 ? f : null;
            double? watts = Native.nvmlDeviceGetPowerUsage(_gpu, out var mw) == 0 ? mw / 1000.0 : null;
            const double gb = 1L << 30;
            return new GpuStat(t, u.Gpu, mem.Used / gb, mem.Total / gb, fan, watts);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _gpu = IntPtr.Zero;
            return null;
        }
    }
}

public record Snapshot(double Cpu, int RamPct, double RamUsedGb, double RamTotalGb, GpuStat? Gpu);

/// <summary>
/// The one background timer. Samples every 5 s while only the tray is alive and every 1 s while a window shows live data.
/// Also drives the automatic RAM purge.
/// </summary>
public static class Live
{
    public static Snapshot? Last { get; private set; }
    public static event Action<Snapshot>? Updated;
    static Timer? _timer;
    static int _watchers, _busy;

    public static void Start() => _timer = new Timer(_ => Tick(), null, 0, 5000);

    /// <summary>Call with true when a live view opens and false when it closes.</summary>
    public static void Watch(bool on)
    {
        var n = Interlocked.Add(ref _watchers, on ? 1 : -1);
        _timer?.Change(on ? 0 : 5000, n > 0 ? 1000 : 5000);
    }

    static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var ram = SysMon.Ram();
            Last = new Snapshot(SysMon.Cpu(), ram.Pct, ram.UsedGb, ram.TotalGb, SysMon.Gpu());
            Updated?.Invoke(Last);
            Memory.AutoTick();
            GameMode.Tick();
            CleanReminder.Tick();
        }
        catch (Exception e) { Store.Log($"Monitor: {e.Message}"); }
        finally { _busy = 0; }
    }
}

/// <summary>
/// Standby-list purge, the same thing ISLC / RAMMap "Empty Standby List" do. Standby pages are clean file cache,
/// so dropping them never loses data; worst case Windows re-reads a file from disk. We never touch process
/// working sets or kill anything.
/// </summary>
public static class Memory
{
    static DateTime _lastAuto = DateTime.MinValue;
    static readonly object _lock = new();

    public static (long StandbyMb, long FreeMb) Lists()
    {
        var buf = new ulong[22];
        if (Native.NtQuerySystemInformation(Native.SystemMemoryListInformation, buf, buf.Length * 8, out _) != 0) return (0, 0);
        long page = Environment.SystemPageSize;
        ulong standby = 0;
        for (int i = 5; i <= 12; i++) standby += buf[i];
        return ((long)standby * page >> 20, (long)(buf[0] + buf[1]) * page >> 20);
    }

    /// <summary>Returns MB reclaimed, or -1 if Windows refused (not elevated).</summary>
    public static long PurgeStandby(string reason)
    {
        lock (_lock)
        {
            var before = Lists().StandbyMb;
            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            int cmd = Native.MemoryPurgeStandbyList;
            var status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ref cmd, sizeof(int));
            if (status != 0)
            {
                Store.Log($"RAM: purge refused by Windows (NTSTATUS 0x{status:X8}). Not running as administrator?");
                return -1;
            }
            var freed = Math.Max(0, before - Lists().StandbyMb);
            Store.Log($"RAM: freed {freed} MB of standby memory ({reason}).");
            return freed;
        }
    }

    /// <summary>Called every few seconds from the tray. Cheap when nothing to do.</summary>
    public static void AutoTick()
    {
        var s = Store.Settings;
        if (!s.AutoRam || DateTime.Now - _lastAuto < TimeSpan.FromMinutes(2)) return;
        var (standby, free) = Lists();
        if (free >= s.FreeThresholdMb || standby < s.MinStandbyMb) return;
        _lastAuto = DateTime.Now;
        PurgeStandby($"automático: libre {free} MB < {s.FreeThresholdMb} MB");
    }

    /// <summary>Gives our own idle memory back to Windows after closing the big window. Only touches Tidy itself.</summary>
    public static void TrimSelf()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }
}

/// <summary>
/// Scheduled cleanup. By default it only tells the user when there's a lot to clean (cleaning still goes through the confirmation list).
/// With AutoClean the user chose, once and with the list in front of them, to let it clean the chosen categories by itself:
/// every run saves the exact list of what was deleted and says so in a notification. The recycle bin is never emptied this way.
/// </summary>
public static class CleanReminder
{
    public static event Action<long>? Due;
    public static event Action<Cleaner.CleanReport>? Cleaned;
    static DateTime _lastCheck = DateTime.Now; // first look an hour after start, never during boot
    static int _running;

    /// <summary>The categories the schedule covers. Automatic cleaning never includes the recycle bin: your own files are in it.</summary>
    public static List<CleanCategory> Scheduled(bool auto) =>
        Cleaner.Categories.Where(c => (Store.Settings.CleanCategories?.Contains(c.Id) ?? c.DefaultOn) && !(auto && c.RecycleBin)).ToList();

    public static void Tick()
    {
        var s = Store.Settings;
        if (s.CleanReminderDays <= 0 || DateTime.Now - _lastCheck < TimeSpan.FromHours(1)) return;
        _lastCheck = DateTime.Now;
        var period = TimeSpan.FromDays(s.CleanReminderDays);
        if (s.LastClean is { } c && DateTime.Now - c < period) return;
        if (s.LastCleanReminder is { } r && DateTime.Now - r < period) return; // one reminder (or automatic run) per period, no nagging
        if (GameMode.Current != null) return; // never while playing
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        s.LastCleanReminder = DateTime.Now;
        Store.Save();
        var auto = s.AutoClean;
        // off the monitor's timer: scanning and deleting can take a while
        Task.Run(() =>
        {
            try
            {
                var scans = Scheduled(auto).Select(Cleaner.Scan).ToList();
                var bytes = scans.Sum(x => x.Bytes);
                Store.Log($"Scheduled cleanup ({(auto ? "automatic" : "reminder")}): {Store.FormatBytes(bytes)} can be freed.");
                if (!auto) { if (bytes >= 300L << 20) Due?.Invoke(bytes); return; }
                if (bytes == 0 || GameMode.Current != null) return;
                var report = Cleaner.Clean(scans, automatic: true);
                s.LastClean = DateTime.Now;
                s.LastCleanBytes = report.Bytes;
                Store.Save();
                Cleaned?.Invoke(report);
            }
            catch (Exception e) { Store.Log($"Scheduled cleanup failed: {e.Message}"); }
            finally { _running = 0; }
        });
    }
}

public record PowerPlan(Guid Id, string Name, bool Active)
{
    public static readonly Guid Saver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid High = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    // Fixed destination GUID so we never create duplicate "Ultimate" copies.
    public static readonly Guid Ultimate = new("7d1a5e3c-9b4f-4c2e-8a6d-2f0b1c3d4e5f");

    public static List<PowerPlan> All()
    {
        Native.PowerGetActiveScheme(IntPtr.Zero, out var p);
        var active = Marshal.PtrToStructure<Guid>(p);
        Native.LocalFree(p);
        var list = new List<PowerPlan>();
        for (uint i = 0; ; i++)
        {
            var g = Guid.Empty; uint size = 16;
            if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /*ACCESS_SCHEME*/, i, ref g, ref size) != 0) break;
            list.Add(new PowerPlan(g, FriendlyName(g), g == active));
        }
        return list;
    }

    static string FriendlyName(Guid g)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size);
            return Marshal.PtrToStringUni(buf) ?? g.ToString();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static bool IsUltimate(PowerPlan p) =>
        p.Id == Ultimate || p.Name.Contains("Máximo rendimiento", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase);

    public static bool Activate(Guid id, bool record = true)
    {
        var previous = record ? All().FirstOrDefault(p => p.Active) : null;
        if (id == Ultimate)
        {
            // reuse a copy other tools (or Windows) already made instead of piling up duplicates
            var existing = All().FirstOrDefault(IsUltimate);
            if (existing != null) id = existing.Id;
            else
            {
                var (code, output) = Store.Run("powercfg.exe", $"-duplicatescheme {UltimateTemplate} {Ultimate}");
                Store.Log($"Power: create Ultimate Performance plan -> {code} {output.Trim()}");
                if (code != 0) return false;
            }
        }
        var ok = Native.PowerSetActiveScheme(IntPtr.Zero, ref id) == 0;
        Store.Log($"Power: activate plan {id} -> {(ok ? "OK" : "error")}");
        if (ok && previous != null && previous.Id != id)
            History.Add(HistoryKind.Power, $"Plan de energía: {FriendlyName(id)}", $"Antes: {previous.Name}", $"power:{previous.Id}");
        if (ok) PowerTimeouts.ApplySaved();   // the user's "never sleep" etc. follows them to whatever plan is active
        return ok;
    }

    public static Guid ActiveId()
    {
        Native.PowerGetActiveScheme(IntPtr.Zero, out var p);
        var g = Marshal.PtrToStructure<Guid>(p);
        Native.LocalFree(p);
        return g;
    }
}

public record PowerTimeout(string Id, string Label, string Description, Guid Sub, Guid Setting);

/// <summary>Screen off / sleep / hibernate after N minutes of inactivity (0 = never), on the active plan, plugged in and on
/// battery alike. The choice is saved and re-applied whenever Tidy switches plans (profiles, game mode).</summary>
public static class PowerTimeouts
{
    static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99"), SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly PowerTimeout[] All =
    [
        new("display", "Apagar la pantalla", "Tras este tiempo sin tocar el ratón ni el teclado.", SubVideo, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")),
        new("sleep", "Suspender el PC", "El PC se queda en reposo (los juegos y descargas se pausan).", SubSleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")),
        new("hibernate", "Hibernar", "Guarda todo en el disco y apaga. Tarda más en volver.", SubSleep, new("9d7815a6-7ee4-497e-8888-515a05f02364")),
    ];

    public static bool HibernateAvailable =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 0) is int h && h != 0;

    /// <summary>Laptops and tablets. Desktops have no battery (flag 128), so the "on battery" value never applies there.</summary>
    public static bool HasBattery => Native.GetSystemPowerStatus(out var s) && (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;

    public static (uint Ac, uint Dc) Read(PowerTimeout t)
    {
        var (scheme, sub, setting) = (PowerPlan.ActiveId(), t.Sub, t.Setting);
        Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac);
        Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc);
        return (ac, dc);
    }

    /// <summary>Seconds, 0 = never. Records the previous values in History so it can be undone.</summary>
    public static bool Write(PowerTimeout t, uint ac, uint dc, bool record = true)
    {
        var old = Read(t);
        var (scheme, sub, setting) = (PowerPlan.ActiveId(), t.Sub, t.Setting);
        var ok = Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, ac) == 0
              && Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, dc) == 0
              && Native.PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;   // re-activating applies the new values now
        Store.Log($"Power: {t.Id} -> {ac}s AC / {dc}s DC (was {old.Ac}/{old.Dc}) {(ok ? "OK" : "error")}");
        if (ok && record && old != (ac, dc))
            History.Add(HistoryKind.Power, $"{t.Label}: {Describe(ac)}", $"Antes: {Describe(old.Ac)}", $"powertime:{t.Id}:{old.Ac}:{old.Dc}");
        return ok;
    }

    public static void ApplySaved()
    {
        foreach (var t in All)
            if (Store.Settings.PowerTimeouts.TryGetValue(t.Id, out var secs) && Read(t) != ((uint)secs, (uint)secs))
                Write(t, (uint)secs, (uint)secs, record: false);
    }

    public static string Describe(uint secs) => secs switch
    {
        0 => "nunca",
        < 60 => $"{secs} s",
        < 3600 => $"{secs / 60} min",
        _ => secs % 3600 == 0 ? $"{secs / 3600} h" : $"{secs / 3600} h {secs % 3600 / 60} min",
    };
}
