using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SivaCom.Data.Providers;
using SivaCom.Data.Services;

namespace SivaCom.Api.Controllers;

/// <summary>
/// Meta's delivery receipts. Anonymous at the HTTP level and authenticated by
/// Meta's own signature over the raw body, checked before anything is parsed.
/// </summary>
[ApiController]
[Route("api/v1/webhooks/whatsapp")]
[AllowAnonymous]
public sealed class WebhooksController(WebhookService webhooks, IOptions<WhatsAppOptions> options, ILogger<WebhooksController> logger) : ControllerBase
{
    /// <summary>Meta's one-time subscription handshake.</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? token,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var expected = options.Value.WebhookVerifyToken;
        if (mode == "subscribe" && !string.IsNullOrEmpty(expected) && token is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
        {
            logger.LogInformation("Meta webhook verification succeeded: Meta will now send delivery statuses here.");
            return Content(challenge ?? "", "text/plain");
        }

        logger.LogWarning("Meta webhook verification refused: {Reason:l}.",
            string.IsNullOrEmpty(expected) ? "WhatsApp:WebhookVerifyToken is empty in the config"
            : mode != "subscribe" ? $"hub.mode was '{mode}', not 'subscribe'"
            : "the verify token typed into Meta does not match WhatsApp:WebhookVerifyToken");
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpPost]
    [RequestSizeLimit(256 * 1024)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        if (!WebhookService.SignatureIsValid(body, Request.Headers["X-Hub-Signature-256"], options.Value.AppSecret))
        {
            logger.LogWarning("WhatsApp webhook rejected: {Reason:l}. Delivery statuses are not being recorded.",
                string.IsNullOrEmpty(options.Value.AppSecret) ? "WhatsApp:AppSecret is empty in the config"
                : "its signature does not match WhatsApp:AppSecret (wrong app secret, or it was reset in Meta)");
            return Unauthorized();
        }

        List<StatusUpdate> updates;
        try
        {
            updates = WebhookService.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest();
        }

        await webhooks.ApplyAsync(updates, ct);

        // 200 promptly, including for duplicates: anything else makes Meta
        // retry, and the retries are what duplicates are.
        return Ok();
    }
}
