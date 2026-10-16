using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public static class BrokerConfiguration
{
    public static Trading212Client? Create(IConfiguration configuration, string? workspaceRoot = null)
    {
        var keyFile = configuration["TWENTY_T212_KEY_FILE"];
        var secretFile = configuration["TWENTY_T212_SECRET_FILE"];
        // Credentials come either from mounted files or directly from the environment (e.g. Dockhand encrypted secrets), never a mix.
        var keyValue = configuration["TWENTY_T212_KEY"];
        var secretValue = configuration["TWENTY_T212_SECRET"];
        var files = !string.IsNullOrWhiteSpace(keyFile) || !string.IsNullOrWhiteSpace(secretFile);
        var values = !string.IsNullOrWhiteSpace(keyValue) || !string.IsNullOrWhiteSpace(secretValue);
        if (!files && !values) return null;
        if (files && values) throw new ResearchException("broker-credential-source-ambiguous");
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
        static string Value(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 4096 ? value.Trim() : throw new ResearchException("broker-credential-value");
        var (key, secret) = files ? (Read(keyFile), Read(secretFile)) : (Value(keyValue), Value(secretValue));
        return new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, environment, key, secret, evidence: workspaceRoot is null ? null : new BrokerEvidenceArchive(workspaceRoot));
    }
}
