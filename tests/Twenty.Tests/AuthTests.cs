using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Twenty.Api.Auth;

namespace Twenty.Tests;

public sealed class AuthTests
{
    private const string Code = "test-setup-code";
    private const string Password = "correct horse battery";

    [Fact]
    public async Task SetupNeedsTheOneTimeCodeAndThenCloses()
    {
        await using var f = await Host.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("/api/private")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/health")).StatusCode);
        var state = await f.Client.GetFromJsonAsync<State>("/api/auth/state");
        Assert.True(state!.SetupRequired); Assert.False(state.Authenticated);
        Assert.Equal("setup-code-invalid", await Error(await f.Client.PostAsJsonAsync("/api/auth/setup", new { setupCode = "wrong", username = "scott", password = Password })));
        Assert.Equal("password-too-short", await Error(await f.Client.PostAsJsonAsync("/api/auth/setup", new { setupCode = Code, username = "scott", password = "short" })));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync("/api/auth/setup", new { setupCode = Code, username = "scott", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/private")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync("/api/auth/setup", new { setupCode = Code, username = "other", password = Password })).StatusCode);
        state = await f.Client.GetFromJsonAsync<State>("/api/auth/state");
        Assert.False(state!.SetupRequired); Assert.True(state.Authenticated); Assert.Equal("scott", state.Username);
        Assert.Null(f.Accounts.SetupCode);
    }

    [Fact]
    public async Task RepeatedFailuresLockTheAccountEvenForTheRightPassword()
    {
        await using var f = await Host.Start();
        await f.Setup();
        await f.Client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("/api/private")).StatusCode);
        for (var i = 1; i < AccountStore.MaximumFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.PostAsJsonAsync("/api/auth/login", new { username = "scott", password = "wrong password!" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Client.PostAsJsonAsync("/api/auth/login", new { username = "scott", password = "wrong password!" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Client.PostAsJsonAsync("/api/auth/login", new { username = "scott", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync("/api/private")).StatusCode);
        f.Clock.Advance(AccountStore.LockoutPeriod + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync("/api/auth/login", new { username = "SCOTT", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/private")).StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordSignsOutOtherSessions()
    {
        await using var f = await Host.Start();
        await f.Setup();
        using var other = f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/auth/login", new { username = "scott", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/private")).StatusCode);
        Assert.Equal("password-current-invalid", await Error(await f.Client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "nope", newPassword = "another long password" })));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = "another long password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/private")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/private")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsJsonAsync("/api/auth/login", new { username = "scott", password = Password })).StatusCode);
    }

    [Fact]
    public async Task SessionCookieIsHardenedAndOnlyAHashIsStored()
    {
        await using var f = await Host.Start(insecure: false);
        var response = await f.Client.PostAsJsonAsync("/api/auth/setup", new { setupCode = Code, username = "scott", password = Password });
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("twenty.session=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie); Assert.Contains("secure", cookie);
        var file = File.ReadAllText(Path.Combine(f.Root, "auth", "account.json"));
        Assert.DoesNotContain(Password, file);
        var reloaded = new AccountStore(f.Root, iterations: 1000);
        Assert.False(reloaded.SetupRequired); Assert.Null(reloaded.SetupCode);
        Assert.Equal(LoginOutcome.Success, reloaded.Login("scott", Password).Outcome);
        Assert.Equal(LoginOutcome.Invalid, reloaded.Login("someone", Password).Outcome);
    }

    [Fact]
    public void GeneratedSetupCodesAreUnpredictableAndConfiguredCodesAreUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = new AccountStore(Path.Combine(root, "a"), iterations: 1000).SetupCode; var b = new AccountStore(Path.Combine(root, "b"), iterations: 1000).SetupCode;
            Assert.NotEqual(a, b); Assert.Equal(24, a!.Length);
            Assert.Equal("fixed", new AccountStore(Path.Combine(root, "c"), " fixed ", 1000).SetupCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SignInCanBeSwitchedOffButNotWithALiveBrokerKey()
    {
        await using var f = await Host.Start(true, ("TWENTY_AUTH", "off"), ("TWENTY_T212_ENVIRONMENT", "demo"), ("TWENTY_T212_KEY", "k"), ("TWENTY_T212_SECRET", "s"));
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/private")).StatusCode);
        var state = await f.Client.GetFromJsonAsync<Dictionary<string, object>>("/api/auth/state");
        Assert.Equal("True", state!["authDisabled"].ToString());

        static bool Disabled(params (string Key, string Value)[] values) => AuthModule.Disabled(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());
        Assert.False(Disabled());
        Assert.True(Disabled(("TWENTY_AUTH", "off"), ("TWENTY_T212_ENVIRONMENT", "live")));
        Assert.Equal("auth-required-for-live-broker", Assert.Throws<AuthException>(() =>
            Disabled(("TWENTY_AUTH", "off"), ("TWENTY_T212_ENVIRONMENT", "live"), ("TWENTY_T212_KEY", "k"))).Code);
        Assert.Equal("auth-setting-invalid", Assert.Throws<AuthException>(() => Disabled(("TWENTY_AUTH", "maybe"))).Code);
    }

    private static async Task<string?> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>())?["error"]?.ToString();

    private sealed record State(bool Authenticated, string? Username, bool SetupRequired);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public HttpClient Client { get; private set; } = null!;
        public required AccountStore Accounts { get; init; }
        public required ManualClock Clock { get; init; }
        public required string Root { get; init; }
        private readonly List<HttpClient> clients = [];

        public static async Task<Host> Start(bool insecure = true, params (string Key, string Value)[] settings)
        {
            var root = Path.Combine(Path.GetTempPath(), "twenty-auth-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["TWENTY_SETUP_CODE"] = Code;
            builder.Configuration["TWENTY_INSECURE_COOKIES"] = insecure ? "true" : "false";
            foreach (var (key, value) in settings) builder.Configuration[key] = value;
            var clock = new ManualClock();
            var accounts = AuthModule.Add(builder, root, iterations: 1000, clock: clock);
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
            app.MapGet("/health", () => "ok").AllowAnonymous();
            app.MapGet("/api/private", () => "secret");
            AuthModule.Map(app, accounts, AuthModule.Disabled(builder.Configuration));
            await app.StartAsync();
            var host = new Host { App = app, Accounts = accounts, Clock = clock, Root = root };
            host.Client = host.NewClient();
            return host;
        }

        public HttpClient NewClient()
        {
            var client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() })
            { BaseAddress = new Uri(App.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            clients.Add(client); return client;
        }

        public async Task Setup() =>
            Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/api/auth/setup", new { setupCode = Code, username = "scott", password = Password })).StatusCode);

        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients) client.Dispose();
            await App.StopAsync(); await App.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
