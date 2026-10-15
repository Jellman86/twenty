using System.Security.Cryptography;

namespace Twenty.Core.Data;

/// <summary>Explicit offline import into immutable, content-addressed dataset files.</summary>
public static class DatasetArchive
{
    public static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (buffer.Length <= DatasetJson.MaximumBytes)
        {
            var count = stream.Read(block, 0, (int)Math.Min(block.Length, DatasetJson.MaximumBytes + 1L - buffer.Length));
            if (count == 0) break;
            buffer.Write(block, 0, count);
        }
        if (buffer.Length > DatasetJson.MaximumBytes) throw new InvalidDataException("Dataset exceeds the input size limit.");
        return buffer.ToArray();
    }

    public static PointInTimeDataset Load(string root, string digest)
    {
        if (digest.Length != 64 || !digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A lowercase SHA-256 content address is required.", nameof(digest));
        var bytes = ReadBounded(Path.Combine(root, "datasets", DatasetValidator.SchemaVersion, digest + ".json"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest)
            throw new InvalidDataException("Archived dataset failed content-address verification.");
        return new PointInTimeDataset(DatasetJson.Read(bytes));
    }

    public static string Store(string root, ReadOnlySpan<byte> bytes)
    {
        _ = new PointInTimeDataset(DatasetJson.Read(bytes)); // Validate before creating anything.
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(root, "datasets", DatasetValidator.SchemaVersion);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, digest + ".json");
        var temporary = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                var existing = ReadBounded(destination);
                if (!existing.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("Existing archived content does not match its content address; import refused.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }
}
