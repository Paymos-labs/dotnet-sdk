# Paymos .NET SDK

Official .NET 8+ client for the Paymos Merchant API. Uses `HttpClient`, supports
`CancellationToken`, bounded `IAsyncEnumerable` cursor traversal, structured
errors, safe retries, HMAC signing, and raw-body webhook verification.

```bash
dotnet add package Paymos
```

```csharp
using var paymos = new PaymosClient("pk_test_...", "sk_test_...");
var invoice = await paymos.Invoices.CreateAsync(new CreateInvoiceRequest(
    ProjectId: "prj_...",
    Amount: "10.00",
    Currency: "USD",
    ExternalOrderId: "order_123"));
```

Every operation returns strongly typed records. `ListAsync` returns one
`Page<T>`; `IterateAsync` exposes a bounded `IAsyncEnumerable<T>` that follows
cursors automatically. API failures throw `PaymosApiException` and
preserve status, problem code, field, response headers, body, error kind, and
`Retry-After`.

```csharp
var verifier = new WebhookVerifier(
    Environment.GetEnvironmentVariable("PAYMOS_WEBHOOK_SECRET")!);
var webhook = verifier.ConstructEvent<JsonElement>(signatureHeader, rawRequestBody);
```

Pass the exact request bytes to the verifier before parsing JSON. Never expose
the API secret to browser or mobile code. Full documentation:
https://paymos.io/docs/server-sdks
