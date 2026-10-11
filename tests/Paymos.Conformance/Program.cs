using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Paymos;

using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract.json")));
var vectors = contract.RootElement.GetProperty("vectors");
var post = vectors.GetProperty("post_signing");
Equal(post.GetProperty("authorization").GetString(), RequestSigner.AuthorizationHeader(
    Text(post, "api_key"), Text(post, "api_secret"), Text(post, "timestamp"), Text(post, "method"), Text(post, "path"), Text(post, "query"), Text(post, "body")));
// An absent body signs an EMPTY last segment, never sha256(""). This vector is a POST, so an SDK
// that shortcuts on the method ("only GET is empty") fails here.
var emptyBody = vectors.GetProperty("post_empty_body_signing");
Equal(emptyBody.GetProperty("authorization").GetString(), RequestSigner.AuthorizationHeader(
    Text(emptyBody, "api_key"), Text(emptyBody, "api_secret"), Text(emptyBody, "timestamp"), Text(emptyBody, "method"), Text(emptyBody, "path"), Text(emptyBody, "query"), Text(emptyBody, "body")));
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
True(handler.UserAgents.All(x => x == $"paymos-dotnet/{SdkVersion.Value}"));

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
// Payment channels. Two paginations live here and they are NOT the same: the channel list is an
// ordinary keyset page whose cursor is null on the last page, while the deposit endpoint is a FEED
// whose cursor is always present — even on an empty page — because feed positions are global and a
// filtered poller still has to advance past the ones it skipped.
var channelHandler = new ScriptedHandler();
var channelClient = new PaymosClient("pk", "sk", new HttpClient(channelHandler), timeProvider: new FixedTimeProvider(), maxRetries: 0);

// 1. Create twice returns the channel both times: 201 new, 200 existing, never an error.
channelHandler.Enqueue(HttpStatusCode.Created, ChannelJson("pc_1", true));
channelHandler.Enqueue(HttpStatusCode.OK, ChannelJson("pc_1", true));
var createdChannel = await channelClient.PaymentChannels.CreateAsync(new("prj_1", "customer-42"));
var replayedChannel = await channelClient.PaymentChannels.CreateAsync(new("prj_1", "customer-42"));
Equal("pc_1", createdChannel.Id); Equal("pc_1", replayedChannel.Id);
Equal("POST /v1/payment-channels", channelHandler.Paths[0]);
using (var createBody = JsonDocument.Parse(channelHandler.Bodies[0]))
{
    Equal("prj_1", createBody.RootElement.GetProperty("project_id").GetString());
    Equal("customer-42", createBody.RootElement.GetProperty("external_id").GetString());
}
// An absent address means "this rail has not provisioned yet"; an absent minimum means "cannot quote
// one right now" — never "no minimum". Both must arrive as null, not as an empty string.
Equal("TRC20", createdChannel.Networks[0].Network);
Equal("1.00", createdChannel.Networks[0].Tokens[0].MinimumDeposit);
Equal<string?>(null, createdChannel.Networks[0].Tokens[1].MinimumDeposit);
Equal<string?>(null, createdChannel.Networks[1].Address);

// 2. Block returns the UPDATED channel — it is not a void call.
channelHandler.Enqueue(HttpStatusCode.OK, ChannelJson("pc_1", false));
var blockedChannel = await channelClient.PaymentChannels.BlockAsync("pc_1");
True(!blockedChannel.IsAcceptingPayments);
Equal("POST /v1/payment-channels/pc_1/block", channelHandler.Paths[^1]);

// 8. Unblock is the mirror of it, and is asserted so it cannot quietly become a no-op.
channelHandler.Enqueue(HttpStatusCode.OK, ChannelJson("pc_1", true));
var unblockedChannel = await channelClient.PaymentChannels.UnblockAsync("pc_1");
True(unblockedChannel.IsAcceptingPayments);
Equal("POST /v1/payment-channels/pc_1/unblock", channelHandler.Paths[^1]);

// 3. An empty feed page still yields a cursor to resume from. This is the test that fails if someone
// modelled the feed on the invoice list, where an empty page means "you are done".
channelHandler.Enqueue(HttpStatusCode.OK, "{\"items\":[],\"next_cursor\":\"cur_2\"}");
var quietFeedPage = await channelClient.PaymentChannelDeposits.ReadAsync();
Equal(0, quietFeedPage.Items.Count); Equal("cur_2", quietFeedPage.NextCursor);
Equal<PaymentChannelDepositFeedBlockage?>(null, quietFeedPage.Blocked);
// ...and the feed has its OWN page type whose cursor is not nullable, so no caller can write
// `if (cursor is null) break` against it. The channel list's cursor is nullable precisely because
// that list does end. Asserted, because sharing one page type is the mistake this prevents.
var nullability = new System.Reflection.NullabilityInfoContext();
Equal(typeof(PaymentChannelDepositFeedPage), typeof(PaymentChannelDepositResource).GetMethod("ReadAsync")!.ReturnType.GetGenericArguments()[0]);
Equal(System.Reflection.NullabilityState.NotNull, nullability.Create(typeof(PaymentChannelDepositFeedPage).GetProperty("NextCursor")!).ReadState);
Equal(System.Reflection.NullabilityState.Nullable, nullability.Create(typeof(Page<PaymentChannel>).GetProperty("NextCursor")!).ReadState);

