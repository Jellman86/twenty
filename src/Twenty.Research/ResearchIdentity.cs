using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record AssemblyEvidence(string Name, string Sha256);
public sealed record ResearchBuildEvidence(string Scheme, string Sha256, ImmutableArray<AssemblyEvidence> Assemblies,
    string DependencyLockSha256, string BuildSdk, string Framework, string Architecture);

public static class ResearchIdentity
{
    public const string Scheme = "research-assemblies-v1";
    private static readonly Lazy<ResearchBuildEvidence> identity = new(Create);
    public static ResearchBuildEvidence Current => identity.Value;
    public static ImmutableArray<AssemblyEvidence> Artifacts(params Assembly[] assemblies)
    {
        if (assemblies.Length == 0 || assemblies.Select(a => a.GetName().Name).Distinct(StringComparer.Ordinal).Count() != assemblies.Length)
            throw new ResearchException("identity-assembly-set");
        return assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(a =>
            new AssemblyEvidence(a.GetName().Name!, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location))))).ToImmutableArray();
    }
    // JSON framing and ordinal assembly names prevent an ambiguous concatenation; runtime context is recorded separately from the scientific artifact pair.
    public static string Digest(string scheme, ImmutableArray<AssemblyEvidence> artifacts, string? executionRuntime = null) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { scheme, artifacts, executionRuntime })));
    private static ResearchBuildEvidence Create()
    {
        var assembly = typeof(ResearchStore).Assembly;
        using var resource = assembly.GetManifestResourceStream("Twenty.Research.dependencies.lock.json") ?? throw new ResearchException("identity-dependency-resource-missing");
        var dependencyHash = Convert.ToHexStringLower(SHA256.HashData(resource));
        var sdk = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == "ResearchBuildSdk")?.Value;
        if (string.IsNullOrWhiteSpace(sdk)) throw new ResearchException("identity-build-sdk-missing");
        var artifacts = Artifacts(typeof(Backtest).Assembly, assembly);
        return new(Scheme, Digest(Scheme, artifacts), artifacts, dependencyHash, sdk, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString());
    }
}
