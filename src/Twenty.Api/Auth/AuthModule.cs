using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Twenty.Api.Auth;

public sealed record SetupRequest(string? SetupCode, string? Username, string? Password);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record PasswordRequest(string? CurrentPassword, string? NewPassword);

// Every endpoint requires the operator session unless it opts out with AllowAnonymous.
public static class AuthModule
{
    public const string StampClaim = "twenty:stamp";
    public const string RateLimitPolicy = "auth";

    public static AccountStore Add(WebApplicationBuilder builder, string root, int? iterations = null, TimeProvider? clock = null)
    {
        var store = new AccountStore(root, builder.Configuration["TWENTY_SETUP_CODE"], iterations ?? 600_000, clock);
        builder.Services.AddSingleton(store);
        // Keys persist in the private volume, so redeploying the container keeps sessions valid.
        builder.Services.AddDataProtection().SetApplicationName("twenty").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "auth", "keys")));
        var insecure = string.Equals(builder.Configuration["TWENTY_INSECURE_COOKIES"], "true", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "twenty.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = insecure ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountStore>();
                if (!accounts.StampValid(context.Principal?.FindFirstValue(StampClaim)))
                { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); }
            };
        });
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return store;
    }

    public static void Map(IEndpointRouteBuilder endpoints, AccountStore store)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapGet("/state", (ClaimsPrincipal user) => new
        {
            authenticated = user.Identity?.IsAuthenticated == true,
            username = user.Identity?.IsAuthenticated == true ? user.Identity.Name : null,
            setupRequired = store.SetupRequired,
            passwordChangedAt = user.Identity?.IsAuthenticated == true ? store.Account?.PasswordChangedAt : null,
        }).AllowAnonymous();
        auth.MapPost("/setup", async (SetupRequest request, HttpContext context) =>
        {
            try { var stamp = store.Setup(request.SetupCode, request.Username, request.Password); await SignIn(context, store.Account!.Username, stamp); return Results.Ok(new { authenticated = true }); }
            catch (AuthException error) { return Results.Json(new { error = error.Code }, statusCode: error.Code == "setup-closed" ? 409 : 400); }
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/login", async (LoginRequest request, HttpContext context) =>
        {
            var result = store.Login(request.Username, request.Password);
            if (result.Outcome == LoginOutcome.Locked) return Results.Json(new { error = "login-locked", lockedUntil = result.LockedUntil }, statusCode: 429);
            if (result.Outcome != LoginOutcome.Success) return Results.Json(new { error = "login-invalid" }, statusCode: 401);
            await SignIn(context, store.Account!.Username, result.Stamp!);
            return Results.Ok(new { authenticated = true });
        }).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/logout", async (HttpContext context) =>
        { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Ok(new { authenticated = false }); }).AllowAnonymous();
        auth.MapPost("/password", async (PasswordRequest request, HttpContext context) =>
        {
            try { var stamp = store.ChangePassword(request.CurrentPassword, request.NewPassword); await SignIn(context, store.Account!.Username, stamp); return Results.Ok(new { changed = true }); }
            catch (AuthException error) { return Results.Json(new { error = error.Code }, statusCode: 400); }
        }).RequireRateLimiting(RateLimitPolicy);
    }

    private static Task SignIn(HttpContext context, string username, string stamp) =>
        context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username), new Claim(StampClaim, stamp)], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });
}