// 4. A blocked feed page surfaces WHICH deposit and WHY — otherwise a stuck feed is byte-identical
// to a quiet day and a merchant cannot tell "no new payments" from "I am not seeing my money".
channelHandler.Enqueue(HttpStatusCode.OK, "{\"items\":[],\"next_cursor\":\"cur_2\",\"blocked\":{\"deposit_id\":\"pcd_9\",\"reason\":\"not_confirmed\"}}");
var stalledFeedPage = await channelClient.PaymentChannelDeposits.ReadAsync();
Equal("pcd_9", stalledFeedPage.Blocked!.DepositId); Equal("not_confirmed", stalledFeedPage.Blocked.Reason);
Equal("cur_2", stalledFeedPage.NextCursor);

// 5. Every filter reaches the wire in snake_case. The API rejects unknown query fields outright, so a
// camelCase slip is a 400 at a merchant, not a failure in CI.
channelHandler.Enqueue(HttpStatusCode.OK, "{\"items\":[],\"next_cursor\":null}");
await channelClient.PaymentChannels.ListAsync(new(Limit: 2, Cursor: "cur_1", Status: ["blocked", "active"], ExternalId: "customer-42", ProjectId: "prj_1"));
Equal("GET /v1/payment-channels?cursor=cur_1&external_id=customer-42&limit=2&project_id=prj_1&status=active&status=blocked", channelHandler.Paths[^1]);
channelHandler.Enqueue(HttpStatusCode.OK, "{\"items\":[],\"next_cursor\":\"cur_2\"}");
await channelClient.PaymentChannelDeposits.ReadAsync(new(Limit: 2, Cursor: "cur_1", ProjectId: "prj_1", PaymentChannelId: "pc_1", ConfirmedFrom: 1700000000));
Equal("GET /v1/payment-channel-deposits?confirmed_from=1700000000&cursor=cur_1&limit=2&payment_channel_id=pc_1&project_id=prj_1", channelHandler.Paths[^1]);

// 6. Ids are PATH-encoded, not query-encoded: a form encoder would spell the space "+" and leave "/".
channelHandler.Enqueue(HttpStatusCode.OK, ChannelJson("pc_1", true));
await channelClient.PaymentChannels.GetAsync("pc/1 2");
Equal("GET /v1/payment-channels/pc%2F1%202", channelHandler.Paths[^1]);

// 7. Simulate sends the REQUIRED network, and omits stage entirely when unset — not null, not "".
channelHandler.Enqueue(HttpStatusCode.OK, DepositJson());
await channelClient.PaymentChannels.SimulateDepositAsync("pc_1", new("100.00", "USDT", "TRC20"));
Equal("POST /v1/sandbox/payment-channels/pc_1/simulate-deposit", channelHandler.Paths[^1]);
using (var simulateBody = JsonDocument.Parse(channelHandler.Bodies[^1]))
{
    Equal("TRC20", simulateBody.RootElement.GetProperty("network").GetString());
    Equal("100.00", simulateBody.RootElement.GetProperty("amount").GetString());
    True(!simulateBody.RootElement.TryGetProperty("stage", out _));
}
channelHandler.Enqueue(HttpStatusCode.OK, DepositJson());
await channelClient.PaymentChannels.SimulateDepositAsync("pc_1", new("100.00", "USDT", "TRC20", SimulateDepositStage.Confirming));
using (var stagedBody = JsonDocument.Parse(channelHandler.Bodies[^1]))
    Equal("confirming", stagedBody.RootElement.GetProperty("stage").GetString());

// The deposit itself: money is a decimal STRING, timestamps are integers, and absent members are null.
channelHandler.Enqueue(HttpStatusCode.OK, DepositJson());
var deposit = await channelClient.PaymentChannelDeposits.GetAsync("pcd_1");
Equal("GET /v1/payment-channel-deposits/pcd_1", channelHandler.Paths[^1]);
Equal("100.00", deposit.Gross); Equal("1.00", deposit.Fee); Equal("99.00", deposit.Net);
True(deposit.IsFinal); Equal(1700000005L, deposit.ConfirmedAt);
Equal<string?>(null, deposit.ExplorerUrl); Equal<string?>(null, deposit.SourceAddress);
Equal<long?>(null, deposit.FirstIncludedBlockTimestamp);

