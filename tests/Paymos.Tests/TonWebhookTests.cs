using System.Text;
using System.Text.Json;
using Paymos;

internal static class TonWebhookTests
{
    public static void Run(JsonElement fixtures)
    {
        var vectors = fixtures.GetProperty("vectors");
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "invoice_raw", "withdrawal_raw", "refund_raw", "channel_deposit_raw",
            "invoice_friendly", "withdrawal_friendly", "refund_friendly", "channel_deposit_friendly",
            "invoice_v1", "withdrawal_v1", "refund_v1", "channel_deposit_v1",
            "invoice_v2", "withdrawal_v2", "refund_v2", "channel_deposit_v2"
        };
        var actualNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in vectors.EnumerateArray())
            if (!actualNames.Add(vector.GetProperty("name").GetString()!))
                throw new Exception("Duplicate signed TON webhook vector.");
        if (vectors.GetArrayLength() != expectedNames.Count || !actualNames.SetEquals(expectedNames))
            throw new Exception("Expected all 16 signed TON webhook vectors.");
        var now = DateTimeOffset.FromUnixTimeSeconds(fixtures.GetProperty("now").GetInt64());
        var verifier = new WebhookVerifier(fixtures.GetProperty("secret").GetString()!);
        foreach (var vector in vectors.EnumerateArray())
        {
            var body = vector.GetProperty("raw_body").GetString()!;
            var header = vector.GetProperty("header").GetString()!;
            var evt = verifier.ConstructEvent<JsonElement>(header, Encoding.UTF8.GetBytes(body), now);
            if (evt.EventId != vector.GetProperty("event_id").GetString()
                || evt.EventType != vector.GetProperty("event_type").GetString()
                || evt.Version != vector.GetProperty("version").GetInt32()
                || evt.OccurredAt != fixtures.GetProperty("now").GetInt64())
                throw new Exception("Signed webhook envelope changed during decoding.");
            foreach (var expected in vector.GetProperty("expect").EnumerateArray())
            {
                var value = evt.Data;
                foreach (var part in expected.GetProperty("path").EnumerateArray())
                    value = value.GetProperty(part.GetString()!);
                if (value.GetRawText() != expected.GetProperty("value").GetRawText())
                    throw new Exception("Signed webhook field changed during decoding.");
            }
            if (verifier.Verify(header, Encoding.UTF8.GetBytes(body + " "), now))
                throw new Exception("JSON-valid body change must invalidate the signature.");
        }
    }
}
