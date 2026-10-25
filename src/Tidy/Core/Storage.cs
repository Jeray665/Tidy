using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Tidy.Core;

public record DriveUsage(string Name, string Label, long Total, long Free)
{
    public long Used => Total - Free;
    public double Percent => Total > 0 ? Used * 100.0 / Total : 0;
}

public record FoundFile(string Path, long Size, DateTime Modified, bool Protected)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public enum FileKind { Document, Image, Archive, Video, Audio, Other }
public record MovedFile(string From, string To);

public record FolderSize(string Path, long Size);
public record DiskScan(List<FoundFile> Large, List<FolderSize> Folders, long Files, long Bytes);
public record DuplicateGroup(long Size, List<FoundFile> Files);

/// <summary>Storage like PC Manager: what takes space, big files, downloads, duplicates.
/// Nothing here deletes for good: files go to the Recycle Bin, and only after the user picks them.</summary>
public static class Storage
{
    static string Up(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    public static string Downloads => Path.Combine(Up(Environment.SpecialFolder.UserProfile), "Downloads");

    public static List<DriveUsage> Drives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .Select(d => new DriveUsage(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace)).ToList();

    /// <summary>Windows, programs and system files can be listed but never selected for deletion.</summary>
    public static bool IsProtected(string path, FileAttributes attr = 0)
    {
        if (attr.HasFlag(FileAttributes.System)) return true;
        foreach (var f in new[] { Up(Environment.SpecialFolder.Windows), Up(Environment.SpecialFolder.ProgramFiles), Up(Environment.SpecialFolder.ProgramFilesX86),
                                  Up(Environment.SpecialFolder.CommonApplicationData), Path.Combine(Up(Environment.SpecialFolder.LocalApplicationData), "Packages"),
                                  Store.SecureDir, Store.Dir })
            if (path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        var root = Path.GetPathRoot(path) ?? "";
        foreach (var name in new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "Windows.old" })
            if (path.StartsWith(Path.Combine(root, name) + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        // programs' own data (Chrome, VS Code, game launchers…): moving or deleting it breaks them. Their caches are Limpieza's job; Temp stays free.
        var local = Up(Environment.SpecialFolder.LocalApplicationData);
        if (path.StartsWith(Up(Environment.SpecialFolder.ApplicationData) + "\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(Path.Combine(local, "Temp") + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        // installed games: their big files are the game itself; moving or deleting one breaks it (uninstall them from Aplicaciones)
        foreach (var lib in GameLibraries)
            if (path.Contains(lib, StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetFileName(path).ToLowerInvariant() is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" or "dumpstack.log.tmp";
    }

    static readonly string[] GameLibraries =
        [@"\XboxGames\", @"\steamapps\", @"\Epic Games\", @"\Riot Games\", @"\EA Games\", @"\Ubisoft Game Launcher\", @"\GOG Galaxy\Games\", @"\WindowsApps\"];

    static readonly Dictionary<string, FileKind> Kinds = new[]
    {
        (FileKind.Document, ".pdf .doc .docx .xls .xlsx .ppt .pptx .odt .ods .odp .rtf .txt .csv .md .epub"),
        (FileKind.Image, ".jpg .jpeg .png .gif .bmp .webp .heic .tif .tiff .psd .svg .ico .raw .dng .cr2 .nef .arw"),
        (FileKind.Archive, ".zip .rar .7z .tar .gz .tgz .bz2 .xz .zst .cab .iso"),
        (FileKind.Video, ".mp4 .mkv .avi .mov .wmv .webm .flv .m4v .mpg .mpeg .ts .3gp"),
        (FileKind.Audio, ".mp3 .wav .flac .aac .ogg .m4a .wma .opus .aiff"),
    }.SelectMany(k => k.Item2.Split(' ').Select(ext => (ext, k.Item1))).ToDictionary(x => x.ext, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>By extension only: the file is never opened.</summary>
    public static FileKind Kind(string path) => Kinds.GetValueOrDefault(Path.GetExtension(path), FileKind.Other);

    static EnumerationOptions Opts(bool recurse) => new()
    {
        RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>One pass over a drive or folder: the biggest files and how much each folder (two levels deep) takes.</summary>
    public static DiskScan Scan(string root, long minSize, IProgress<string>? progress, CancellationToken ct)
    {
        var large = new PriorityQueue<FoundFile, long>();
        var folders = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long files = 0, bytes = 0;
        var rootLen = root.TrimEnd('\\').Length + 1;
        var sw = Stopwatch.StartNew();
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", Opts(true)))
        {
            ct.ThrowIfCancellationRequested();
            long len;
            try { len = f.Length; } catch { continue; }
            files++; bytes += len;
            // accumulate into first- and second-level folders
            var rel = f.FullName.Length > rootLen ? f.FullName[rootLen..] : "";
            var parts = rel.Split('\\');
            if (parts.Length > 1) Bump(folders, f.FullName[..(rootLen + parts[0].Length)], len);
            if (parts.Length > 2) Bump(folders, f.FullName[..(rootLen + parts[0].Length + 1 + parts[1].Length)], len);
            if (len >= minSize)
            {
                large.Enqueue(new FoundFile(f.FullName, len, f.LastWriteTime, IsProtected(f.FullName, f.Attributes)), len);
                if (large.Count > 3000) large.Dequeue();
            }
            if (sw.ElapsedMilliseconds > 250) { progress?.Report($"{files:N0} archivos · {Store.FormatBytes(bytes)} · {f.DirectoryName}"); sw.Restart(); }
        }
        var list = new List<FoundFile>();
        while (large.Count > 0) list.Add(large.Dequeue());
        list.Reverse();
        return new DiskScan(list, folders.Select(kv => new FolderSize(kv.Key, kv.Value)).OrderByDescending(f => f.Size).ToList(), files, bytes);
        static void Bump(Dictionary<string, long> d, string k, long v) => d[k] = d.GetValueOrDefault(k) + v;
    }

    public static List<FoundFile> DownloadedFiles() => Directory.Exists(Downloads)
        ? new DirectoryInfo(Downloads).EnumerateFiles("*", Opts(true))
            .Select(f => new FoundFile(f.FullName, f.Length, f.LastWriteTime, false))
            .OrderByDescending(f => f.Size).ToList()
        : [];

    public static string[] PersonalFolders() =>
        new[] { Downloads, Up(Environment.SpecialFolder.MyDocuments), Up(Environment.SpecialFolder.MyPictures), Up(Environment.SpecialFolder.MyVideos),
                Up(Environment.SpecialFolder.MyMusic), Up(Environment.SpecialFolder.Desktop) }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Same size → same first/last 64 KB → same full SHA-256. Only byte-identical files are grouped.</summary>
    public static List<DuplicateGroup> Duplicates(IEnumerable<string> roots, long minSize, IProgress<string>? progress, CancellationToken ct)
    {
        var bySize = new Dictionary<long, List<FileInfo>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", Opts(true)))
            {
                ct.ThrowIfCancellationRequested();
                if (f.Length < minSize || IsProtected(f.FullName, f.Attributes) || !seen.Add(f.FullName)) continue;
                (bySize.TryGetValue(f.Length, out var l) ? l : bySize[f.Length] = []).Add(f);
            }
        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var groups = new List<DuplicateGroup>();
        var done = 0;
        foreach (var (size, list) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Comparando {++done:N0} de {candidates.Count:N0} grupos de tamaño…");
            foreach (var quick in list.GroupBy(f => Hash(f.FullName, quick: true)).Where(g => g.Key != null && g.Count() > 1))
                foreach (var full in quick.GroupBy(f => Hash(f.FullName, quick: false)).Where(g => g.Key != null && g.Count() > 1))
                    groups.Add(new DuplicateGroup(size, full.OrderBy(f => f.CreationTime)
                        .Select(f => new FoundFile(f.FullName, f.Length, f.LastWriteTime, false)).ToList()));
        }
        return groups.OrderByDescending(g => g.Size * (g.Files.Count - 1)).ToList();
    }

    static string? Hash(string path, bool quick)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            if (!quick) return Convert.ToHexString(SHA256.HashData(s));
            var buf = new byte[1 << 17];
            var n = s.Read(buf, 0, 1 << 16);
            if (s.Length > 1 << 16) { s.Seek(-Math.Min(1 << 16, s.Length - n), SeekOrigin.End); n += s.Read(buf, n, 1 << 16); }
            return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n)));
        }
        catch { return null; }
    }

    /// <summary>To the Recycle Bin, never a permanent delete. Re-checks every path right before.</summary>
    public static (int Recycled, int Skipped) Recycle(IEnumerable<FoundFile> files)
    {
        var ok = files.Where(f => !f.Protected && !IsProtected(f.Path) && File.Exists(f.Path)
                                  && !File.GetAttributes(f.Path).HasFlag(FileAttributes.ReparsePoint)).ToList();
        var skipped = files.Count() - ok.Count;
        if (ok.Count == 0) return (0, skipped);
        Native.Recycle(ok.Select(f => f.Path));
        var gone = ok.Count(f => !File.Exists(f.Path));
        Store.Log($"Storage: {gone} files sent to the Recycle Bin ({Store.FormatBytes(ok.Where(f => !File.Exists(f.Path)).Sum(f => f.Size))})");
        foreach (var f in ok) Store.Log($"  recycled: {f.Path}");
        if (gone > 0) History.Add(HistoryKind.Files, $"{gone} archivos a la Papelera", Store.FormatBytes(ok.Where(f => !File.Exists(f.Path)).Sum(f => f.Size)), "recycle");
        return (gone, skipped + ok.Count - gone);
    }

    /// <summary>Moves the files into <paramref name="dest"/> (e.g. another disk), never overwriting: a clash gets " (2)".
    /// What went where is kept in the admin-only folder so Historial can put every file back.</summary>
    public static (int Moved, int Skipped) Move(IEnumerable<FoundFile> files, string dest, IProgress<string>? progress = null)
    {
        if (IsProtected(Path.Combine(dest, "x")) || !Directory.Exists(dest)) throw new IOException("Esa carpeta no vale como destino.");
        var moved = new List<MovedFile>();
        var list = files.ToList();
        long bytes = 0;
        foreach (var f in list)
        {
            if (f.Protected || IsProtected(f.Path) || !File.Exists(f.Path) || File.GetAttributes(f.Path).HasFlag(FileAttributes.ReparsePoint)) continue;
            if (string.Equals(Path.GetDirectoryName(f.Path), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            progress?.Report($"Moviendo {moved.Count + 1} de {list.Count}: {f.Name}");
            var to = Free(Path.Combine(dest, f.Name));
            try { File.Move(f.Path, to); moved.Add(new(f.Path, to)); bytes += f.Size; }
            catch (Exception e) { Store.Log($"Storage: couldn't move {f.Path}: {e.Message}"); }
        }
        if (moved.Count > 0)
        {
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            Directory.CreateDirectory(Store.MovesDir);
            File.WriteAllText(Path.Combine(Store.MovesDir, id + ".json"), System.Text.Json.JsonSerializer.Serialize(moved));
            foreach (var m in moved) Store.Log($"  moved: {m.From} -> {m.To}");
            History.Add(HistoryKind.Files, $"{moved.Count} archivos movidos a {dest}", Store.FormatBytes(bytes), $"moved:{id}");
        }
        return (moved.Count, list.Count - moved.Count);
    }

    static string Free(string path)
    {
        var (dir, name, ext) = (Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }

    public static List<MovedFile>? MoveRecord(string id)
    {
        var file = Path.Combine(Store.MovesDir, id + ".json");
        if (id.Contains('\\') || id.Contains('/') || id.Contains("..") || !File.Exists(file)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<MovedFile>>(File.ReadAllText(file)); } catch { return null; }
    }

    /// <summary>Moves that still have something to put back (for Historial's button).</summary>
    public static IEnumerable<string> Moves() => Directory.Exists(Store.MovesDir)
        ? Directory.EnumerateFiles(Store.MovesDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Where(id => MoveRecord(id)?.Any(m => File.Exists(m.To)) == true)
        : [];

    /// <summary>Puts each file back where it was. Skips (and keeps in the record) any whose old place is taken again.</summary>
    public static bool UndoMove(string id)
    {
        if (MoveRecord(id) is not { } moved) return false;
        var left = new List<MovedFile>();
        foreach (var m in moved)
        {
            if (!File.Exists(m.To)) continue;
            try
            {
                if (File.Exists(m.From) || IsProtected(m.From)) throw new IOException("su sitio original está ocupado");
                Directory.CreateDirectory(Path.GetDirectoryName(m.From)!);
                File.Move(m.To, m.From);
            }
            catch (Exception e) { Store.Log($"Storage: couldn't move back {m.To}: {e.Message}"); left.Add(m); }
        }
        var file = Path.Combine(Store.MovesDir, id + ".json");
        if (left.Count == 0) File.Delete(file);
        else File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(left));
        Store.Log($"Storage: move {id} undone, {left.Count} left");
        return left.Count == 0;
    }
}

public record ProcInfo(int Pid, string Name, string Description, string? Path, long Memory, double Cpu, bool Critical, int Count);

/// <summary>Process list with CPU %, memory and safe actions (close, priority, trim).</summary>
public static class Processes
{
    /// <summary>Closing these crashes or logs off Windows.</summary>
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso", "svchost", "fontdrvhost",
        "dwm", "Memory Compression", "MemCompression", "Secure System", "sihost", "MsMpEng", "NisSrv", "SecurityHealthService", "audiodg", "ctfmon",
    };

    static readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _prev = [];
    static readonly Dictionary<string, string> _desc = new(StringComparer.OrdinalIgnoreCase);
    // the page's timer, re-sorting and "close" can all ask at once; the CPU samples above aren't thread-safe
    static readonly object _gate = new();

    public static bool IsCritical(Process p) => Critical.Contains(p.ProcessName) || p.Id == Environment.ProcessId || p.SessionId == 0 && p.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Grouped by program (like Task Manager's apps view). CPU % is since the previous call.</summary>
    public static List<ProcInfo> List() { lock (_gate) return ListLocked(); }

    static List<ProcInfo> ListLocked()
    {
        var now = DateTime.UtcNow;
        var cores = Environment.ProcessorCount;
        var rows = new List<(int Pid, string Name, string? Path, long Mem, double Cpu, bool Critical)>();
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    alive.Add(p.Id);
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        if (_prev.TryGetValue(p.Id, out var prev)) cpu = (t - prev.Cpu).TotalMilliseconds / (now - prev.At).TotalMilliseconds / cores * 100;
                        _prev[p.Id] = (t, now);
                    }
                    catch { }
                    rows.Add((p.Id, p.ProcessName, Native.ProcessPath(p.Id), p.PrivateMemorySize64, Math.Max(0, cpu), IsCritical(p)));
                }
                catch { }
            }
        }
        foreach (var dead in _prev.Keys.Where(k => !alive.Contains(k)).ToList()) _prev.Remove(dead);
        return rows.GroupBy(r => r.Path ?? r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcInfo(g.First().Pid, g.First().Name, Describe(g.First().Path, g.First().Name), g.First().Path,
                                      g.Sum(r => r.Mem), g.Sum(r => r.Cpu), g.Any(r => r.Critical), g.Count()))
            .OrderByDescending(p => p.Memory).ToList();
    }

    static string Describe(string? path, string name)
    {
        if (path == null) return name;
        if (_desc.TryGetValue(path, out var d)) return d;
        try { d = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } fd ? fd : name; } catch { d = name; }
        return _desc[path] = d;
    }

    static IEnumerable<Process> Instances(ProcInfo info) =>
        Process.GetProcessesByName(info.Name).Where(p => string.Equals(Native.ProcessPath(p.Id), info.Path, StringComparison.OrdinalIgnoreCase));

    public static (int Closed, string Error) Close(ProcInfo info)
    {
        if (info.Critical) return (0, "Proceso del sistema: cerrarlo bloquearía Windows.");
        int n = 0; string err = "";
        foreach (var p in Instances(info))
            using (p)
            {
                if (IsCritical(p)) continue;
                try { p.Kill(); n++; } catch (Exception e) { err = e.Message; }
            }
        Store.Log($"Processes: closed {info.Name} ({n} instances)");
        return (n, err);
    }

    public static bool SetPriority(ProcInfo info, ProcessPriorityClass priority)
    {
        if (info.Critical || priority == ProcessPriorityClass.RealTime) return false;
        var ok = true;
        foreach (var p in Instances(info)) using (p) try { p.PriorityClass = priority; } catch { ok = false; }
        Store.Log($"Processes: {info.Name} priority -> {priority}");
        return ok;
    }

    /// <summary>Asks Windows to page out what the process isn't using. Harmless: it reloads on demand.</summary>
    public static int Trim(ProcInfo info) => Instances(info).Count(p => { using (p) return Native.TrimProcess(p.Id); });

    public static List<(int Pid, string App)> WhoLocks(string path) => Native.WhoLocks(path);
}
