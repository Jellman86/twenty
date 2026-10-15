using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Api.Research;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

// Private exact request/response bodies. Authentication, cookies and arbitrary headers are never archived.
public sealed class BrokerEvidenceArchive
{
    private readonly string root;
    private readonly object gate = new();
    public BrokerEvidenceArchive(string workspaceRoot) { root = Path.Combine(workspaceRoot, "broker-evidence"); Directory.CreateDirectory(root); }
    public void Record(BrokerEnvironment environment, string method, string path, int? status, byte[] bytes, bool complete, string? intentId = null, long? accountId = null)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new ResearchException("broker-evidence-payload-limit");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new { environment = environment.ToString().ToLowerInvariant(), method, path, status,
            capturedAt = DateTimeOffset.UtcNow, sha256 = sha, complete, intentId, lastObservedAccountId = accountId }, ResearchStore.Json);
        lock (gate)
        {
            var size = Directory.EnumerateFiles(root).Sum(p => new FileInfo(p).Length);
            var payload = Path.Combine(root, sha + ".body");
            if (size + (File.Exists(payload) ? 0 : bytes.Length) + receipt.Length > 128L * 1024 * 1024) throw new ResearchException("broker-evidence-capacity");
            if (File.Exists(payload))
            { if (!File.ReadAllBytes(payload).AsSpan().SequenceEqual(bytes)) throw new ResearchException("broker-evidence-integrity"); }
            else Write(payload, bytes);
            var name = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N") + ".json";
            Write(Path.Combine(root, name), receipt);
        }
    }
    private static void Write(string path, byte[] bytes)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
