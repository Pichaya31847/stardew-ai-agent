using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace StardewMCP.Agent;

/// <summary>Lossless, appendable gzip members; old JSONL remains readable and can be migrated safely.</summary>
public static class CompressedLogStore
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static void Append(string legacyPath, string text)
    {
        // Finish a member before touching the destination so serialization/compression errors cannot leave a header behind.
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, true))
        {
            var bytes = Utf8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        using var file = new FileStream(legacyPath + ".gz", FileMode.Append, FileAccess.Write, FileShare.Read);
        buffer.Position = 0;
        buffer.CopyTo(file);
        file.Flush(true);
    }

    public static bool Exists(string legacyPath) => File.Exists(legacyPath) || File.Exists(legacyPath + ".gz");

    public static string Read(string legacyPath)
    {
        var old = File.Exists(legacyPath) ? File.ReadAllText(legacyPath, Utf8) : "";
        var path = legacyPath + ".gz";
        if (!File.Exists(path)) return old;
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Utf8);
        var current = reader.ReadToEnd();
        return old + (old.Length > 0 && !old.EndsWith("\n", StringComparison.Ordinal) && current.Length > 0 ? "\n" : "") + current;
    }

    public static void Migrate(string legacyPath)
    {
        if (!File.Exists(legacyPath) || File.Exists(legacyPath + ".gz")) return;
        string destination = legacyPath + ".gz";
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = File.OpenRead(legacyPath))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) source.CopyTo(gzip);
                output.Flush(true);
            }
            // Compare decompressed bytes, including BOM/newlines, before removing the original.
            using (var source = File.OpenRead(legacyPath))
            using (var packed = File.OpenRead(temporary))
            using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
            {
                var left = new byte[8192];
                var right = new byte[8192];
                int count;
                while ((count = source.Read(left, 0, left.Length)) > 0)
                {
                    int got = 0;
                    while (got < count)
                    {
                        int next = gzip.Read(right, got, count - got);
                        if (next == 0) throw new InvalidDataException("Compressed log verification failed.");
                        got += next;
                    }
                    if (!left.Take(count).SequenceEqual(right.Take(count))) throw new InvalidDataException("Compressed log differs from its source.");
                }
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Compressed log has extra bytes.");
            }
            File.Move(temporary, destination);
            File.Delete(legacyPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static int[] Days(string directory) => Directory.EnumerateFiles(directory, "day_*.jsonl*")
        .Select(Path.GetFileName)
        .Where(n => n != null && (n.EndsWith(".jsonl", StringComparison.Ordinal) || n.EndsWith(".jsonl.gz", StringComparison.Ordinal)))
        .Select(n => int.TryParse(n![4..n!.IndexOf('.')], out var day) ? day : 0)
        .Where(day => day > 0).Distinct().OrderBy(day => day).ToArray();
}
