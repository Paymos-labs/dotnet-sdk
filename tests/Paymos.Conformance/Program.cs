using System.Net;
using System.Text;
using System.Text.Json;
using Paymos;

using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract.json")));
var vectors = contract.RootElement.GetProperty("vectors");
var post = vectors.GetProperty("post_signing");
Equal(post.GetProperty("authorization").GetString(), RequestSigner.AuthorizationHeader(
    Text(post, "api_key"), Text(post, "api_secret"), Text(post, "timestamp"), Text(post, "method"), Text(post, "path"), Text(post, "query"), Text(post, "body")));
var get = vectors.GetProperty("get_query_signing");
Equal(Text(get, "query"), RequestSigner.BuildQuery(new Dictionary<string, object?> { ["status"] = new[] { "paid_over", "paid" }, ["project_id"] = "prj/a", ["limit"] = 50 }));
Equal(Text(get, "query"), RequestSigner.BuildQuery(new InvoiceListOptions(50, Status: [InvoiceStatus.PaidOver, InvoiceStatus.Paid], ProjectId: "prj/a")));
var webhook = vectors.GetProperty("webhook");
var verifier = new WebhookVerifier(Text(webhook, "secret"), TimeSpan.FromSeconds(webhook.GetProperty("tolerance_seconds").GetInt32()));
True(verifier.Verify(Text(webhook, "header"), Encoding.UTF8.GetBytes(Text(webhook, "raw_body")), DateTimeOffset.FromUnixTimeSeconds(webhook.GetProperty("now").GetInt64())));
var typedWebhook = verifier.ConstructEvent<JsonElement>(Text(webhook, "header"), Encoding.UTF8.GetBytes(Text(webhook, "raw_body")), DateTimeOffset.FromUnixTimeSeconds(webhook.GetProperty("now").GetInt64()));
Equal("evt_123", typedWebhook.EventId);
Equal("inv_123", typedWebhook.Data.GetProperty("invoice_id").GetString());
True(!verifier.Verify(Text(webhook, "header") + ",t=" + webhook.GetProperty("timestamp").GetInt64(), Encoding.UTF8.GetBytes(Text(webhook, "raw_body")), DateTimeOffset.FromUnixTimeSeconds(webhook.GetProperty("now").GetInt64())));

var handler = new CaptureHandler();
var client = new PaymosClient("pk_test_key", "sk_test_secret", new HttpClient(handler), timeProvider: new FixedTimeProvider());
await client.System.TimeAsync(); await client.Invoices.CreateAsync(new("prj_1", "10.00", "USD", "order_1")); await client.Invoices.GetAsync("inv/1"); await client.Invoices.ListAsync(new(Limit: 1));
await client.Invoices.CancelAsync("inv_1", "reason"); await client.Invoices.ConfirmPaymentAsync("inv_1", "USDT", "tron"); await client.Invoices.SimulatePaymentAsync("inv_1", "paid");
await client.Withdrawals.CreateAsync(new("address", "tron", "USDT", "5.00", "payout_1")); await client.Withdrawals.GetAsync("wdr_1"); await client.Withdrawals.ListAsync(new(Limit: 1));
await client.Withdrawals.CancelAsync("wdr_1", "reason"); await client.Withdrawals.SimulateCompletionAsync("wdr_1"); await client.Balances.GetAsync();
Equal(new[] { "GET /v1/time", "POST /v1/invoices", "GET /v1/invoices/inv%2F1", "GET /v1/invoices?limit=1", "POST /v1/invoices/inv_1/cancel", "POST /v1/invoices/inv_1/confirm-payment", "POST /v1/sandbox/invoices/inv_1/simulate-payment", "POST /v1/withdrawals", "GET /v1/withdrawals/wdr_1", "GET /v1/withdrawals?limit=1", "POST /v1/withdrawals/wdr_1/cancel", "POST /v1/sandbox/withdrawals/wdr_1/simulate-completion", "GET /v1/balances" }, handler.Paths.ToArray());
True(handler.UserAgents.All(x => x == "paymos-dotnet/1.0.0"));

var retryHandler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
retryHandler.RetryAfterSeconds = 0;
var retryClient = new PaymosClient("pk", "sk", new HttpClient(retryHandler), timeProvider: new FixedTimeProvider(), baseDelay: TimeSpan.Zero);
await retryClient.Invoices.CreateAsync(new("prj_1", "10.00", "USD", "order_1"));
Equal(2, retryHandler.Attempts);

