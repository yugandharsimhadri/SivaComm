using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SivaComm.Api;
using SivaComm.Api.Auth;
using SivaComm.Api.Controllers;
using SivaComm.Data;
using SivaComm.Data.Providers;
using SivaComm.Data.Security;
using SivaComm.Data.Services;

// Content root is the executable's folder, so a Windows service (whose
// working directory is System32) still finds appsettings*.json beside it.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : null,
});

// Git-ignored; the developer's database password and anything else secret.
// (On a server, appsettings.Production.json beside the exe plays this part.)
// IgnoreLocalSettings=true is for the automated tests, which must not pick
// up a developer's own file.
if (!builder.Configuration.GetValue<bool>("IgnoreLocalSettings"))
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Installed with sc.exe (deploy\Deploy-SivaComm.ps1): report start/stop to
// the service manager. A no-op when run from a console.
builder.Host.UseWindowsService(o => o.ServiceName = "SivaCommApi");

// Logging:File:PathFormat in the config file, e.g. "C:\\SivaComm\\logs\\sivacomm-{Date}.log".
// A service has no console, so without this its logs go nowhere useful.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Logging:File:PathFormat"]))
    builder.Logging.AddFile(builder.Configuration.GetSection("Logging:File"));

// ── Configuration, checked up front ──────────────────────────────────────
var config = builder.Configuration;

builder.Services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<OtpOptions>(config.GetSection(OtpOptions.SectionName));
builder.Services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.SectionName));
builder.Services.Configure<MessagingOptions>(config.GetSection(MessagingOptions.SectionName));
builder.Services.Configure<WorkerOptions>(config.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.SectionName));

var database = config.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
if (string.IsNullOrWhiteSpace(database.ConnectionString) && string.IsNullOrWhiteSpace(database.Password))
    throw new InvalidOperationException(
        "Database:Password is not configured. Put it in appsettings.Local.json (development), " +
        "appsettings.Production.json (server), or the Database__Password environment variable.");

(config.GetSection(OtpOptions.SectionName).Get<OtpOptions>() ?? new OtpOptions()).Validate();

var security = config.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
if (!builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(security.MasterKey) || security.MasterKey == DevelopmentKeys.MasterKey))
    throw new InvalidOperationException(
        "Security:MasterKey is missing or is the development key. Generate 32 random bytes, base64-encode them, " +
        "and set Security__MasterKey — see docs/DEPLOYMENT.md. Losing this key makes stored secrets unreadable.");

// ── Data and services ────────────────────────────────────────────────────
builder.Services.AddDbContextFactory<CommDbContext>(o => o
    .UseNpgsql(database.BuildConnectionString())
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Crypto>();
builder.Services.AddSingleton<OutboxSignal>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddSingleton<MessageService>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<ApplicationService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<RequestAuthenticator>();
builder.Services.AddSingleton<ConfiguredApplicationsSync>();
builder.Services.AddScoped<OutboxProcessor>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddHostedService<OutboxWorker>();

// WhatsApp when the section carries a token and a phone number id; the log
// otherwise — and the log only shows codes in Development.
var whatsApp = config.GetSection(WhatsAppOptions.SectionName).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
if (whatsApp.IsConfigured)
    builder.Services.AddHttpClient<IMessageProvider, WhatsAppCloudProvider>(c => c.Timeout = TimeSpan.FromSeconds(15));
else
    builder.Services.AddSingleton<IMessageProvider, LoggingMessageProvider>();

// DataProtection:KeysPath — where the keys that encrypt the dashboard's login
// cookie live. Set it on a server, or every restart signs every operator out.
var keysPath = config["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    var dp = builder.Services.AddDataProtection()
        .SetApplicationName("SivaComm")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

// Applications — the consuming applications, their signing keys and their
// templates, read from the config file and applied at startup. Validated
// here so a typo stops the service at start, with the reason.
var configuredApps = config.GetSection("Applications").Get<List<ConfiguredApplication>>() ?? [];
var appErrors = ConfiguredApplicationsSync.Validate(configuredApps);
if (appErrors.Count > 0)
    throw new InvalidOperationException("The Applications section is not valid:\n  " + string.Join("\n  ", appErrors));

// ── Authentication ───────────────────────────────────────────────────────
// Two audiences that never overlap: applications sign each request; people
// at the dashboard hold a cookie. Neither credential satisfies the other's
// policy.
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, AppSignatureHandler>(AppClaims.Scheme, null)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.Cookie.Name = "sivacomm.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;

        // An API answers 401/403; it does not redirect to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(AppClaims.Policy, p => p
        .AddAuthenticationSchemes(AppClaims.Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(AppClaims.ApplicationId));

    o.AddPolicy(AdminAuth.Policy, p => p
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireRole("Admin"));
});

builder.Services.AddSivaCommRateLimits(config);

