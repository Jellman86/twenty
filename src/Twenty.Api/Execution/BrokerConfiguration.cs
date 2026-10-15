using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public static class BrokerConfiguration
{
    public static Trading212Client? Create(IConfiguration configuration, string? workspaceRoot = null)
    {
        var keyFile = configuration["TWENTY_T212_KEY_FILE"];
        var secretFile = configuration["TWENTY_T212_SECRET_FILE"];
        if (string.IsNullOrWhiteSpace(keyFile) && string.IsNullOrWhiteSpace(secretFile)) return null;
        var environment = configuration["TWENTY_T212_ENVIRONMENT"] switch
        {
            "demo" => BrokerEnvironment.Demo,
            "live" => BrokerEnvironment.Live,
            _ => throw new ResearchException("broker-environment-required")
        };
        string Read(string? path)
        {
            try
            {
                if (path is null || !Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 4096) throw new ResearchException("broker-credential-file");
                return File.ReadAllText(path).Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new ResearchException("broker-credential-file"); }
        }
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        return new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, environment, Read(keyFile), Read(secretFile), evidence: workspaceRoot is null ? null : new BrokerEvidenceArchive(workspaceRoot));
    }
}