// 9. The channel LIST iterator terminates on a null cursor. The feed deliberately has no iterator at
// all — a helper that stops on an empty cursor would halt a merchant's reconciliation on the first
// quiet minute — and this asserts that no such helper has been added since.
channelHandler.Enqueue(HttpStatusCode.OK, $"{{\"items\":[{ChannelJson("pc_1", true)}],\"next_cursor\":\"cur_2\"}}");
channelHandler.Enqueue(HttpStatusCode.OK, $"{{\"items\":[{ChannelJson("pc_2", true)}],\"next_cursor\":null}}");
var iteratedChannels = new List<string>();
await foreach (var channel in channelClient.PaymentChannels.IterateAsync())
    iteratedChannels.Add(channel.Id);
Equal(new[] { "pc_1", "pc_2" }, iteratedChannels.ToArray());
Equal(0, channelHandler.Pending);
True(typeof(PaymentChannelDepositResource).GetMethods().All(
    method => !method.ReturnType.Name.StartsWith("IAsyncEnumerable", StringComparison.Ordinal)));

// A payment_channel.deposit.confirmed webhook carries the FULL deposit wire shape as `data` — the
// same shape PaymentChannelDeposits.GetAsync above already decodes — so the existing generic
// ConstructEvent<TData> needs no new decoding code; this proves it through the real verifier
// (HMAC-SHA256 over "{timestamp}.{body}"), not just JsonSerializer.Deserialize. This is a native-TRX
// deposit: it has no contract, no attributable source address, and no minted explorer link, so
// contract_address/source_address/explorer_url/first_included_block_timestamp are ABSENT from the
// JSON — never null — and must decode as null. See the ordering note on PaymentChannelDeposit:
// confirming/reorged are advisory and may arrive out of order; only confirmed+IsFinal may credit.
const string depositWebhookSecret = "whsec_test";
const long depositWebhookTimestamp = 1700000000L;
const string depositWebhookBody =
    "{\"event_id\":\"evt_2\",\"event_type\":\"payment_channel.deposit.confirmed\",\"version\":1," +
    "\"occurred_at\":1700000000,\"data\":{\"id\":\"pcd_9\",\"payment_channel_id\":\"pc_1\"," +
    "\"project_id\":\"prj_1\",\"payment_channel_external_id\":\"customer-42\",\"status\":\"confirmed\"," +
    "\"is_final\":true,\"is_test\":false,\"currency\":\"TRX\",\"network\":\"TRC20\"," +
    "\"chain_id\":728126428,\"gross\":\"100.00\",\"fee\":\"1.00\",\"net\":\"99.00\"," +
    "\"applied_fee_percent\":1,\"customer_fee_percent\":0,\"tx_hash\":\"9a2f1c\"," +
    "\"transfer_id\":\"trf_1\",\"destination_address\":\"TChannelAddress\",\"block_height\":60000000," +
    "\"created_at\":1700000000,\"updated_at\":1700000100,\"confirmed_at\":1700000100}}";
using var depositHmac = new HMACSHA256(Encoding.UTF8.GetBytes(depositWebhookSecret));
var depositSignature = Convert.ToHexString(
    depositHmac.ComputeHash(Encoding.UTF8.GetBytes($"{depositWebhookTimestamp}.{depositWebhookBody}")));
var depositVerifier = new WebhookVerifier(depositWebhookSecret);
True(depositVerifier.Verify(
    $"t={depositWebhookTimestamp},v1={depositSignature}",
    Encoding.UTF8.GetBytes(depositWebhookBody),
    DateTimeOffset.FromUnixTimeSeconds(depositWebhookTimestamp)));
var depositWebhookEvent = depositVerifier.ConstructEvent<PaymentChannelDeposit>(
    $"t={depositWebhookTimestamp},v1={depositSignature}",
    Encoding.UTF8.GetBytes(depositWebhookBody),
    DateTimeOffset.FromUnixTimeSeconds(depositWebhookTimestamp));
Equal("payment_channel.deposit.confirmed", depositWebhookEvent.EventType);
var webhookDeposit = depositWebhookEvent.Data;
Equal("100.00", webhookDeposit.Gross); Equal("1.00", webhookDeposit.Fee); Equal("99.00", webhookDeposit.Net);
True(webhookDeposit.IsFinal);
Equal<string?>(null, webhookDeposit.ContractAddress); Equal<string?>(null, webhookDeposit.SourceAddress);
Equal<string?>(null, webhookDeposit.ExplorerUrl); Equal<long?>(null, webhookDeposit.FirstIncludedBlockTimestamp);

