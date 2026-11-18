using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// C8 Lab 5: the race script. Usage:
//   RaceScript A --api <url>[,<url>...] --unit <unitId> [--buyers 20] [--deposit 5000]
//   RaceScript D --api <urlA>,<urlB> --unit <unitId> --tenant <tenantId>
// Environment: RACE_SIGNING_KEY (base64, the key the API instances trust for the test issuer) and, for D,
// RACE_WEBHOOK_SECRET (the instances' Payments:WebhookSecret). Both are generated per run by race.sh; neither is printed.
var options = Options.Parse(args);
var key = Environment.GetEnvironmentVariable("RACE_SIGNING_KEY") ?? throw new InvalidOperationException("Set RACE_SIGNING_KEY.");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

switch (options.Scenario)
{
    case "A":
        await ScenarioA();
        break;
    case "D":
        await ScenarioD();
        break;
    default:
        throw new ArgumentException($"Unknown scenario {options.Scenario}; A or D.");
}

// A: N buyers reserve one unit at the same moment, spread over the API instances. Expected: one 201, N-1 409.
async Task ScenarioA()
{
    var buyers = Enumerable.Range(1, options.Buyers).Select(Buyer).ToList();
    using var gate = new SemaphoreSlim(0);
    var clock = Stopwatch.StartNew();
    var tasks = buyers.Select(async (buyer, i) =>
    {
        var api = options.Apis[i % options.Apis.Count];
        await gate.WaitAsync();
        var started = clock.Elapsed;
        var (status, body) = await ReserveAsync(api, buyer);
        return (Buyer: buyer, Api: api, Status: status, Body: body, Started: started, Took: clock.Elapsed - started);
    }).ToList();
    gate.Release(buyers.Count);
    var results = await Task.WhenAll(tasks);

    Console.WriteLine($"Scenario A: {buyers.Count} buyers reserve unit {options.Unit} at once over {options.Apis.Count} instance(s)");
    foreach (var r in results.OrderBy(r => r.Started))
    {
        Console.WriteLine($"  {r.Started.TotalMilliseconds,7:F1} ms  {r.Api}  buyer …{r.Buyer.ToString()[^4..]}  {(int)r.Status} {Summary(r.Body)}  ({r.Took.TotalMilliseconds:F0} ms)");
    }

    Console.WriteLine("  outcome: " + string.Join(", ", results.GroupBy(r => (int)r.Status).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}")));
}

// D: one buyer's reservation, then the same signed checkout.session.completed event delivered to two instances at the
// same moment. Expected: both 200, one DepositPaid (race.sh counts the rows).
async Task ScenarioD()
{
    var secret = Environment.GetEnvironmentVariable("RACE_WEBHOOK_SECRET") ?? throw new InvalidOperationException("Set RACE_WEBHOOK_SECRET.");
    var buyer = Buyer(1);
    var (status, body) = await ReserveAsync(options.Apis[0], buyer);
    if ((int)status != 201)
    {
        throw new InvalidOperationException($"Setup reservation failed: {(int)status} {body}");
    }

    var reservation = JsonElement.Parse(body).GetProperty("id").GetGuid();
    var session = $"cs_race_{Guid.NewGuid():N}";
    var payload = CompletedEvent($"evt_race_{Guid.NewGuid():N}", session, reservation);
    using var gate = new SemaphoreSlim(0);
    var clock = Stopwatch.StartNew();
    var tasks = options.Apis.Select(async api =>
    {
        await gate.WaitAsync();
        var started = clock.Elapsed;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(api), "/webhooks/stripe")) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", Sign(payload, secret));
        using var response = await http.SendAsync(request);
        return (Api: api, Status: (int)response.StatusCode, Started: started, Took: clock.Elapsed - started);
    }).ToList();
    gate.Release(options.Apis.Count);
    var results = await Task.WhenAll(tasks);

    Console.WriteLine($"Scenario D: reservation {reservation}, session {session}: the same event to {options.Apis.Count} instances at once");
    foreach (var r in results.OrderBy(r => r.Started))
    {
        Console.WriteLine($"  {r.Started.TotalMilliseconds,7:F1} ms  {r.Api}  {r.Status}  ({r.Took.TotalMilliseconds:F0} ms)");
    }

    Console.WriteLine($"SESSION={session}");
}

async Task<(System.Net.HttpStatusCode Status, string Body)> ReserveAsync(string api, Guid buyer)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(api), $"/v1/units/{options.Unit}/reservations"))
    {
        Content = JsonContent.Create(new { deposit = options.Deposit, currency = "GBP" }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(buyer));
    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    using var response = await http.SendAsync(request);
    return (response.StatusCode, await response.Content.ReadAsStringAsync());
}

// The race buyers: fixed subjects, so race.sh can give each a tenant membership.
static Guid Buyer(int i) => Guid.Parse($"0198a000-0000-7000-9000-0000000b{i:D4}");

string Token(Guid subject) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Issuer = "dotnet-user-jwts",
    Subject = new ClaimsIdentity([new Claim("sub", subject.ToString()), new Claim("token_use", "access"), new Claim("client_id", "homebuild-race"), new Claim("cognito:groups", "buyer")]),
    Expires = DateTime.UtcNow.AddMinutes(10),
    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(key)), SecurityAlgorithms.HmacSha256),
});

string CompletedEvent(string eventId, string session, Guid reservation) => JsonSerializer.Serialize(new Dictionary<string, object?>
{
    ["id"] = eventId,
    ["object"] = "event",
    ["api_version"] = "2026-10-28.endive",
    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    ["livemode"] = false,
    ["type"] = "checkout.session.completed",
    ["data"] = new Dictionary<string, object?>
    {
        ["object"] = new Dictionary<string, object?>
        {
            ["id"] = session, ["object"] = "checkout.session", ["client_reference_id"] = reservation.ToString(),
            ["metadata"] = new Dictionary<string, string> { ["tenant_id"] = options.Tenant, ["unit_id"] = options.Unit, ["reservation_id"] = reservation.ToString() },
            ["amount_total"] = (long)(options.Deposit * 100), ["currency"] = "gbp", ["payment_intent"] = "pi_" + session,
            ["status"] = "complete", ["payment_status"] = "paid", ["livemode"] = false,
        },
    },
});

static string Sign(string payload, string secret)
{
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    return $"t={timestamp},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}")))}";
}

static string Summary(string body) =>
    body.Length == 0 ? "" : JsonElement.Parse(body) is var root && root.TryGetProperty("title", out var title) ? title.GetString()! : root.TryGetProperty("state", out var state) ? state.GetString()! : "";

internal sealed record Options(string Scenario, IReadOnlyList<string> Apis, string Unit, string Tenant, int Buyers, decimal Deposit)
{
    public static Options Parse(string[] args)
    {
        string Value(string name, string? fallback = null) =>
            Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException($"Missing {name}.");
        return new(args.Length > 0 ? args[0] : throw new ArgumentException("Scenario A or D."),
            Value("--api").Split(',', StringSplitOptions.RemoveEmptyEntries), Value("--unit"), Value("--tenant", "0198a000-0000-7000-8000-00000000000a"),
            int.Parse(Value("--buyers", "20"), CultureInfo.InvariantCulture), decimal.Parse(Value("--deposit", "5000"), CultureInfo.InvariantCulture));
    }
}
