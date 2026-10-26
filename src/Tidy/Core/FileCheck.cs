using System.IO;
using System.Security.Cryptography;

namespace Tidy.Core;

/// <summary>Is this download the real one? Its fingerprint (hash) compared with the one the website publishes, and the
/// publisher's digital signature for programs. Read-only: the file is never changed.</summary>
public static class FileCheck
{
    /// <summary>The algorithm a published fingerprint belongs to, by its length (hex): MD5, SHA-1, SHA-256 or SHA-512.</summary>
    public static string? AlgorithmOf(string expected) => Clean(expected).Length switch
    {
        32 => "MD5", 40 => "SHA-1", 64 => "SHA-256", 128 => "SHA-512", _ => null,
    };

    /// <summary>Spaces, dashes and "sha256:" prefixes out, lowercase: what's left must be hex.</summary>
    public static string Clean(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (t.IndexOf(':') is >= 0 and < 8) t = t[(t.IndexOf(':') + 1)..];
        t = new string(t.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return t.All(Uri.IsHexDigit) ? t : "";
    }

    public static async Task<string> Hash(string path, string algorithm, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using HashAlgorithm h = algorithm switch
        {
            "MD5" => MD5.Create(), "SHA-1" => SHA1.Create(), "SHA-512" => SHA512.Create(), _ => SHA256.Create(),
        };
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[1 << 20];
        long done = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            h.TransformBlock(buffer, 0, n, null, 0);
            done += n;
            progress?.Report(stream.Length > 0 ? done / (double)stream.Length : 1);
        }
        h.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(h.Hash!);
    }

    public static bool Matches(string actual, string expected) => Clean(expected) is { Length: > 0 } e && e == actual.ToLowerInvariant();

    /// <summary>Programs and installers carry the publisher's signature; Windows checks it hasn't been altered.</summary>
    public static bool IsSignable(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".msi" or ".dll" or ".sys" or ".msix" or ".appx" or ".cab" or ".ps1";

    public static bool HasValidSignature(string path) => Native.HasValidSignature(path);
}