Console.WriteLine("Paymos .NET conformance: PASS");

static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
static string ChannelJson(string id, bool acceptingPayments) =>
    $"{{\"id\":\"{id}\",\"project_id\":\"prj_1\",\"external_id\":\"customer-42\"," +
    $"\"status\":\"{(acceptingPayments ? "active" : "blocked")}\",\"is_accepting_payments\":{(acceptingPayments ? "true" : "false")}," +
    "\"is_fully_provisioned\":true,\"is_test\":true,\"applied_fee_percent\":1.5,\"customer_fee_percent\":0," +
    "\"networks\":[{\"network\":\"TRC20\",\"status\":\"active\",\"address\":\"TWd4Wr\",\"tokens\":[{\"symbol\":\"USDT\",\"minimum_deposit\":\"1.00\"},{\"symbol\":\"USDC\"}]}," +
    "{\"network\":\"BEP20\",\"status\":\"provisioning\",\"tokens\":[]}]," +
    "\"created_at\":1700000000,\"updated_at\":1700000000}";
// invoice-late-payment wire contract (late-26): Invoice decodes paid_late_at (Unix seconds | null |
// absent legacy) through the PUBLIC client path, beside an unchanged terminal status and payment.paid.
{
    const string MarkedJson = "{\"invoice_id\":\"inv_1\",\"project_id\":\"prj_1\",\"status\":\"expired\",\"is_final\":true,\"is_test\":false,\"payment_url\":\"https://pay.test/inv_1\",\"order\":{\"external_id\":\"order_1\",\"amount\":\"100.00\",\"currency\":\"USD\"},\"payment\":{\"currency\":\"USDT\",\"network\":\"tron\",\"chain_id\":728126428,\"expected\":\"100.00\",\"paid\":\"60.00\"},\"created_at\":1700000000,\"updated_at\":1700003600,\"expires_at\":1700003600,\"completed_at\":1700003600,\"paid_late_at\":1700007200}";

    Invoice LateInvoice(string json) { var late = new PaymosClient("pk", "sk", new HttpClient(new FixedJsonHandler(json)), timeProvider: new FixedTimeProvider()); return late.Invoices.GetAsync("inv_1").GetAwaiter().GetResult(); }

    var marked = LateInvoice(MarkedJson);
    Equal(marked.Status, InvoiceStatus.Expired);
    Equal(marked.IsFinal, true);
    Equal(marked.PaidLateAt, 1700007200L);
    Equal(marked.Payment!.Paid, "60.00");

    var nullMarked = LateInvoice(MarkedJson.Replace("\"paid_late_at\":1700007200", "\"paid_late_at\":null"));
    Equal((long?)nullMarked.PaidLateAt!, null);

    var legacy = LateInvoice(MarkedJson.Replace(",\"paid_late_at\":1700007200", ""));
    Equal((long?)legacy.PaidLateAt!, null);
    Equal(legacy.Status, InvoiceStatus.Expired);

    try { LateInvoice("{}"); throw new Exception("Expected required-field rejection"); } catch (Exception ex) { True(ex.Message.Length > 0); }
}

static string DepositJson() =>
    "{\"id\":\"pcd_1\",\"payment_channel_id\":\"pc_1\",\"project_id\":\"prj_1\",\"payment_channel_external_id\":\"customer-42\"," +
    "\"status\":\"confirmed\",\"is_final\":true,\"is_test\":true,\"currency\":\"USDT\",\"network\":\"TRC20\",\"chain_id\":728126428," +
    "\"gross\":\"100.00\",\"fee\":\"1.00\",\"net\":\"99.00\",\"applied_fee_percent\":1.0,\"customer_fee_percent\":0," +
    "\"tx_hash\":\"9f1c\",\"transfer_id\":\"trf_1\",\"destination_address\":\"TWd4Wr\",\"block_height\":64000000," +
    "\"created_at\":1700000000,\"updated_at\":1700000005,\"confirmed_at\":1700000005}";
static void Equal<T>(T expected, T actual) { if (expected is Array expectedArray && actual is Array actualArray) { if (!expectedArray.Cast<object?>().SequenceEqual(actualArray.Cast<object?>())) throw new Exception("Sequences differ"); return; } if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
sealed class FixedTimeProvider : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000); }
sealed class FixedJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
}
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
sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();
    public List<string> Paths { get; } = []; public List<string> Bodies { get; } = [];
    public int Pending => responses.Count;
    public void Enqueue(HttpStatusCode status, string body) => responses.Enqueue((status, body));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Paths.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        if (responses.Count == 0) throw new Exception($"No stubbed response for {Paths[^1]}");
        var (status, body) = responses.Dequeue();
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
sealed class ProblemHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}
