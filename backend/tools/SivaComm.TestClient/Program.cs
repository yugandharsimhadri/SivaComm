using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SivaComm.TestClient;

/// <summary>
/// SivaCommTest.exe — plays the part of a consuming application against a
/// SivaComm server, so the whole path (signing, OTP over WhatsApp, welcome
/// message, delivery status) can be tested from the server without HMS_WEB.
///
///   SivaCommTest                    menu
///   SivaCommTest health
///   SivaCommTest otp [phone]        full reset flow: send, type the code, verify, consume, complete
///   SivaCommTest welcome [phone]    send the welcome template, then follow its status
///   SivaCommTest status &lt;id&gt;        delivery status of a message
///
/// Settings: sivacomm-test.json beside the exe (or -c path).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var list = args.ToList();

        var configPath = Path.Combine(AppContext.BaseDirectory, "sivacomm-test.json");
        var c = list.IndexOf("-c");
        if (c >= 0 && c + 1 < list.Count) { configPath = list[c + 1]; list.RemoveRange(c, 2); }

        Settings settings;
        try { settings = Settings.Load(configPath); }
        catch (Exception ex) { return Fail($"Cannot use {configPath}: {ex.Message}"); }

        var client = new Client(settings);
        var interactive = list.Count == 0;

        try
        {
            if (interactive)
            {
                Info($"SivaComm test client — {settings.BaseUrl} as {settings.ClientId}/{settings.KeyId}");
                Console.WriteLine();
                Console.WriteLine("  1  Health check");
                Console.WriteLine("  2  OTP (password-reset flow)");
                Console.WriteLine("  3  Welcome message");
                Console.WriteLine("  4  Message status");
                Console.WriteLine();
                list = (Ask("Choose 1-4") ?? "") switch
                {
                    "1" => ["health"],
                    "2" => ["otp"],
                    "3" => ["welcome"],
                    "4" => ["status"],
                    _ => [],
                };
            }

            var code = list.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "health" => await Health(client),
                "otp" => await Otp(client, settings, list.ElementAtOrDefault(1)),
                "welcome" => await Welcome(client, settings, list.ElementAtOrDefault(1)),
                "status" => await Status(client, list.ElementAtOrDefault(1) ?? Ask("Message id") ?? ""),
                _ => Usage(),
            };
            return Done(code, interactive);
        }
        catch (HttpRequestException ex)
        {
            return Done(Fail($"Could not reach {settings.BaseUrl}: {ex.Message}. Is the SivaComApi service running, and is BaseUrl right?"), interactive);
        }
        catch (TaskCanceledException)
        {
            return Done(Fail($"{settings.BaseUrl} did not answer within 30 seconds."), interactive);
        }
    }

    // ------------------------------------------------------------------ health

    private static async Task<int> Health(Client client)
    {
        var ok = true;
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            var (status, body) = await client.GetUnsigned(path);
            Line(status == HttpStatusCode.OK, $"{path,-14} {(int)status} {body.Trim()}");
            ok &= status == HttpStatusCode.OK;
        }

        // A signed request the server must reject as "not found", not 401:
        // proves ClientId/KeyId/Secret match the server's Applications entry.
        var (s, _) = await client.Send(HttpMethod.Get, $"/api/v1/messages/{Guid.Empty}");
        if (s == HttpStatusCode.Unauthorized)
        {
            Line(false, "signature      401 — ClientId, KeyId or Secret does not match the server's Applications config");
            ok = false;
        }
        else Line(s == HttpStatusCode.NotFound, $"signature      {(int)s} — credentials accepted");

        return ok ? 0 : 1;
    }

    // --------------------------------------------------------------------- otp

    private static async Task<int> Otp(Client client, Settings settings, string? phone)
    {
        phone = PhoneOrAsk(settings, phone);
        if (phone is null) return Fail("No phone number.");

        // What HMS_WEB does: its own reference for the reset and for the user,
        // never the user's name, email or password.
        var reset = $"test-{Guid.NewGuid():N}";
        var user = $"test-user-{Guid.NewGuid():N}"[..24];

        Step("1. Create challenge (SivaComm sends the OTP on WhatsApp)");
        var (status, created) = await client.Send(HttpMethod.Post, "/api/v1/otp/challenges",
            new { resetReference = reset, userReference = user, phone, purpose = "PASSWORD_RESET" });
        Show(status, created);
        if (status != HttpStatusCode.Accepted) return 1;
        var challenge = created!["challengeId"]!.GetValue<Guid>();

        Info("The OTP is on its way. Check WhatsApp on that phone (and Messages in the dashboard).");
        Info("Without Meta settings the server only logs the message, and nothing arrives.");

        string? grant = null;
        while (grant is null)
        {
            var otp = Ask("OTP received (Enter = cancel, r = resend)");
            if (string.IsNullOrWhiteSpace(otp))
            {
                Step("Cancel challenge");
                var (cs, cancelled) = await client.Send(HttpMethod.Post, $"/api/v1/otp/challenges/{challenge}/cancel", new { resetReference = reset });
                Show(cs, cancelled);
                return 1;
            }
            if (otp.Trim().Equals("r", StringComparison.OrdinalIgnoreCase))
            {
                Step("Resend");
                var (rs, resent) = await client.Send(HttpMethod.Post, $"/api/v1/otp/challenges/{challenge}/resend", new { resetReference = reset });
                Show(rs, resent);
                continue;
            }

            Step("2. Verify OTP");
            var (vs, verified) = await client.Send(HttpMethod.Post, $"/api/v1/otp/challenges/{challenge}/verify",
                new { resetReference = reset, otp = otp.Trim() });
            if (vs == HttpStatusCode.OK) grant = verified!["verificationGrant"]!.GetValue<string>();
            Show(vs, verified);
            if (grant is null && verified?["status"]?.GetValue<string>() is not "INVALID_OTP") return 1;
        }

        Step("3. Consume grant (the application would now change the password)");
        var (gs, consumed) = await client.Send(HttpMethod.Post, "/api/v1/grants/consume",
            new { grant, resetReference = reset }, idempotencyKey: reset);
        Show(gs, consumed);
        if (gs != HttpStatusCode.OK) return 1;

        Step("4. Report completion (no password is sent — there is none in this test)");
        var (ps, completed) = await client.Send(HttpMethod.Post, $"/api/v1/otp/challenges/{challenge}/completion",
            new { resetReference = reset, outcome = "PASSWORD_CHANGED" });
        Show(ps, completed);

        Console.WriteLine();
        Line(ps == HttpStatusCode.OK, ps == HttpStatusCode.OK ? "OTP flow passed end to end." : "Completion failed.");
        return ps == HttpStatusCode.OK ? 0 : 1;
    }

    // ----------------------------------------------------------------- welcome

    private static async Task<int> Welcome(Client client, Settings settings, string? phone)
    {
        phone = PhoneOrAsk(settings, phone);
        if (phone is null) return Fail("No phone number.");

        Step($"Send welcome ({settings.WelcomeParameters.Count} parameter(s): {string.Join(", ", settings.WelcomeParameters)})");
        var (status, queued) = await client.Send(HttpMethod.Post, "/api/v1/messages/welcome",
            new { clientReference = $"test-{Guid.NewGuid():N}", phone, parameters = settings.WelcomeParameters });
        Show(status, queued);
        if (status != HttpStatusCode.Accepted) return 1;

        return await Follow(client, queued!["messageId"]!.GetValue<Guid>());
    }

    // ------------------------------------------------------------------ status

    private static async Task<int> Status(Client client, string id)
    {
        if (!Guid.TryParse(id, out var messageId)) return Fail("A message id (a GUID) is needed.");
        var (status, body) = await client.Send(HttpMethod.Get, $"/api/v1/messages/{messageId}");
        Show(status, body);
        return status == HttpStatusCode.OK ? 0 : 1;
    }

    /// <summary>Polls a message until it is delivered, read or failed, or a minute passes.</summary>
    private static async Task<int> Follow(Client client, Guid messageId)
    {
        Step("Delivery status (up to 60 s; Ctrl+C to stop)");
        var last = "";
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            var (_, body) = await client.Send(HttpMethod.Get, $"/api/v1/messages/{messageId}");
            var state = body?["status"]?.GetValue<string>() ?? "?";
            if (state != last)
            {
                Console.WriteLine($"   {watch.Elapsed:mm\\:ss}  {state}");
                last = state;
            }
            if (state is "DELIVERED" or "READ") { Line(true, "Delivered."); return 0; }
            if (state is "FAILED" or "EXPIRED") { Info("The reason is in the dashboard (Messages) and the server log."); return 1; }
            await Task.Delay(2000);
        }

        Info($"Still {last} after a minute. SUBMITTED/SENT means Meta accepted it (or, with no WhatsApp settings on the server, that it was only logged); DELIVERED and READ need the webhook configured.");
        Info($"Check later: SivaCommTest status {messageId}");
        return last is "SUBMITTED" or "SENT" ? 0 : 1;
    }

    // ----------------------------------------------------------------- helpers

    private static string? PhoneOrAsk(Settings settings, string? phone)
    {
        phone ??= string.IsNullOrWhiteSpace(settings.Phone) ? null : settings.Phone;
        return phone ?? Ask("WhatsApp number with country code (e.g. 919876543210)");
    }

    private static void Show(HttpStatusCode status, JsonNode? body)
    {
        // The grant is a credential: show enough to recognise it, not to use it.
        if (body?["verificationGrant"] is JsonValue g && g.GetValue<string>() is { Length: > 8 } grant)
            body["verificationGrant"] = grant[..6] + "…(hidden)";

        var ok = (int)status is >= 200 and < 300;
        Line(ok, $"HTTP {(int)status} {body?["status"]}{(body?["message"] is JsonValue m ? " — " + m : "")}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(Indent(body?.ToJsonString(Pretty) ?? "(no body)"));
        Console.ResetColor();
    }

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // display only
    };

    private static string Indent(string s) => "   " + s.Replace("\n", "\n   ");

    private static string? Ask(string prompt)
    {
        Console.Write($"?  {prompt}: ");
        var s = Console.ReadLine();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static void Step(string s) { Console.WriteLine(); Color(ConsoleColor.Cyan, $"==> {s}"); }
    private static void Info(string s) => Color(ConsoleColor.Gray, $"    {s}");
    private static void Line(bool ok, string s) => Color(ok ? ConsoleColor.Green : ConsoleColor.Red, $"{(ok ? "OK" : "XX")}  {s}");
    private static int Fail(string s) { Line(false, s); return 1; }

    private static void Color(ConsoleColor color, string s)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(s);
        Console.ResetColor();
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Usage: SivaCommTest [-c config.json] <command>

              health              health checks, and proves the signing credentials work
              otp [phone]         password-reset OTP: send, type the code you receive, verify, consume
              welcome [phone]     send the welcome template and follow its delivery status
              status <messageId>  delivery status of one message

            With no command, shows a menu. Settings: sivacomm-test.json beside the exe.
            """);
        return 2;
    }

    /// <summary>Keeps the window open when the exe was double-clicked.</summary>
    private static int Done(int code, bool interactive)
    {
        if (interactive)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close.");
            Console.ReadLine();
        }
        return code;
    }
}

public sealed record Settings(string BaseUrl, string ClientId, string KeyId, string Secret, string Phone, List<string> WelcomeParameters)
{
    public static Settings Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var r = doc.RootElement;
        string Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

        var s = new Settings(
            Get("BaseUrl").TrimEnd('/'), Get("ClientId"), Get("KeyId"), Get("Secret"), Get("Phone"),
            r.TryGetProperty("WelcomeParameters", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(e => e.ToString()).ToList()
                : []);

        if (!Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("BaseUrl is missing or not a URL.");
        if (s.ClientId == "" || s.KeyId == "") throw new InvalidOperationException("ClientId and KeyId are required.");
        if (s.Secret is "" || s.Secret.StartsWith("REPLACE-ME")) throw new InvalidOperationException("Secret is not filled in.");
        return s;
    }
}

/// <summary>
/// Signs requests exactly as docs/API.md specifies (HMS_WEB's SivaCommClient
/// is the production implementation):
///   canonical = METHOD \n PATH_AND_QUERY \n TIMESTAMP \n NONCE \n hex(SHA256(body))
///   signature = base64(HMAC-SHA256(secret, canonical))
/// </summary>
public sealed class Client(Settings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };

    public async Task<(HttpStatusCode, string)> GetUnsigned(string path)
    {
        using var response = await _http.GetAsync(path.TrimStart('/'));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode, JsonNode?)> Send(HttpMethod method, string path, object? payload = null, string? idempotencyKey = null)
    {
        var body = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var nonce = Guid.NewGuid().ToString("N");

        var canonical = string.Join('\n', method.Method.ToUpperInvariant(), path, timestamp, nonce,
            Convert.ToHexStringLower(SHA256.HashData(body)));
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.Secret), Encoding.UTF8.GetBytes(canonical)));

        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.Add("X-SivaComm-Client", settings.ClientId);
        request.Headers.Add("X-SivaComm-Key-Id", settings.KeyId);
        request.Headers.Add("X-SivaComm-Timestamp", timestamp);
        request.Headers.Add("X-SivaComm-Nonce", nonce);
        request.Headers.Add("X-SivaComm-Signature", signature);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
        }

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? node = null;
        try { if (text.Length > 0) node = JsonNode.Parse(text); } catch (JsonException) { node = JsonValue.Create(text); }
        return (response.StatusCode, node);
    }
}
