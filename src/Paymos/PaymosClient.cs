using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paymos;

public sealed class PaymosClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _time;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public InvoiceResource Invoices { get; }
    public WithdrawalResource Withdrawals { get; }
    public BalanceResource Balances { get; }
    public SystemResource System { get; }

    public PaymosClient(
        string apiKey,
        string apiSecret,
        HttpClient? httpClient = null,
        Uri? baseUri = null,
        TimeProvider? timeProvider = null,
        int maxRetries = 2,
        TimeSpan? baseDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        if (baseDelay is { } configuredDelay && configuredDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay));

        _apiKey = apiKey;
        _apiSecret = apiSecret;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _baseUri = baseUri ?? new Uri("https://api.paymos.io", UriKind.Absolute);
        if (!_baseUri.IsAbsoluteUri)
            throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));
        _time = timeProvider ?? TimeProvider.System;
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(150);

        Invoices = new InvoiceResource(this);
        Withdrawals = new WithdrawalResource(this);
        Balances = new BalanceResource(this);
        System = new SystemResource(this);
    }

    internal async Task<T> SendAsync<T>(
        string method,
        string path,
        object? payload = null,
        string query = "",
        CancellationToken cancellationToken = default)
    {
        var body = payload is null ? "" : JsonSerializer.Serialize(payload, Json);
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            var timestamp = _time.GetUtcNow().ToUnixTimeSeconds()
                .ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(_baseUri, path + query));
            if (body.Length > 0)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                RequestSigner.AuthorizationHeader(_apiKey, _apiSecret, timestamp, method, path, query, body));
            request.Headers.TryAddWithoutValidation("X-Request-Timestamp", timestamp);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd($"paymos-dotnet/{SdkVersion.Value}");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _maxRetries && IsSafe(method))
            {
                await DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var responseBody = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (attempt < _maxRetries &&
                    (response.StatusCode == HttpStatusCode.TooManyRequests ||
                     ((int)response.StatusCode >= 500 && IsSafe(method))))
                {
                    await DelayAsync(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new PaymosApiException((int)response.StatusCode, responseBody, response.Headers);
                if (responseBody.Length == 0)
                    throw new PaymosException("Paymos API returned an empty response.");

                try
                {
                    return JsonSerializer.Deserialize<T>(responseBody, Json)
                        ?? throw new PaymosException("Paymos API returned a null JSON response.");
                }
                catch (JsonException error)
                {
                    throw new PaymosException("Paymos API returned invalid JSON.", error);
                }
            }
        }

        throw new InvalidOperationException("Unreachable retry state.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private TimeSpan Backoff(int attempt) => _baseDelay * Math.Pow(2, attempt);

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - _time.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
        return Backoff(attempt);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _time, cancellationToken);

    private static bool IsSafe(string method) => method is "GET" or "HEAD" or "OPTIONS";
}

public sealed class InvoiceResource(PaymosClient client)
{
    public Task<Invoice> CreateAsync(CreateInvoiceRequest payload, CancellationToken cancellationToken = default) =>
        client.SendAsync<Invoice>("POST", "/v1/invoices", payload, cancellationToken: cancellationToken);

