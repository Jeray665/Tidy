using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tidy.Core;

/// <param name="Format">"jpg", "png", "bmp", "tiff", "gif", "ico", or "" to keep each image's own format.</param>
/// <param name="Quality">JPG quality, 1-100.</param>
/// <param name="MaxSide">Longest side in pixels after resizing; 0 keeps the size. Never enlarges.</param>
/// <param name="StripData">Leave out GPS, camera, dates and the rest of the hidden data.</param>
/// <param name="OutDir">Folder for the new files; null puts each next to its original.</param>
public record ImageJob(string Format, int Quality, int MaxSide, bool StripData, string? OutDir);

/// <param name="To">The new file, or null if this one was skipped (Note says why). Images and videos alike.</param>
public record CopyResult(string From, string? To, long Before, long After, string? Note);

/// <summary>
/// Converts, compresses and resizes images with Windows' own codecs (WIC, what Explorer uses for thumbnails): nothing extra to
/// install, nothing running until used. Originals are never touched: every result is a new file, and Historial can send them
/// all to the Recycle Bin.
/// </summary>
public static class Images
{
    public static readonly string[] Readable =
        [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".heic", ".heif", ".webp", ".avif", ".jxr", ".wdp"];
    public static readonly string[] Writable = ["jpg", "png", "bmp", "tiff", "gif", "ico"];
    const long MaxPixels = 200_000_000; // ~800 MB decoded; bigger ones are skipped instead of exhausting memory
    static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    public static bool IsImage(string path) => Readable.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The format a file ends up in: the chosen one, or its own (HEIC, WebP and AVIF can't be written, so JPG).</summary>
    public static string TargetFormat(string path, string format)
    {
        if (format != "") return format;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "png", ".bmp" or ".dib" => "bmp", ".gif" => "gif", ".tif" or ".tiff" => "tiff", ".ico" => "ico", _ => "jpg",
        };
    }

