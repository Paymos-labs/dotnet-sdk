using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Paymos;

public sealed class WebhookVerifier
{
    private readonly byte[] _secret;
    private readonly TimeSpan _tolerance;

    public WebhookVerifier(string secret, TimeSpan? tolerance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _secret = Encoding.UTF8.GetBytes(secret);
        _tolerance = tolerance ?? TimeSpan.FromMinutes(5);
        if (_tolerance < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tolerance));
    }

    public bool Verify(string signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset? now = null)
    {
        try { AssertValid(signatureHeader, rawBody, now); return true; }
        catch (PaymosException) { return false; }
    }

    public void AssertValid(string signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset? now = null)
    {
        var (timestamp, signatures) = ParseHeader(signatureHeader);
        var current = now ?? DateTimeOffset.UtcNow;
        if ((current - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > _tolerance)
            throw new WebhookTimestampException("Webhook timestamp is outside the allowed tolerance.");
        var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
        var signed = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signed, 0); rawBody.CopyTo(signed.AsSpan(prefix.Length));
        using var hmac = new HMACSHA256(_secret);
        var expected = hmac.ComputeHash(signed);
        foreach (var signature in signatures)
        {
            if (signature.Length != 64) continue;
            try { if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature))) return; }
            catch (FormatException) { }
        }
        throw new WebhookSignatureException("Webhook signature does not match payload.");
    }

    public JsonElement ConstructEvent(string signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset? now = null)
    {
        AssertValid(signatureHeader, rawBody, now);
        using var document = ParseEnvelope(rawBody);
        return document.RootElement.Clone();
    }

    public WebhookEvent<TData> ConstructEvent<TData>(
        string signatureHeader,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset? now = null)
    {
        AssertValid(signatureHeader, rawBody, now);
        using var document = ParseEnvelope(rawBody);
        return document.RootElement.Deserialize<WebhookEvent<TData>>(PaymosClient.Json)
            ?? throw new PaymosException("Paymos webhook event is null.");
    }

    private static JsonDocument ParseEnvelope(ReadOnlySpan<byte> rawBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody.ToArray());
        }
        catch (JsonException error)
        {
            throw new PaymosException("Paymos webhook contains invalid JSON.", error);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("event_id", out var eventId) || eventId.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(eventId.GetString()) ||
            !root.TryGetProperty("event_type", out var eventType) || eventType.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(eventType.GetString()) ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionValue) || versionValue < 1 ||
            !root.TryGetProperty("occurred_at", out var occurredAt) || occurredAt.ValueKind != JsonValueKind.Number || !occurredAt.TryGetInt64(out var occurredAtValue) || occurredAtValue < 0 ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new PaymosException("Paymos webhook event envelope is invalid.");
        }

        return document;
    }

    private static (long Timestamp, string[] Signatures) ParseHeader(string header)
    {
        long? timestamp = null; var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "t")
            {
                if (timestamp is not null ||
                    !long.TryParse(pair[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                    value < 0)
                    throw new WebhookSignatureException("Webhook signature header is missing or malformed.");
                timestamp = value;
            }
            else if (pair[0] == "v1" && pair[1].Length > 0) signatures.Add(pair[1]);
        }
        return timestamp is null || signatures.Count == 0
            ? throw new WebhookSignatureException("Webhook signature header is missing or malformed.")
            : (timestamp.Value, signatures.ToArray());
    }
}
