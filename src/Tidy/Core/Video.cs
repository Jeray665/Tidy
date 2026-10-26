using System.IO;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace Tidy.Core;

/// <param name="Mode">"mp4" (the video, compressed), "mp3" or "m4a" (only the sound).</param>
/// <param name="Quality">0 high, 1 balanced, 2 smallest. Video only.</param>
/// <param name="MaxShortSide">1080, 720, 480… (the "p" of the video, its short side); 0 keeps the size. Never enlarges.</param>
/// <param name="OutDir">Folder for the new files; null puts each next to its original.</param>
public record VideoJob(string Mode, int Quality, int MaxShortSide, string? OutDir);

/// <summary>
/// Compresses videos and pulls out their sound with Windows' own media engine (Media Foundation, the one the Photos and
/// Films apps use): nothing extra installed, the graphics card does the work when it can, and nothing runs until used.
/// Originals are never touched: every result is a new file, and Historial can send them all to the Recycle Bin.
/// </summary>
public static class Video
{
    public static readonly string[] Readable =
        [".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".asf", ".3gp", ".3g2", ".mts", ".m2ts", ".ts", ".webm", ".mpg", ".mpeg",
         ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac"];
    static readonly string[] SoundOnly = [".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac"];
    // bits per pixel per frame for H.264: high ~ the camera's own look, smallest still fine on a phone screen
    static readonly double[] BitsPerPixel = [0.10, 0.065, 0.04];

    public static bool IsMedia(string path) => Readable.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsSoundOnly(string path) => SoundOnly.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <param name="progress">Overall progress, 0 to 1.</param>
    /// <param name="each">Per file: its index and its own progress, 0 to 1.</param>
    public static async Task<List<CopyResult>> Process(IReadOnlyList<string> files, VideoJob job, IProgress<double>? progress = null,
        IProgress<(int Index, double Done)>? each = null, CancellationToken ct = default)
    {
        var results = new List<CopyResult>();
        var me = System.Diagnostics.Process.GetCurrentProcess();
        var priority = me.PriorityClass;
        // the PC stays usable while it works: games and the rest go first
        try { me.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var index = i;
                var mine = new Progress<double>(p => { each?.Report((index, p)); progress?.Report((index + p) / files.Count); });
                results.Add(await One(files[i], job, mine, ct));
                each?.Report((index, 1));
            }
        }
        finally
        {
            try { me.PriorityClass = priority; } catch { }
            var made = results.Where(r => r.To != null).ToList();
            Images.Remember(made, job.Mode == "mp4" ? $"{made.Count} vídeos comprimidos" : $"{made.Count} audios sacados a {job.Mode.ToUpperInvariant()}");
        }
        return results;
    }

