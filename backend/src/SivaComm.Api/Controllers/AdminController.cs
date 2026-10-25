using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using SivaComm.Data;
using SivaComm.Data.Services;

namespace SivaComm.Api.Controllers;

public static class AdminAuth
{
    public const string Policy = "Admin";

    /// <summary>
    /// Every state-changing admin request must carry this header. A browser
    /// will not add a custom header to a cross-site request without a CORS
    /// preflight, and this API allows none — so together with the SameSite
    /// cookie it stops another site driving an operator's session.
    /// </summary>
    public const string CsrfHeader = "X-SivaComm-Admin";
}

/// <summary>Rejects unsafe methods without <see cref="AdminAuth.CsrfHeader"/>.</summary>
public sealed class RequireAdminHeaderAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) &&
            context.HttpContext.Request.Headers[AdminAuth.CsrfHeader] != "1")
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }
}

public sealed record AdminLoginRequest(string Username, string Password);

public sealed record RegisterApplicationRequest(string Code, string Name);

public sealed record SetActiveRequest(bool Active);

[ApiController]
[Route("api/admin/auth")]
[RequireAdminHeader]
public sealed class AdminAuthController(AdminService admins) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.AdminLogin)]
    public async Task<IActionResult> Login(AdminLoginRequest request, CancellationToken ct)
    {
        var user = await admins.SignInAsync(request.Username ?? "", request.Password ?? "", ct);
        if (user is null) return Unauthorized(new { message = "Incorrect username or password." });

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, "Admin")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Ok(new { username = user.Username });
    }

    [HttpPost("logout")]
    [Authorize(Policy = AdminAuth.Policy)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = AdminAuth.Policy)]
    public IActionResult Me() => Ok(new { username = User.Identity!.Name });
}

/// <summary>Dashboard reads and application management. Cookie-authenticated
/// operators only; no application credential can reach these.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = AdminAuth.Policy)]
[RequireAdminHeader]
public sealed class AdminController(DashboardService dashboard, ApplicationService applications) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] Guid? applicationId, CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        return Ok(await dashboard.SummaryAsync(start, end, applicationId, ct));
    }

    [HttpGet("messages")]
    public async Task<IActionResult> Messages(
        [FromQuery] Guid? applicationId, [FromQuery] MessageStatus? status, [FromQuery] MessagePurpose? purpose,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var (start, end) = Range(from, to);
        return Ok(await dashboard.MessagesAsync(applicationId, status, purpose, start, end, page, pageSize, ct));
    }

    [HttpGet("errors")]
    public async Task<IActionResult> Errors(CancellationToken ct) => Ok(await dashboard.RecentErrorsAsync(25, ct));

    [HttpGet("applications")]
    public async Task<IActionResult> Applications(CancellationToken ct) => Ok(await applications.ListAsync(ct));

    /// <summary>The response holds the secret. It is shown once and cannot be retrieved again.</summary>
    [HttpPost("applications")]
    public async Task<IActionResult> Register(RegisterApplicationRequest request, CancellationToken ct)
    {
        if (request.Code is null || !System.Text.RegularExpressions.Regex.IsMatch(request.Code, "^[a-z0-9][a-z0-9-]{1,39}$"))
            return BadRequest(new { message = "Code must be 2–40 lower-case letters, digits or hyphens." });
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Name is required." });

        var result = await applications.RegisterAsync(request.Code, request.Name, User.Identity!.Name!, ct);
        if (result is null) return Conflict(new { message = "An application with that code already exists." });

        return Ok(new { applicationId = result.Value.App.Id, key = result.Value.Key });
    }

    [HttpPost("applications/{id:guid}/keys")]
    public async Task<IActionResult> IssueKey(Guid id, CancellationToken ct)
    {
        try
        {
            var key = await applications.IssueKeyAsync(id, User.Identity!.Name!, ct);
            return key is null ? NotFound() : Ok(key);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("applications/{id:guid}/keys/{keyId}/revoke")]
    public async Task<IActionResult> RevokeKey(Guid id, string keyId, CancellationToken ct) =>
        await applications.RevokeKeyAsync(id, keyId, User.Identity!.Name!, ct) ? NoContent() : NotFound();

    [HttpPut("applications/{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) =>
        await applications.SetActiveAsync(id, request.Active, ct) ? NoContent() : NotFound();

    [HttpPut("applications/{id:guid}/templates")]
    public async Task<IActionResult> SetTemplate(Guid id, TemplateView template, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(template.TemplateName) || string.IsNullOrWhiteSpace(template.LanguageCode))
            return BadRequest(new { message = "Template name and language are required." });
        if (template.ParameterCount is < 0 or > 10) return BadRequest(new { message = "Parameter count must be 0–10." });

        return await applications.SetTemplateAsync(id, template, ct) ? NoContent() : NotFound();
    }

    /// <summary>Defaults to the last 30 days; at most a year, so one query
    /// cannot scan the whole history.</summary>
    private static (DateTime From, DateTime To) Range(DateTime? from, DateTime? to)
    {
        var end = (to ?? DateTime.UtcNow.AddDays(1).Date).ToUniversalTime();
        var start = (from ?? end.AddDays(-30)).ToUniversalTime();
        if (end - start > TimeSpan.FromDays(366)) start = end.AddDays(-366);
        return (DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc));
    }
}