// Cors:AllowedOrigins — where the dashboard is hosted when it is not served
// by this API itself (Cloudflare Pages: https://sivacom.sivayaantechnologies.com).
// Credentials are allowed so the admin cookie travels; only the origins listed
// here may read responses. Application calls are server to server and never
// need CORS. Empty (the default) allows no cross-origin browser calls at all.
const string DashboardCors = "Dashboard";
var dashboardOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy(DashboardCors, p => p
    .WithOrigins(dashboardOrigins)
    .AllowCredentials()
    .WithHeaders("Content-Type", AdminAuth.CsrfHeader)
    .WithMethods("GET", "POST", "PUT")));

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // A field this API does not define is an error, not ignored — so a
        // source application cannot accidentally send a password here.
        o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx => new BadRequestObjectResult(new
    {
        success = false,
        status = "INVALID_REQUEST",
        message = "The request body is not valid for this endpoint.",
        correlationId = ctx.HttpContext.CorrelationId(),
    }));

var app = builder.Build();

// ── Release steps ────────────────────────────────────────────────────────
//   SivaComm.Api --migrate                       apply migrations and exit
//   SivaComm.Api --register-app <code> <name>    register an application; prints its key once
if (args.Contains("--migrate"))
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<CommDbContext>>().CreateDbContextAsync();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    Console.WriteLine(pending.Count == 0 ? "Already current." : $"Applying {pending.Count}: {string.Join(", ", pending)}");
    await db.Database.MigrateAsync();
    return;
}

if (database.MigrateOnStartup ?? app.Environment.IsDevelopment())
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<CommDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

var registerAt = Array.IndexOf(args, "--register-app");
if (registerAt >= 0)
{
    if (args.Length < registerAt + 3) { Console.Error.WriteLine("Usage: --register-app <code> <name>"); return; }
    var result = await app.Services.GetRequiredService<ApplicationService>()
        .RegisterAsync(args[registerAt + 1], args[registerAt + 2], "cli", default);
    if (result is null) { Console.Error.WriteLine("An application with that code already exists."); return; }

    Console.WriteLine(JsonSerializer.Serialize(new { result.Value.Key.ClientId, result.Value.Key.KeyId, result.Value.Key.Secret },
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Store the secret in the application's configuration now. It cannot be shown again.");
    return;
}

if (configuredApps.Count > 0)
{
    foreach (var line in await app.Services.GetRequiredService<ConfiguredApplicationsSync>().ApplyAsync(configuredApps, default))
        app.Logger.LogInformation("Application from configuration — {Summary}", line);
}

if (await app.Services.GetRequiredService<AdminService>()
        .EnsureBootstrapAdminAsync(config["Admin:Username"], config["Admin:Password"], default))
    app.Logger.LogWarning("Created the first dashboard admin '{User}' from configuration.", config["Admin:Username"]);

if (!whatsApp.IsConfigured)
{
    if (app.Environment.IsDevelopment())
        app.Logger.LogWarning("No WhatsApp provider is configured. Messages are logged instead of sent, with their codes, because this is Development.");
    else
        app.Logger.LogWarning("No WhatsApp provider is configured. Messages are NOT sent and their content is not logged. Fill the WhatsApp section — see docs/WHATSAPP_SETUP.md.");
}

// ── Pipeline ─────────────────────────────────────────────────────────────
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
forwarded.KnownProxies.Add(IPAddress.Loopback);
forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
app.UseForwardedHeaders(forwarded);

app.UseCorrelationId();
app.UseSafeErrors();

if (!app.Environment.IsDevelopment()) app.UseHsts();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(DashboardCors);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.MapGet("/health/live", () => Results.Text("ok")).AllowAnonymous();
app.MapGet("/health/ready", async (IDbContextFactory<CommDbContext> factory, TimeProvider time, CancellationToken ct) =>
{
    try
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Database.CanConnectAsync(ct)) return Results.StatusCode(503);

        // A worker that stopped looping means queued codes go nowhere.
        var workerStale = OutboxWorker.LastHeartbeat != default &&
                          time.GetUtcNow().UtcDateTime - OutboxWorker.LastHeartbeat > TimeSpan.FromMinutes(2);
        return workerStale ? Results.StatusCode(503) : Results.Text("ready");
    }
    catch
    {
        return Results.StatusCode(503);
    }
}).AllowAnonymous();

// The dashboard is a single-page app: unknown non-API paths serve its shell.
// An unknown /api path is a plain 404, not the dashboard's HTML.
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

/// <summary>Shipped in appsettings.Development.json; refused anywhere else.</summary>
public static class DevelopmentKeys
{
    public const string MasterKey = "c2l2YWNvbW0tZGV2ZWxvcG1lbnQta2V5LW5vdC1hLXNlY3JldA==";
}
