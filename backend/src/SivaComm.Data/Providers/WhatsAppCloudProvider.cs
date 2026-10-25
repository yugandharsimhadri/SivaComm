using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SivaComm.Data.Providers;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Permanent System User token. Never the 24-hour dashboard token.</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>The phone number <b>id</b> from API Setup, not the number.</summary>
    public string PhoneNumberId { get; set; } = "";

    /// <summary>The Meta app secret, used to check X-Hub-Signature-256 on webhooks.</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>Chosen by us and typed into Meta's webhook setup; echoed back on the verify GET.</summary>
    public string WebhookVerifyToken { get; set; } = "";

    public string ApiVersion { get; set; } = "v21.0";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(PhoneNumberId);
}

/// <summary>
/// Meta's WhatsApp Cloud API. Ported from HMS_WEB's WhatsAppCloudMessageSender
/// with three changes: it returns Meta's message id (the key every delivery
/// webhook reports against), it classifies failures so the worker knows
/// whether a retry can help, and it never logs a response body, which can
/// echo the recipient back.
///
/// An OTP goes out as an <b>Authentication</b> template: the code is the one
/// body parameter, and the same code fills the copy-code button. Meta requires
/// that category for one-time codes.
/// </summary>
public sealed class WhatsAppCloudProvider(
    HttpClient http,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppCloudProvider> logger) : IMessageProvider
{
    private readonly WhatsAppOptions _options = options.Value;

    public string Name => "whatsapp-cloud";
    public MessageChannel Channel => MessageChannel.WhatsApp;

    public async Task<ProviderResult> SendAsync(ProviderMessage message, CancellationToken ct)
    {
        var components = new List<object>
        {
            new
            {
                type = "body",
                parameters = message.Parameters.Select(p => new { type = "text", text = p }).ToArray(),
            },
        };

        if (message.Purpose == MessagePurpose.PasswordResetOtp)
        {
            components.Add(new
            {
                type = "button",
                sub_type = "url",
                index = "0",
                parameters = new[] { new { type = "text", text = message.Parameters[0] } },
            });
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = message.To,
            type = "template",
            template = new
            {
                name = message.TemplateName,
                language = new { code = message.LanguageCode },
                components,
            },
        };

        var url = $"https://graph.facebook.com/{_options.ApiVersion}/{_options.PhoneNumberId}/messages";

        HttpResponseMessage response;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new("Bearer", _options.AccessToken);

            response = await http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not reach WhatsApp for message {MessageId}: {Error}", message.MessageId, ex.GetType().Name);
            return ProviderResult.Transient(ex is TaskCanceledException ? "timeout" : "network error");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var id = ReadMessageId(body);
                if (id is null)
                {
                    logger.LogError("WhatsApp accepted message {MessageId} but returned no message id.", message.MessageId);
                    return ProviderResult.Permanent("accepted without a message id");
                }

                logger.LogInformation("WhatsApp accepted message {MessageId} as {ProviderId}.", message.MessageId, id);
                return ProviderResult.Accepted(id);
            }

            var error = ReadError(body);
            var summary = $"{(int)response.StatusCode} {error}";
            logger.LogError("WhatsApp refused message {MessageId}: {Error}", message.MessageId, summary);

            // 429 and 5xx can succeed on a later attempt. Everything else —
            // a missing template, an expired token, an unknown number — will
            // fail identically, so retrying only delays the failure report.
            return response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                ? ProviderResult.Transient(summary)
                : ProviderResult.Permanent(summary);
        }
    }

    private static string? ReadMessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0
                ? messages[0].GetProperty("id").GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>"(#132001) Template name does not exist" — code and title,
    /// which is what an operator needs and contains no personal data.</summary>
    private static string ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                var code = e.TryGetProperty("code", out var c) ? c.ToString() : "?";
                var msg = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                var text = $"(#{code}) {msg ?? "error"}";
                return text.Length <= 200 ? text : text[..200];
            }
        }
        catch (JsonException) { }

        return "unreadable error response";
    }
}