    static async Task<CopyResult> One(string from, VideoJob job, IProgress<double> progress, CancellationToken ct)
    {
        long before = 0;
        string? to = null;
        try
        {
            var info = new FileInfo(from);
            if (!info.Exists) return new(from, null, 0, 0, "Ya no está.");
            before = info.Length;
            var dir = job.OutDir ?? info.DirectoryName!;
            if (Storage.IsProtected(Path.Combine(dir, "x"))) return new(from, null, before, 0, "Está en una carpeta de Windows o de un programa: elige otra carpeta de destino.");

            using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = await MediaEncodingProfile.CreateFromStreamAsync(input.AsRandomAccessStream()).AsTask(ct);
            var (profile, why) = Target(source, job);
            if (profile == null) return new(from, null, before, 0, why);

            to = Images.Free(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(from)} (Tidy).{job.Mode}"));
            using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.ReadWrite)) // CreateNew: never over an existing file
            {
                async Task<PrepareTranscodeResult> Prepare(bool graphicsCard)
                {
                    input.Position = 0;
                    output.SetLength(0);
                    var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = graphicsCard };
                    return await transcoder.PrepareStreamTranscodeAsync(input.AsRandomAccessStream(), output.AsRandomAccessStream(), profile).AsTask(ct);
                }
                // the graphics card first (fast, the PC stays free); some size changes it won't do, and then the processor does
                var prepared = await Prepare(true);
                if (!prepared.CanTranscode) prepared = await Prepare(false);
                if (!prepared.CanTranscode) { output.Dispose(); Discard(to); return new(from, null, before, 0, Reason(prepared.FailureReason, from)); }
                await prepared.TranscodeAsync().AsTask(ct, new Progress<double>(p => progress.Report(Math.Clamp(p / 100, 0, 1))));
            }
            var after = new FileInfo(to).Length;
            if (after == 0) { Discard(to); return new(from, null, before, 0, "Windows no pudo convertir este archivo."); }
            // already an MP4 and it would only grow: nothing gained, so no copy (from another format it's still worth it: MP4 plays everywhere)
            if (job.Mode == "mp4" && after >= before && Path.GetExtension(from).ToLowerInvariant() is ".mp4" or ".m4v")
            { Discard(to); return new(from, null, before, 0, "No se ha guardado copia: ya estaba muy comprimido y ocuparía más que el original."); }
            return new(from, to, before, after, after >= before && job.Mode == "mp4" ? "No ocupa menos: el original ya estaba muy comprimido." : null);
        }
        catch (OperationCanceledException) { Discard(to); throw; }
        catch (Exception e)
        {
            Discard(to);
            Store.Log($"Video: {from}: {e.Message}");
            return new(from, null, before, 0, e is UnauthorizedAccessException or IOException ? $"No se pudo leer o guardar: {e.Message}" : CodecMissing(from));
        }
    }

    /// <summary>The profile for the new file, or null with the reason.</summary>
    public static (MediaEncodingProfile? Profile, string? Why) Target(MediaEncodingProfile source, VideoJob job)
    {
        if (source.Audio == null && job.Mode != "mp4") return (null, "No tiene sonido.");
        if (job.Mode == "mp3") return (MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High), null);
        if (job.Mode == "m4a") return (MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High), null);
        if (source.Video is not { Width: > 0, Height: > 0 } v) return (null, "No tiene imagen: elige sacar solo el sonido.");

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        var (w, h) = Fit(v.Width, v.Height, job.MaxShortSide);
        var fps = v.FrameRate.Denominator > 0 && v.FrameRate.Numerator > 0 ? v.FrameRate.Numerator / (double)v.FrameRate.Denominator : 30;
        profile.Video.Width = w;
        profile.Video.Height = h;
        if (v.FrameRate.Denominator > 0 && v.FrameRate.Numerator > 0)
        {
            profile.Video.FrameRate.Numerator = v.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = v.FrameRate.Denominator;
        }
        profile.Video.PixelAspectRatio.Numerator = profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = Bitrate(w, h, fps, job.Quality, v.Bitrate);
        // phones store upright videos as sideways frames plus "turn 90°": the transcoder turns the frames itself (and swaps w/h)
        profile.Audio = source.Audio == null ? null
            : AudioEncodingProperties.CreateAac(source.Audio.SampleRate == 44100 ? 44100u : 48000u, Math.Clamp(source.Audio.ChannelCount, 1u, 2u), job.Quality == 0 ? 192000u : 128000u);
        return (profile, null);
    }

    /// <summary>The size with its short side at most maxShort (0: unchanged), even numbers as H.264 needs.</summary>
    public static (uint W, uint H) Fit(uint w, uint h, int maxShort)
    {
        var shortSide = Math.Min(w, h);
        var scale = maxShort > 0 && shortSide > maxShort ? maxShort / (double)shortSide : 1;
        return ((uint)Math.Max(2, Math.Round(w * scale / 2) * 2), (uint)Math.Max(2, Math.Round(h * scale / 2) * 2));
    }

    /// <summary>Bits per second for the new video: enough for its size and quality, and never more than the original had.</summary>
    public static uint Bitrate(uint w, uint h, double fps, int quality, uint original)
    {
        var wanted = w * (double)h * Math.Clamp(fps, 1, 120) * BitsPerPixel[Math.Clamp(quality, 0, 2)];
        if (original > 0) wanted = Math.Min(wanted, original * 0.85);
        return (uint)Math.Clamp(wanted, 300_000, 100_000_000);
    }

    static string Reason(TranscodeFailureReason r, string from) => r switch
    {
        TranscodeFailureReason.CodecNotFound => CodecMissing(from),
        TranscodeFailureReason.InvalidProfile => "Windows no admite este cambio con este archivo.",
        _ => "Windows no pudo convertir este archivo.",
    };

    static string CodecMissing(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" or ".mp4" or ".m4v" =>"Windows no puede abrirlo. Si es de un iPhone o un móvil reciente (HEVC), instala \"HEVC Video Extensions\" de Microsoft Store.",
        ".webm" or ".mkv" => "Windows no puede abrirlo: instala \"VP9 Video Extensions\" o \"AV1 Video Extension\" de Microsoft Store.",
        _ => "Windows no puede abrir este archivo.",
    };

    // only ever the file this run was writing: unfinished, nobody else has it
    static void Discard(string? path)
    {
        try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
    }
}
