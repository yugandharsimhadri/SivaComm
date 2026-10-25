using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SivaComm.Data.Providers;

/// <summary>One template message to one number, as the worker hands it over.</summary>
public sealed record ProviderMessage(
    Guid MessageId,
    MessagePurpose Purpose,
    string To,
    string TemplateName,
    string LanguageCode,
    IReadOnlyList<string> Parameters);

public enum ProviderResultKind
{
    Accepted,

    /// <summary>Worth trying again: timeout, 5xx, throttling.</summary>
    Transient,

    /// <summary>Will fail the same way every time: bad template, bad number, bad token.</summary>
    Permanent,
}

/// <param name="Error">Provider error code and short title — never a raw body.</param>
public sealed record ProviderResult(ProviderResultKind Kind, string? ProviderMessageId = null, string? Error = null)
{
    public static ProviderResult Accepted(string id) => new(ProviderResultKind.Accepted, id);
    public static ProviderResult Transient(string error) => new(ProviderResultKind.Transient, Error: error);
    public static ProviderResult Permanent(string error) => new(ProviderResultKind.Permanent, Error: error);
}

/// <summary>
/// A transport. WhatsApp today; SMS is another implementation plus a
/// channel value, with nothing above this interface changing.
/// </summary>
public interface IMessageProvider
{
    string Name { get; }
    MessageChannel Channel { get; }
    Task<ProviderResult> SendAsync(ProviderMessage message, CancellationToken ct);
}

/// <summary>
/// Stands in when no provider is configured. Unlike HMS_WEB's logging sender
/// it writes the parameters — which include the OTP — <b>only in
/// Development</b>; anywhere else it logs that a message was dropped and
/// nothing more, because a log full of live reset codes is exactly what the
/// rest of this service is built to avoid.
/// </summary>
public sealed class LoggingMessageProvider(ILogger<LoggingMessageProvider> logger, IHostEnvironment environment) : IMessageProvider
{
    public string Name => "log";
    public MessageChannel Channel => MessageChannel.WhatsApp;

    public Task<ProviderResult> SendAsync(ProviderMessage message, CancellationToken ct)
    {
        if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "DEVELOPMENT ONLY — no provider configured, message NOT sent.\n  Message : {MessageId}\n  " +
                "Purpose : {Purpose}\n  Template: {Template}\n  Params  : {Parameters}",
                message.MessageId, message.Purpose, message.TemplateName, string.Join(" | ", message.Parameters));
        }
        else
        {
            logger.LogError(
                "No messaging provider is configured; message {MessageId} ({Purpose}) was dropped.",
                message.MessageId, message.Purpose);
        }

        return Task.FromResult(ProviderResult.Accepted($"log-{message.MessageId:N}"));
    }
}
