using System.Reflection;
using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Twenty.Api.Execution;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public class ResearchIdentityTests
{
    [Fact]
    public async Task ResearchWritesKeepTheirOwnAdmissionGuardWithoutHostMiddleware()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-boundary-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ResearchStore(root);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            ResearchModule.Map(app, store, new DiagnosticStore(store), () => new { mode = "disconnected" });
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            foreach (var origin in new[] { "", "https://untrusted.example" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/research/studies/unknown/cancel");
                if (origin.Length > 0) { request.Headers.Add("Origin", origin); request.Headers.Add("X-Twenty-Request", "1"); }
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/research/studies/unknown/cancel");
            crossSite.Headers.Add("X-Twenty-Request", "1"); crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var denied = await client.SendAsync(crossSite); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(store.ListRuns()); await app.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ScientificIdentityOwnsTheWholeResearchBoundaryWithoutTheBrokerHost()
    {
        var identity = ResearchIdentity.Current;
        Assert.Equal("research-assemblies-v1", identity.Scheme);
        Assert.Equal(new[] { "Twenty.Core", "Twenty.Research" }, identity.Assemblies.Select(a => a.Name));
        var research = typeof(ResearchStore).Assembly;
        Assert.Equal(research, typeof(ResearchModule).Assembly); Assert.Equal(research, typeof(ExperimentWorker).Assembly);
        Assert.DoesNotContain(research.GetReferencedAssemblies(), a => a.Name == "Twenty.Api");
        Assert.DoesNotContain(identity.Assemblies, a => a.Name == typeof(ExecutionCoordinator).Assembly.GetName().Name);
        Assert.Equal(research.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ResearchBuildSdk").Value, identity.BuildSdk);
        Assert.True(Version.TryParse(identity.BuildSdk, out _)); Assert.NotEqual("unrecorded", identity.Framework);
        using var resource = research.GetManifestResourceStream("Twenty.Research.dependencies.lock.json")!;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(resource)), identity.DependencyLockSha256);
        Assert.Equal(typeof(Backtest).Assembly, typeof(Twenty.Core.Data.DatasetArchive).Assembly);
        Assert.Throws<ResearchException>(() => ResearchIdentity.Artifacts(research, research));
    }
    [Fact]
    public void AVersionedManifestCannotBeReinterpretedAsTheLegacyIdentity()
    {
        var actual = ResearchIdentity.Current;
        Assert.Equal(actual.Sha256, ResearchIdentity.Digest(actual.Scheme, actual.Assemblies));
        Assert.NotEqual(actual.Sha256, ResearchIdentity.Digest("legacy-core-api-assemblies-v1", actual.Assemblies));
        var execution = ResearchIdentity.Artifacts(typeof(Backtest).Assembly, typeof(ResearchStore).Assembly, typeof(ExecutionCoordinator).Assembly);
        Assert.Equal(new[] { "Twenty.Api", "Twenty.Core", "Twenty.Research" }, execution.Select(a => a.Name));
        Assert.NotEqual(actual.Sha256, ResearchIdentity.Digest("execution-assemblies-v2", execution, actual.Framework + "/" + actual.Architecture));
    }
}
