# Paymos .NET SDK

Official .NET 8+ client for the Paymos Merchant API, including static per-customer
wallets and their confirmed-deposit feed. Uses `HttpClient`, supports
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

A payment channel is one payer's reusable set of deposit addresses. Create it,
read its rails, then poll the confirmed-deposit feed and persist the cursor:

```csharp
var channel = await paymos.PaymentChannels.CreateAsync(
    new CreatePaymentChannelRequest(ProjectId: "prj_...", ExternalId: "customer_42"));

foreach (var rail in channel.Networks)
{
    if (rail.Address is null) continue; // this rail has not provisioned yet
    Console.WriteLine($"{rail.Network} {rail.Status} {rail.Address}");
}

var feed = await paymos.PaymentChannelDeposits.ReadAsync(
    new PaymentChannelDepositFeedOptions(Cursor: savedCursor, Limit: 100));
foreach (var deposit in feed.Items) Credit(deposit);
await SaveCursorAsync(feed.NextCursor);
```

Four things that bite a caller who guesses. Repeating the same `ExternalId`
returns the **same** channel — `200` instead of `201`, and both bodies are a
channel, so calling this on every checkout is safe: a repeat is not a duplicate
and not an error. A rail's `Address` is null until that rail finishes
provisioning and never changes once set, so null means "not yet", not "no
address". A null `MinimumDeposit` means "we cannot quote a minimum right now",
never "there is no minimum" — reading it as zero is how a merchant accepts a
deposit that lands below the live minimum and is never credited. And
`NextCursor` is never empty, not even on a page with no items: store it and
resume from it, and never loop until it is null the way `IterateAsync` ends a
list, because that loop either spins forever or stops on the first quiet page
and leaves reconciliation silently behind. `ConfirmedFrom` is the first poll's
lower bound only; afterwards the stored cursor is the resume mechanism.

```csharp
var verifier = new WebhookVerifier(
    Environment.GetEnvironmentVariable("PAYMOS_WEBHOOK_SECRET")!);
var webhook = verifier.ConstructEvent<JsonElement>(signatureHeader, rawRequestBody);
```

Pass the exact request bytes to the verifier before parsing JSON. Never expose
the API secret to browser or mobile code.

The three `payment_channel.deposit.*` events each carry a full `PaymentChannelDeposit`
as their `data`, so pass it as the type argument — the same generic `ConstructEvent<TData>`
above needs no new decoding code:

```csharp
var depositEvent = verifier.ConstructEvent<PaymentChannelDeposit>(signatureHeader, rawRequestBody);
if (depositEvent.EventType == "payment_channel.deposit.confirmed" && depositEvent.Data.IsFinal)
{
    Credit(depositEvent.Data.PaymentChannelExternalId, depositEvent.Data.Net); // idempotent by depositEvent.EventId
}
```

`confirming` and `reorged` are advisory and may arrive out of order — a `confirming` can
land after the `confirmed` for the same deposit, and a `reorged` can be superseded by a
later `confirmed`. Credit only on `payment_channel.deposit.confirmed` with `IsFinal`
true, and never let an advisory event regress a deposit already known to be confirmed.
Crediting on `confirming` releases goods against money a reorg can still take back.

Full documentation:
https://paymos.io/docs/server-sdks

## 401 and 403 are different problems

A **401** means the credential is absent, malformed or unverifiable — fix the key or the signing
clock, and stop retrying.

A **403** means the credential was accepted and something else refused. It covers two very
different situations:

- **the key is wrong for this call** — `payout_key_required`, `payment_key_required`,
  `forbidden`;
- **the key is fine, the account or project state says no** — `whitelist_required`,
  `merchant_suspended`, `widget_inactive`, `terminal_not_enabled`, `not_sandbox`.

A customer entering a payout address that is not whitelisted yet is the second kind: nothing is
wrong with your credentials, and paging the on-call engineer about them is the wrong move. Always
switch on the error `code` before deciding what to do.

`PaymosApiException.Kind` is `"permission"` for 403 and `"authentication"` for 401. Both remain the
same exception type — only the category string tells them apart.