var unavailableHandler = new SequenceHandler(HttpStatusCode.ServiceUnavailable);
var unavailableClient = new PaymosClient("pk", "sk", new HttpClient(unavailableHandler), timeProvider: new FixedTimeProvider(), baseDelay: TimeSpan.Zero);
try { await unavailableClient.Invoices.CreateAsync(new("prj_1", "10.00", "USD", "order_1")); throw new Exception("Expected API error"); }
catch (PaymosApiException error) { Equal("unavailable", error.Kind); Equal(1, unavailableHandler.Attempts); }

var multiProblem = vectors.GetProperty("problem_details").GetProperty("multi");
var problemHandler = new ProblemHandler(HttpStatusCode.BadRequest, multiProblem.GetRawText());
var problemClient = new PaymosClient("pk", "sk", new HttpClient(problemHandler), timeProvider: new FixedTimeProvider(), maxRetries: 0);
try { await problemClient.System.TimeAsync(); throw new Exception("Expected API error"); }
catch (PaymosApiException error)
{
    Equal("about:blank", error.Type);
    Equal("Bad Request", error.Title);
    Equal(400, error.ProblemStatus);
    Equal("Validation failed.", error.Detail);
    Equal("validation_failed", error.Code);
    Equal<string?>(null, error.Field);
    Equal("field_required", error.Errors.Single().Code);
}
Console.WriteLine("Paymos .NET conformance: PASS");

static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
static void Equal<T>(T expected, T actual) { if (expected is Array expectedArray && actual is Array actualArray) { if (!expectedArray.Cast<object?>().SequenceEqual(actualArray.Cast<object?>())) throw new Exception("Sequences differ"); return; } if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
sealed class FixedTimeProvider : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000); }
sealed class CaptureHandler : HttpMessageHandler
{
    public List<string> Paths { get; } = []; public List<string> UserAgents { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Paths.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
        UserAgents.Add(request.Headers.UserAgent.ToString());
        var path = request.RequestUri.AbsolutePath;
        var body = path switch
        {
            "/v1/time" => "{\"server_time\":1700000000}",
            "/v1/balances" => "[]",
            "/v1/invoices" when request.Method == HttpMethod.Get => "{\"items\":[],\"next_cursor\":null}",
            "/v1/withdrawals" when request.Method == HttpMethod.Get => "{\"items\":[],\"next_cursor\":null}",
            _ when path.Contains("invoice", StringComparison.Ordinal) => "{\"invoice_id\":\"inv_1\",\"project_id\":\"prj_1\",\"status\":\"awaiting_payment\",\"is_final\":false,\"is_test\":true,\"payment_url\":\"https://pay.paymos.io/i/inv_1\",\"order\":{\"external_id\":\"order_1\",\"amount\":\"10.00\",\"currency\":\"USD\"},\"created_at\":1700000000,\"updated_at\":1700000000}",
            _ => "{\"withdrawal_id\":\"wdr_1\",\"external_order_id\":\"payout_1\",\"status\":\"created\",\"is_final\":false,\"is_test\":true,\"amount\":\"5.00\",\"currency\":\"USDT\",\"network\":\"tron\",\"destination_address\":\"address\",\"created_at\":1700000000}"
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
{
    public int Attempts { get; private set; }
    public int? RetryAfterSeconds { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { var status = statuses[Math.Min(Attempts, statuses.Length - 1)]; Attempts++; var response = new HttpResponseMessage(status) { Content = new StringContent(status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices ? "{\"invoice_id\":\"inv_1\",\"project_id\":\"prj_1\",\"status\":\"awaiting_payment\",\"is_final\":false,\"is_test\":true,\"payment_url\":\"https://pay.paymos.io/i/inv_1\",\"order\":{\"external_id\":\"order_1\",\"amount\":\"10.00\",\"currency\":\"USD\"},\"created_at\":1700000000,\"updated_at\":1700000000}" : $"{{\"type\":\"about:blank\",\"title\":\"{status}\",\"status\":{(int)status},\"detail\":\"retry later\",\"code\":\"{(status == HttpStatusCode.ServiceUnavailable ? "unavailable" : "rate_limited")}\"}}") }; if (RetryAfterSeconds is { } seconds) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)); return Task.FromResult(response); }
}
sealed class ProblemHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}
