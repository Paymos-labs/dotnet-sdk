using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Paymos;

internal static class RefundsTests
{
    public static async Task<int> RunAsync(JsonElement vectors)
    {
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("simulate terminal status preserves current refund fields", SimulateAsync),
            ("full create and same-key replay", () => CreateAndReplayAsync(vectors)),
            ("overpayment 20 then full 100", () => OverpaymentThenFullAsync(vectors)),
            ("changed mode returns structured 409", () => ChangedModeAsync(vectors)),
            ("foreign id returns 404", () => NotFoundAsync(vectors, "wdr_foreign")),
            ("missing id returns 404", () => NotFoundAsync(vectors, "wdr_missing")),
            ("list filters and cursor traversal", () => PaginationAsync(vectors)),
            ("get preserves wire strings and refreshes state", GetAsync),
            ("invalid cursor returns structured 400", () => InvalidCursorAsync(vectors)),
            ("validation retains top-level and nested errors", ValidationErrorsAsync),
            ("unknown transport outcome reuses key", UnknownOutcomeAsync),
            ("503 does not retry create", UnavailableAsync),
            ("429 retries the identical request", RateLimitedAsync),
            ("sandbox refusal stays structured", SandboxRefusedAsync),
            ("repeated cursor is rejected", RepeatedCursorAsync),
            ("cursor traversal respects page bound", PageBoundAsync),
            ("create validates inputs before transport", InvalidInputAsync)
        };
        var failed = 0;
        foreach (var (name, run) in cases)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS refunds: {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL refunds: {name}: {error}");
            }
        }
        Console.WriteLine($"Paymos .NET refunds: {cases.Length - failed} passed, {failed} failed");
        return failed;
    }

    private static async Task SimulateAsync()
    {
        const string address = "UQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPuwA";
        foreach (var status in new[] { "completed", "failed", "cancelled" })
        {
            using var handler = new RefundHandler();
            handler.Enqueue(200, JsonSerializer.Serialize(new
            {
                withdrawal_id = "wdr_1", invoice_id = "inv_1", mode = "full", external_order_id = "key",
                status, is_final = true, is_test = true, amount = "20", currency = "USDT", network = "TON",
                destination_address = address, first_sender = address, created_at = 1700000000L,
                type = "refund", project_id = "prj_1"
            }));
            using var http = new HttpClient(handler);
            using var client = Client(http);
            var result = await client.Refunds.SimulateAsync("wdr/1 ?", status);
            Equal(status, result.Status.Value);
            Equal(address, result.DestinationAddress);
            Equal(address, result.FirstSender);
            Equal("refund", result.Type);
            Equal("prj_1", result.ProjectId);
            Equal("POST /v1/sandbox/refunds/wdr%2F1%20%3F/simulate", handler.Paths[0]);
            using var body = JsonDocument.Parse(handler.Bodies[0]);
            Equal(1, body.RootElement.EnumerateObject().Count());
            Equal(status, body.RootElement.GetProperty("status").GetString());
            handler.Complete(1);
        }
    }

    private static async Task CreateAndReplayAsync(JsonElement vectors)
    {
        var create = vectors.GetProperty("create");
        var body = create.GetProperty("body");
        var invoiceId = Text(body, "invoice_id");
        var key = Text(body, "external_order_id");
        using var handler = new RefundHandler();
        handler.Enqueue(create.GetProperty("new_status").GetInt32(), Wire("wdr_full", invoiceId, "full", key, "120"));
        handler.Enqueue(create.GetProperty("replay_status").GetInt32(), Wire("wdr_full", invoiceId, "full", key, "120"));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var created = await client.Refunds.CreateAsync(invoiceId, RefundMode.Full, key);
        var replay = await client.Refunds.CreateAsync(invoiceId, RefundMode.Full, key);
        Equal("120", created.Amount);
        Equal(RefundMode.Full, created.Mode);
        Equal("wdr_full", created.WithdrawalId);
        Equal(invoiceId, created.InvoiceId);
        Equal(key, created.ExternalOrderId);
        Equal(WithdrawalStatus.Created, created.Status);
        True(created.IsTest && !created.IsFinal);
        Equal(created, replay);
        Equal($"{Text(create, "method")} {Text(create, "path")}", handler.Paths[0]);
        Equal(handler.Bodies[0], handler.Bodies[1]);
        AssertCreateBody(handler.Bodies[0], body, create.GetProperty("rejected_fields"));
        handler.Complete(2);
    }

    private static async Task OverpaymentThenFullAsync(JsonElement vectors)
    {
        var overpayment = vectors.GetProperty("overpayment");
        var body = overpayment.GetProperty("body");
        var invoiceId = Text(body, "invoice_id");
        var key = Text(body, "external_order_id");
        using var handler = new RefundHandler();
        handler.Enqueue(201, Wire("wdr_over", invoiceId, "overpayment", key, "20"));
        handler.Enqueue(200, Wire("wdr_over", invoiceId, "overpayment", key, "20", completed: true));
        handler.Enqueue(201, Wire("wdr_remaining", invoiceId, "full", "remaining-full", "100"));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var over = await client.Refunds.CreateAsync(invoiceId, RefundMode.Overpayment, key);
        Equal("20", over.Amount);
        Equal(RefundMode.Overpayment, over.Mode);
        AssertCreateBody(handler.Bodies[0], body, vectors.GetProperty("create").GetProperty("rejected_fields"));
        var completed = await client.Withdrawals.SimulateCompletionAsync(over.WithdrawalId);
        Equal(WithdrawalStatus.Completed, completed.Status);
        True(completed.IsFinal && completed.IsTest);
        Equal("POST /v1/sandbox/withdrawals/wdr_over/simulate-completion", handler.Paths[1]);
        Equal("", handler.Bodies[1]);
        var full = await client.Refunds.CreateAsync(invoiceId, RefundMode.Full, "remaining-full");
        Equal("100", full.Amount);
        Equal(RefundMode.Full, full.Mode);
        Equal("wdr_remaining", full.WithdrawalId);
        handler.Complete(3);
    }

    private static async Task ChangedModeAsync(JsonElement vectors)
    {
        var body = vectors.GetProperty("create").GetProperty("body");
        var invoiceId = Text(body, "invoice_id");
        var key = Text(body, "external_order_id");
        using var handler = new RefundHandler();
        handler.Enqueue(201, Wire("wdr_full", invoiceId, "full", key, "120"));
        var status = vectors.GetProperty("create").GetProperty("changed_mode_status").GetInt32();
        var problem = Problem(status, "withdrawal_idempotency_conflict", "external_order_id");
        handler.Enqueue(status, problem);
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await client.Refunds.CreateAsync(invoiceId, RefundMode.Full, key);
        var error = await ThrowsAsync<PaymosApiException>(() => client.Refunds.CreateAsync(invoiceId, RefundMode.Overpayment, key));
        AssertProblem(error, status, "withdrawal_idempotency_conflict", "conflict", problem);
        Equal("external_order_id", error.Field);
        using var changedBody = JsonDocument.Parse(handler.Bodies[1]);
        Equal(key, Text(changedBody.RootElement, "external_order_id"));
        Equal("overpayment", Text(changedBody.RootElement, "mode"));
        handler.Complete(2);
    }

    private static async Task NotFoundAsync(JsonElement vectors, string id)
    {
        var status = vectors.GetProperty("get").GetProperty("foreign_or_missing_status").GetInt32();
        var problem = Problem(status, "not_found");
        using var handler = new RefundHandler();
        handler.Enqueue(status, problem);
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var error = await ThrowsAsync<PaymosApiException>(() => client.Refunds.GetAsync(id));
        AssertProblem(error, status, "not_found", "not_found", problem);
        Equal($"{Text(vectors.GetProperty("get"), "method")} /v1/refunds/{id}", handler.Paths[0]);
        handler.Complete(1);
    }

    private static async Task PaginationAsync(JsonElement vectors)
    {
        using var handler = new RefundHandler();
        var offset = 0;
        var sizes = vectors.GetProperty("list").GetProperty("page_sizes").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        for (var page = 0; page < sizes.Length; page++)
        {
            var items = Enumerable.Range(offset, sizes[page])
                .Select(index => Wire($"wdr_{index}", "inv_1", "full", $"key-{index}", "120"));
            var next = page < sizes.Length - 1 ? $"cur/{page + 1}+" : null;
            handler.Enqueue(200, Page(items, next));
            offset += sizes[page];
        }
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var options = new RefundListOptions(Limit: 100, InvoiceId: "inv_1",
            Status: [WithdrawalStatus.Completed, WithdrawalStatus.Created],
            CreatedFrom: "2026-10-01T00:00:00Z", CreatedTo: "2026-11-01T03:00:00+03:00");
        var ids = new List<string>();
        await foreach (var refund in client.Refunds.IterateAsync(options))
            ids.Add(refund.WithdrawalId);
        Equal(205, ids.Count);
        True(ids.SequenceEqual(Enumerable.Range(0, 205).Select(index => $"wdr_{index}")));
        const string prefix = "GET /v1/refunds?created_from=2026-10-01T00%3A00%3A00Z&created_to=2026-11-01T03%3A00%3A00%2B03%3A00";
        const string suffix = "&invoice_id=inv_1&limit=100&status=completed&status=created";
        Equal(prefix + suffix, handler.Paths[0]);
        Equal(prefix + "&cursor=cur%2F1%2B" + suffix, handler.Paths[1]);
        Equal(prefix + "&cursor=cur%2F2%2B" + suffix, handler.Paths[2]);
        Equal<string?>(null, options.Cursor);
        handler.Complete(3);
    }

    private static async Task GetAsync()
    {
        const string amount = "123456789012345678901234567890.123456789012345678";
        using var handler = new RefundHandler();
        handler.Enqueue(200, Wire("wdr_1", "inv_1", "overpayment", "key", amount));
        handler.Enqueue(200, Wire("wdr_1", "inv_1", "overpayment", "key", amount, completed: true));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var created = await client.Refunds.GetAsync("wdr/1 2");
        var completed = await client.Refunds.GetAsync("wdr/1 2");
        Equal("GET /v1/refunds/wdr%2F1%202", handler.Paths[0]);
        Equal(handler.Paths[0], handler.Paths[1]);
        Equal(amount, created.Amount);
        Equal("0.000000000000000001", created.Fee);
        Equal("USDT", created.Currency);
        Equal("TRC20", created.Network);
        Equal("TFirstSender", created.FirstSender);
        Equal(created.FirstSender, created.DestinationAddress);
        Equal<string?>(null, created.Type);
        Equal<string?>(null, created.ProjectId);
        Equal(1700000000L, created.CreatedAt);
        Equal<long?>(null, created.CompletedAt);
        Equal<string?>(null, created.TxHash);
        Equal<string?>(null, created.ExplorerUrl);
        Equal<long?>(null, created.FailedAt);
        Equal<long?>(null, created.CancelledAt);
        Equal(WithdrawalStatus.Created, created.Status);
        Equal(WithdrawalStatus.Completed, completed.Status);
        Equal<long?>(1700000005L, completed.CompletedAt);
        True(completed.IsFinal);
        handler.Complete(2);
    }

    private static async Task InvalidCursorAsync(JsonElement vectors)
    {
        var status = vectors.GetProperty("list").GetProperty("invalid_cursor_status").GetInt32();
        var problem = Problem(status, "pagination_cursor_invalid", "cursor");
        using var handler = new RefundHandler();
        handler.Enqueue(status, problem);
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var error = await ThrowsAsync<PaymosApiException>(() => client.Refunds.ListAsync(new(Cursor: "foreign-cursor")));
        AssertProblem(error, status, "pagination_cursor_invalid", "validation", problem);
        Equal("cursor", error.Field);
        handler.Complete(1);
    }

    private static async Task UnknownOutcomeAsync()
    {
        using var handler = new RefundHandler { TransportFailure = new HttpRequestException("Connection lost.") };
        handler.Enqueue(200, Wire("wdr_saved", "inv_1", "full", "stable-key", "120"));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await ThrowsAsync<HttpRequestException>(() => client.Refunds.CreateAsync("inv_1", RefundMode.Full, "stable-key"));
        Equal(1, handler.Paths.Count);
        var replay = await client.Refunds.CreateAsync("inv_1", RefundMode.Full, "stable-key");
        Equal("wdr_saved", replay.WithdrawalId);
        Equal(handler.Bodies[0], handler.Bodies[1]);
        handler.Complete(2);
    }

    private static async Task ValidationErrorsAsync()
    {
        using var handler = new RefundHandler();
        var problem = Problem(400, "validation_failed", multiple: true);
        handler.Enqueue(400, problem);
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var error = await ThrowsAsync<PaymosApiException>(() => client.Refunds.CreateAsync("inv_1", RefundMode.Full, "key"));
        AssertProblem(error, 400, "validation_failed", "validation", problem, multiple: true);
        Equal<string?>(null, error.Field);
        Equal("field_invalid_enum", error.Errors[0].Code);
        Equal("mode", error.Errors[0].Field);
        Equal("Invalid mode.", error.Errors[0].Message);
        Equal("field_required", error.Errors[1].Code);
        Equal("external_order_id", error.Errors[1].Field);
        handler.Complete(1);
    }

    private static async Task UnavailableAsync()
    {
        using var handler = new RefundHandler();
        var problem = Problem(503, "unavailable");
        handler.Enqueue(503, problem);
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var error = await ThrowsAsync<PaymosApiException>(() => client.Refunds.CreateAsync("inv_1", RefundMode.Full, "stable-key"));
        AssertProblem(error, 503, "unavailable", "unavailable", problem);
        handler.Complete(1);
    }

    private static async Task RateLimitedAsync()
    {
        using var handler = new RefundHandler();
        handler.Enqueue(429, Problem(429, "rate_limited"));
        handler.Enqueue(201, Wire("wdr_1", "inv_1", "full", "stable-key", "120"));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await client.Refunds.CreateAsync("inv_1", RefundMode.Full, "stable-key");
        Equal(handler.Bodies[0], handler.Bodies[1]);
        handler.Complete(2);
    }

    private static async Task SandboxRefusedAsync()
    {
        using var handler = new RefundHandler();
        var problem = Problem(403, "not_sandbox");
        handler.Enqueue(403, problem);
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var error = await ThrowsAsync<PaymosApiException>(() => client.Withdrawals.SimulateCompletionAsync("wdr_1"));
        AssertProblem(error, 403, "not_sandbox", "permission", problem);
        handler.Complete(1);
    }

    private static async Task RepeatedCursorAsync()
    {
        using var handler = new RefundHandler();
        handler.Enqueue(200, Page([], "same"));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await ThrowsAsync<PaymosException>(async () =>
        {
            await foreach (var _ in client.Refunds.IterateAsync(new(Cursor: "same"))) { }
        });
        handler.Complete(1);
    }

    private static async Task PageBoundAsync()
    {
        using var handler = new RefundHandler();
        handler.Enqueue(200, Page([Wire("wdr_1", "inv_1", "full", "key", "120")], "next"));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var count = 0;
        await foreach (var _ in client.Refunds.IterateAsync(maxPages: 1))
            count++;
        Equal(1, count);
        handler.Complete(1);
    }

    private static async Task InvalidInputAsync()
    {
        using var handler = new RefundHandler();
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await ThrowsAsync<ArgumentException>(() => client.Refunds.CreateAsync("", RefundMode.Full, "key"));
        await ThrowsAsync<ArgumentException>(() => client.Refunds.CreateAsync("inv_1", RefundMode.Full, " "));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.Refunds.CreateAsync("inv_1", (RefundMode)99, "key"));
        await ThrowsAsync<ArgumentException>(() => client.Refunds.GetAsync(""));
        handler.Complete(0);
    }

    private static PaymosClient Client(HttpClient http) =>
        new("pk_test_key", "sk_test_secret", http, timeProvider: new RefundTimeProvider(), baseDelay: TimeSpan.Zero);

    private static string Wire(string id, string invoiceId, string mode, string key, string amount, bool completed = false) =>
        JsonSerializer.Serialize(new
        {
            withdrawal_id = id, invoice_id = invoiceId, mode, external_order_id = key,
            status = completed ? "completed" : "created", is_final = completed, is_test = true,
            amount, fee = "0.000000000000000001", currency = "USDT", network = "TRC20",
            destination_address = "TFirstSender", first_sender = "TFirstSender",
            created_at = 1700000000L, completed_at = completed ? 1700000005L : (long?)null
        });

    private static string Page(IEnumerable<string> items, string? cursor) =>
        $"{{\"items\":[{string.Join(",", items)}],\"next_cursor\":{JsonSerializer.Serialize(cursor)}}}";

    private static string Problem(int status, string code, string? field = null, bool multiple = false)
    {
        var problem = new Dictionary<string, object?>
        {
            ["type"] = "about:blank", ["title"] = "Request failed", ["status"] = status,
            ["detail"] = "Refund request failed.", ["code"] = code, ["trace_id"] = "refund-trace"
        };
        if (field is not null) problem["field"] = field;
        if (multiple) problem["errors"] = new[]
        {
            new { code = "field_invalid_enum", field = "mode", message = "Invalid mode." },
            new { code = "field_required", field = "external_order_id", message = "Order id is required." }
        };
        return JsonSerializer.Serialize(problem);
    }

    private static void AssertCreateBody(string raw, JsonElement expected, JsonElement rejectedFields)
    {
        using var document = JsonDocument.Parse(raw);
        var body = document.RootElement;
        Equal(3, body.EnumerateObject().Count());
        foreach (var field in expected.EnumerateObject())
            Equal(field.Value.GetString(), Text(body, field.Name));
        foreach (var field in rejectedFields.EnumerateArray())
            True(!body.TryGetProperty(field.GetString()!, out _));
    }

    private static void AssertProblem(PaymosApiException error, int status, string code, string kind, string body, bool multiple = false)
    {
        Equal(status, error.StatusCode);
        Equal<int?>(status, error.ProblemStatus);
        Equal("about:blank", error.Type);
        Equal("Request failed", error.Title);
        Equal("Refund request failed.", error.Detail);
        Equal(code, error.Code);
        Equal(kind, error.Kind);
        Equal(body, error.ResponseBody);
        Equal("refund-trace", error.TraceId);
        Equal(multiple ? 2 : 0, error.Errors.Count);
        var cache = System.Net.Http.Headers.CacheControlHeaderValue.Parse(error.Headers["cache-control"]);
        True(cache.Private && cache.NoStore);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private sealed class RefundTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000);
    }

    private sealed class RefundHandler : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _responses = new();
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        public Exception? TransportFailure { get; set; }

        public void Enqueue(int status, string body) => _responses.Enqueue((status, body));

        public void Complete(int requests)
        {
            Equal(requests, Paths.Count);
            Equal(0, _responses.Count);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var uri = request.RequestUri!;
            Paths.Add($"{request.Method.Method} {uri.PathAndQuery}");
            Bodies.Add(body);
            var timestamp = request.Headers.GetValues("X-Request-Timestamp").Single();
            Equal("1700000000", timestamp);
            var hash = body.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var payload = $"{timestamp}\n{request.Method.Method}\n{uri.AbsolutePath}\n{uri.Query}\n{hash}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("sk_test_secret"));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            Equal($"HMAC-SHA256 pk_test_key:{signature}", request.Headers.GetValues("Authorization").Single());
            if (TransportFailure is { } failure)
            {
                TransportFailure = null;
                throw failure;
            }
            if (_responses.Count == 0)
                throw new Exception($"No stubbed response for {Paths[^1]}.");
            var (status, responseBody) = _responses.Dequeue();
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, status >= 400 ? "application/problem+json" : "application/json")
            };
            response.Headers.TryAddWithoutValidation("Cache-Control", "private, no-store");
            if (status == 429)
                response.Headers.RetryAfter = new(TimeSpan.Zero);
            return response;
        }
    }
}
