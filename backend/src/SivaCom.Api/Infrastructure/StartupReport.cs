using System.Diagnostics;
using System.Reflection;
using SivaCom.Data;
using SivaCom.Data.Providers;

namespace SivaCom.Api.Infrastructure;

/// <summary>
/// Where the logs go, and the one block written at every start that answers
/// "what is this service actually running with?" without opening the server:
/// which config files it read (and when they were last saved, so an edit
/// that never got a restart shows), whether WhatsApp is on, the database,
/// the URLs and the log files. No secret is ever written — only whether each
/// one is set.
/// </summary>
public static class StartupReport
{
    /// <summary>
    /// The messages log: every OTP and message step by step (request, send,
    /// Meta's answer, delivery), errors from anywhere, and nothing else. It
    /// is written at Information for SivaCom's own categories whatever
    /// Logging:LogLevel says, so turning the main log down to errors never
    /// hides a message's story.
    ///
    /// Logging:Messages:PathFormat moves it; by default it sits beside the
    /// main log as messages-{Date}.log. No main log (development, console)
    /// means no messages log either: the console already shows everything.
    /// </summary>
    public static string? MessagesLogPath(IConfiguration config)
    {
        var explicitPath = config["Logging:Messages:PathFormat"];
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

        var main = config["Logging:File:PathFormat"];
        if (string.IsNullOrWhiteSpace(main)) return null;

        var folder = Path.GetDirectoryName(main);
        return string.IsNullOrEmpty(folder) ? "messages-{Date}.log" : Path.Combine(folder, "messages-{Date}.log");
    }

    public static void AddMessagesLog(ILoggingBuilder logging, IConfiguration config)
    {
        var path = MessagesLogPath(config);
        if (path is null) return;

        logging.AddFile(path,
            minimumLevel: LogLevel.Error,
            levelOverrides: new Dictionary<string, LogLevel>
            {
                ["SivaCom"] = LogLevel.Information,
                // The authentication base class logs "was not authenticated"
                // and "was challenged" under this handler's name; its own
                // warning already says why.
                ["SivaCom.Api.Auth.AppSignatureHandler"] = LogLevel.Warning,
            },
            retainedFileCountLimit: config.GetValue<int?>("Logging:File:RetainedFileCountLimit") ?? 31);
    }

    public static void Write(WebApplication app, WhatsAppOptions whatsApp, DatabaseOptions database)
    {
        var config = app.Configuration;
        var env = app.Environment;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SivaCom.Startup");
        var started = Process.GetCurrentProcess().StartTime;

        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

        var files = new[] { "appsettings.json", $"appsettings.{env.EnvironmentName}.json", "appsettings.Local.json" }
            .Select(name => Path.Combine(env.ContentRootPath, name))
            .Select(path => File.Exists(path)
                ? $"{Path.GetFileName(path)} (saved {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss})"
                : $"{Path.GetFileName(path)} (not found)");

        var phoneId = whatsApp.PhoneNumberId is { Length: > 4 } p ? "..." + p[^4..] : "(empty)";
        var whatsAppLine = whatsApp.IsConfigured
            ? $"ON - phone number id {phoneId}, Graph API {whatsApp.ApiVersion}, access token set"
            : $"OFF - {(string.IsNullOrWhiteSpace(whatsApp.AccessToken) ? "AccessToken" : "PhoneNumberId")} is empty: " +
              (env.IsDevelopment() ? "messages are only logged (Development)" : "EVERY MESSAGE WILL FAIL");

        log.LogInformation(
            "SivaCom started\n" +
            "  Version      : {Version:l}\n" +
            "  Environment  : {Environment:l}, process started {Started:yyyy-MM-dd HH:mm:ss}\n" +
            "  Folder       : {Folder:l}\n" +
            "  Config files : {Files:l}\n" +
            "  Listening on : {Urls:l}\n" +
            "  Database     : {DbHost:l}:{DbPort}/{DbName:l} as {DbUser:l} (SSL {DbSsl:l})\n" +
            "  WhatsApp     : {WhatsApp:l}\n" +
            "  Webhook      : app secret {AppSecret:l}, verify token {VerifyToken:l}\n" +
            "  Worker       : {Worker:l}\n" +
            "  Main log     : {MainLog:l}\n" +
            "  Messages log : {MessagesLog:l}",
            version, env.EnvironmentName, started, env.ContentRootPath, string.Join(", ", files),
            config["Urls"] ?? config["ASPNETCORE_URLS"] ?? "(default)",
            database.Host, database.Port, database.Name, database.Username, database.SslMode,
            whatsAppLine,
            string.IsNullOrWhiteSpace(whatsApp.AppSecret) ? "EMPTY (every delivery status will be refused)" : "set",
            string.IsNullOrWhiteSpace(whatsApp.WebhookVerifyToken) ? "EMPTY (Meta cannot verify the webhook)" : "set",
            config.GetValue("Worker:Enabled", true) ? "on" : "OFF (Worker:Enabled = false): queued messages are never sent",
            config["Logging:File:PathFormat"] ?? "(none: console only)",
            MessagesLogPath(config) ?? "(none: console only)");

        // Also as errors and warnings of their own, so they reach the main
        // log even when it is set to errors only.
        if (!whatsApp.IsConfigured && !env.IsDevelopment())
            log.LogError("WhatsApp is not configured: every message will FAIL. Fill AccessToken and PhoneNumberId in the " +
                         "WhatsApp section of appsettings.{Environment:l}.json and restart the service.", env.EnvironmentName);
        else if (!whatsApp.IsConfigured)
            log.LogWarning("No WhatsApp provider is configured. Messages are logged instead of sent, with their codes, because this is Development.");

        if (whatsApp.IsConfigured && string.IsNullOrWhiteSpace(whatsApp.AppSecret))
            log.LogWarning("WhatsApp:AppSecret is empty: messages are sent, but every delivery status from Meta is refused, so they stay SUBMITTED.");
        // A disabled worker warns from OutboxWorker itself.
    }
}