    public static List<CopyResult> Process(IReadOnlyList<string> files, ImageJob job, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var results = new CopyResult?[files.Count];
        var done = 0;
        try
        {
            // a few at a time: fast on big batches, and the rest of the PC stays responsive
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = ct }, i =>
            {
                results[i] = One(files[i], job);
                progress?.Report(Interlocked.Increment(ref done));
            });
        }
        finally
        {
            // also when cancelled: the copies already made stay undoable
            var made = results.Where(r => r?.To != null).Select(r => r!).ToList();
            Remember(made,job.Format == "" ? $"{made.Count} imágenes optimizadas" : $"{made.Count} imágenes convertidas a {job.Format.ToUpperInvariant()}");
        }
        return [.. results.Select(r => r!)]; // all filled when not cancelled
    }

    /// <summary>Writes down the new files of a batch (images or videos) so Historial can send them to the Recycle Bin.</summary>
    public static void Remember(IReadOnlyList<CopyResult> made, string title)
    {
        if (made.Count == 0) return;
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        Directory.CreateDirectory(Store.CreatedDir);
        File.WriteAllText(Path.Combine(Store.CreatedDir, id + ".json"), System.Text.Json.JsonSerializer.Serialize(made.Select(r => r.To!).ToList()));
        foreach (var r in made) Store.Log($"  copy: {r.From} -> {r.To}");
        var saved = made.Sum(r => r.Before - r.After);
        History.Add(HistoryKind.Files, title, saved > 0 ? $"{Store.FormatBytes(saved)} menos que los originales" : "", $"created:{id}");
    }

    static CopyResult One(string from, ImageJob job)
    {
        long before = 0;
        try
        {
            var info = new FileInfo(from);
            if (!info.Exists) return new(from, null, 0, 0, "Ya no está.");
            before = info.Length;
            var format = TargetFormat(from, job.Format);
            var dir = job.OutDir ?? info.DirectoryName!;
            if (Storage.IsProtected(Path.Combine(dir, "x"))) return new(from, null, before, 0, "Está en una carpeta de Windows o de un programa: elige otra carpeta de destino.");

            byte[]? bytes;
            string? note;
            using (var stream = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                bytes = Encode(stream, format, job, out note);
            if (bytes == null) return new(from, null, before, 0, note);
            var to = Free(Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(from)} (Tidy).{format}"));
            using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write)) output.Write(bytes); // CreateNew: never over an existing file
            return new(from, to, before, bytes.Length, note);
        }
        catch (NotSupportedException) { return new(from, null, before, 0, CodecMissing(from)); }
        catch (FileFormatException) { return new(from, null, before, 0, "El archivo está dañado o no es una imagen."); }
        catch (Exception e)
        {
            Store.Log($"Images: {from}: {e.Message}");
            return new(from, null, before, 0, e is UnauthorizedAccessException or IOException ? $"No se pudo leer o guardar: {e.Message}" : CodecMissing(from));
        }
    }

    static string CodecMissing(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".heic" or ".heif" => "Windows no puede abrir HEIC: instala la extensión gratuita \"HEIF Image Extensions\" de Microsoft Store.",
        ".webp" => "Windows no puede abrir WebP: instala la extensión gratuita \"WebP Image Extensions\" de Microsoft Store.",
        ".avif" => "Windows no puede abrir AVIF: instala la extensión gratuita \"AV1 Video Extension\" de Microsoft Store.",
        _ => "Windows no puede abrir esta imagen.",
    };

    /// <summary>The new image's bytes, or null with the reason.</summary>
    public static byte[]? Encode(Stream source, string format, ImageJob job, out string? note)
    {
        note = null;
        // colour profiles applied (an iPhone's Display P3 photo keeps its colours once the profile isn't there any more)
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count > 1 && decoder is not IconBitmapDecoder)
        { note = "Tiene varias imágenes dentro (GIF animado o TIFF de varias páginas): se deja como está para no perder ninguna."; return null; }
        // an .ico holds several sizes: the biggest one
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) { note = "Es demasiado grande para convertirla de forma segura."; return null; }

        var metadata = frame.Metadata as BitmapMetadata;
        var orientation = Orientation(metadata);
        // resizing through the decoder: Windows' high-quality scaler, and the full-size image never sits in memory
        BitmapSource image = frame;
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (job.MaxSide > 0 && longest > job.MaxSide && decoder.Frames.Count == 1)
        {
            source.Position = 0;
            var scaled = new BitmapImage();
            scaled.BeginInit();
            scaled.StreamSource = source;
            scaled.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth >= frame.PixelHeight) scaled.DecodePixelWidth = job.MaxSide; else scaled.DecodePixelHeight = job.MaxSide;
            scaled.EndInit();
            image = scaled;
        }
        else if (job.MaxSide > 0 && longest > job.MaxSide)
            image = new TransformedBitmap(frame, new ScaleTransform(job.MaxSide / (double)longest, job.MaxSide / (double)longest));
        // photos that the camera stored sideways: turned for real, so they look right everywhere
        image = Upright(image, orientation);

        // same format and the hidden data kept: copied over, with "upright" now that the pixels are
        BitmapMetadata? keep = null;
        if (!job.StripData && metadata != null && format == SameCodec(decoder))
        {
            try { keep = metadata.Clone(); if (orientation > 1) keep.SetQuery("System.Photo.Orientation", (ushort)1); }
            catch { keep = null; }
        }

        if (format == "jpg")
        {
            var (flat, hadAlpha) = OnWhite(image);
            if (hadAlpha) note = "Tenía partes transparentes: en JPG pasan a fondo blanco.";
            image = flat;
        }
        if (format == "ico") return Icon(image);
        if (!Writable.Contains(format)) { note = "Formato no válido."; return null; }
        try { return Save(format, job.Quality, BitmapFrame.Create(image, null, keep, null)); }
        catch when (keep != null) { return Save(format, job.Quality, BitmapFrame.Create(image)); } // hidden data the new file can't hold
    }

    static byte[] Save(string format, int quality, BitmapFrame frame)
    {
        BitmapEncoder encoder = format switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            "png" => new PngBitmapEncoder(),
            "bmp" => new BmpBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => new GifBitmapEncoder(),
        };
        encoder.Frames.Add(frame);
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static string SameCodec(BitmapDecoder d) => d switch
    {
        JpegBitmapDecoder => "jpg", PngBitmapDecoder => "png", TiffBitmapDecoder => "tiff", _ => "",
    };

    static int Orientation(BitmapMetadata? m)
    {
        try { return m?.GetQuery("System.Photo.Orientation") is ushort o and >= 1 and <= 8 ? o : 1; }
        catch { return 1; }
    }

    /// <summary>EXIF orientation 1-8 applied to the pixels (90° turns and mirrors only: exact, no quality loss).</summary>
    public static BitmapSource Upright(BitmapSource image, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1), 3 => new RotateTransform(180), 4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270), _ => null,
        };
        return t == null ? image : new TransformedBitmap(image, t);
    }

    /// <summary>JPG has no transparency: see-through pixels are laid on white instead of turning black.</summary>
    static (BitmapSource Image, bool HadAlpha) OnWhite(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        var rgb = new byte[w * 3 * h];
        var hadAlpha = false;
        for (int i = 0, o = 0; i < px.Length; i += 4, o += 3)
        {
            var a = px[i + 3];
            if (a != 255) hadAlpha = true;
            for (var c = 0; c < 3; c++) rgb[o + c] = (byte)((px[i + c] * a + 255 * (255 - a) + 127) / 255);
        }
        return (BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3), hadAlpha);
    }

    /// <summary>A Windows icon with the usual sizes up to the image's own (PNG inside each one, as Windows Vista and later read).</summary>
    static byte[] Icon(BitmapSource image)
    {
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        var sizes = IconSizes.Where(s => s <= Math.Max(16, longest)).ToList();
        var pngs = sizes.Select(s => SquarePng(image, s)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // 0 means 256
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The image fitted into a size×size transparent square, centred.</summary>
    static byte[] SquarePng(BitmapSource image, int size)
    {
        var scale = size / (double)Math.Max(image.PixelWidth, image.PixelHeight);
        BitmapSource fit = new FormatConvertedBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
        int w = Math.Min(size, fit.PixelWidth), h = Math.Min(size, fit.PixelHeight);
        var src = new byte[fit.PixelWidth * 4 * fit.PixelHeight];
        fit.CopyPixels(src, fit.PixelWidth * 4, 0);
        var dst = new byte[size * 4 * size];
        int x0 = (size - w) / 2, y0 = (size - h) / 2;
        for (var y = 0; y < h; y++) Buffer.BlockCopy(src, y * fit.PixelWidth * 4, dst, ((y0 + y) * size + x0) * 4, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, dst, size * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The path itself, or "name (2).ext", "name (3).ext"… if something is already there.</summary>
    public static string Free(string path)
    {
        var (dir, name, ext) = (Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }

    // ---------- undo ----------
    public static List<string>? Record(string id)
    {
        var file = Path.Combine(Store.CreatedDir, id + ".json");
        if (id.Contains('\\') || id.Contains('/') || id.Contains("..") || !File.Exists(file)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)); } catch { return null; }
    }

    /// <summary>Batches whose files are still there (for Historial's button).</summary>
    public static IEnumerable<string> Batches() => Directory.Exists(Store.CreatedDir)
        ? Directory.EnumerateFiles(Store.CreatedDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Where(id => Record(id)?.Any(File.Exists) == true)
        : [];

    /// <summary>Sends the files a batch created to the Recycle Bin (never the originals: only what's in the record).</summary>
    public static bool UndoBatch(string id)
    {
        if (Record(id) is not { } made) return false;
        var files = made.Where(File.Exists).Select(p => new FoundFile(p, new FileInfo(p).Length, File.GetLastWriteTime(p), false)).ToList();
        var (_, skipped) = Storage.Recycle(files);
        if (skipped == 0) File.Delete(Path.Combine(Store.CreatedDir, id + ".json"));
        return skipped == 0;
    }
}