    public Task<Invoice> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return client.SendAsync<Invoice>(
            "GET", $"/v1/invoices/{RequestSigner.EncodePathSegment(id)}", cancellationToken: cancellationToken);
    }

    public Task<Page<InvoiceListItem>> ListAsync(
        InvoiceListOptions? options = null,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<Page<InvoiceListItem>>(
            "GET", "/v1/invoices", query: RequestSigner.BuildQuery(options), cancellationToken: cancellationToken);

    public Task<Invoice> CancelAsync(string id, string reason, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ValidateReason(reason);
        return client.SendAsync<Invoice>(
            "POST", $"/v1/invoices/{RequestSigner.EncodePathSegment(id)}/cancel", new { reason },
            cancellationToken: cancellationToken);
    }

    public Task<Invoice> ConfirmPaymentAsync(
        string id,
        string currency,
        string network,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        return client.SendAsync<Invoice>(
            "POST", $"/v1/invoices/{RequestSigner.EncodePathSegment(id)}/confirm-payment",
            new { currency, network }, cancellationToken: cancellationToken);
    }

    public Task<Invoice> SimulatePaymentAsync(
        string id,
        string stage,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (stage is not ("paid" or "overpaid" or "underpay" or "cancel"))
            throw new ArgumentException("Invalid simulation stage.", nameof(stage));
        return client.SendAsync<Invoice>(
            "POST", $"/v1/sandbox/invoices/{RequestSigner.EncodePathSegment(id)}/simulate-payment",
            new { stage }, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<InvoiceListItem> IterateAsync(
        InvoiceListOptions? options = null,
        int maxPages = 100,
        CancellationToken cancellationToken = default) =>
        Cursor.Iterate(
            ListAsync,
            options ?? new InvoiceListOptions(),
            value => value.Cursor,
            (value, cursor) => value with { Cursor = cursor },
            maxPages,
            cancellationToken);

    private static void ValidateId(string value) => ArgumentException.ThrowIfNullOrWhiteSpace(value);

    private static void ValidateReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500)
            throw new ArgumentException("Cancellation reason must contain 1 to 500 characters.", nameof(value));
    }
}

public sealed class WithdrawalResource(PaymosClient client)
{
    public Task<Withdrawal> CreateAsync(
        CreateWithdrawalRequest payload,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<Withdrawal>("POST", "/v1/withdrawals", payload, cancellationToken: cancellationToken);

    public Task<Withdrawal> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return client.SendAsync<Withdrawal>(
            "GET", $"/v1/withdrawals/{RequestSigner.EncodePathSegment(id)}", cancellationToken: cancellationToken);
    }

    public Task<Page<Withdrawal>> ListAsync(
        WithdrawalListOptions? options = null,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<Page<Withdrawal>>(
            "GET", "/v1/withdrawals", query: RequestSigner.BuildQuery(options), cancellationToken: cancellationToken);

    public Task<Withdrawal> CancelAsync(string id, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("Cancellation reason must contain 1 to 500 characters.", nameof(reason));
        return client.SendAsync<Withdrawal>(
            "POST", $"/v1/withdrawals/{RequestSigner.EncodePathSegment(id)}/cancel", new { reason },
            cancellationToken: cancellationToken);
    }

    public Task<Withdrawal> SimulateCompletionAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return client.SendAsync<Withdrawal>(
            "POST", $"/v1/sandbox/withdrawals/{RequestSigner.EncodePathSegment(id)}/simulate-completion",
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<Withdrawal> IterateAsync(
        WithdrawalListOptions? options = null,
        int maxPages = 100,
        CancellationToken cancellationToken = default) =>
        Cursor.Iterate(
            ListAsync,
            options ?? new WithdrawalListOptions(),
            value => value.Cursor,
            (value, cursor) => value with { Cursor = cursor },
            maxPages,
            cancellationToken);
}

public sealed class BalanceResource(PaymosClient client)
{
    public Task<IReadOnlyList<Balance>> GetAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<Balance>>("GET", "/v1/balances", cancellationToken: cancellationToken);
}

public sealed class SystemResource(PaymosClient client)
{
    public Task<ServerTime> TimeAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<ServerTime>("GET", "/v1/time", cancellationToken: cancellationToken);
}

internal static class Cursor
{
    public static async IAsyncEnumerable<TItem> Iterate<TItem, TOptions>(
        Func<TOptions?, CancellationToken, Task<Page<TItem>>> fetch,
        TOptions initial,
        Func<TOptions, string?> readCursor,
        Func<TOptions, string, TOptions> writeCursor,
        int maxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TOptions : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);

        var options = initial;
        var cursor = readCursor(options);
        for (var pageNumber = 0; pageNumber < maxPages; pageNumber++)
        {
            var page = await fetch(options, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
                yield return item;
            if (string.IsNullOrEmpty(page.NextCursor))
                yield break;
            if (page.NextCursor == cursor)
                throw new PaymosException("Paymos API returned the same pagination cursor twice.");
            cursor = page.NextCursor;
            options = writeCursor(options, cursor);
        }
    }
}
