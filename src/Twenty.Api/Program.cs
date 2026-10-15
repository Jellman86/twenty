using Twenty.Core;

if (args.Contains("--health-check"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { return (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
    catch (TaskCanceledException) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", mode = "research" }));
app.MapGet("/api/meta", () => new { name = "Twenty", mode = "Research only", brokerConnected = false,
    dataset = "No historical dataset connected", commit = Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local",
    roadmap = "https://github.com/users/Jellman86/projects/1" });
app.MapGet("/api/roadmap", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "roadmap.json"), "application/json"));
app.MapPost("/api/projections", (ProjectionRequest request) =>
{
    try { return Results.Ok(Projection.Calculate(request)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
return 0;
