using System.Text;
using System.Text.Json;
using Paymos;

internal static class TonWebhookTests
{
    public static void Run(JsonElement fixtures)
    {
        var vectors = fixtures.GetProperty("vectors");
        if (vectors.GetArrayLength() != 8) throw new Exception("Expected eight signed raw and friendly schema 1 TON webhook vectors.");
        var now = DateTimeOffset.FromUnixTimeSeconds(fixtures.GetProperty("now").GetInt64());
        var verifier = new WebhookVerifier(fixtures.GetProperty("secret").GetString()!);
        foreach (var vector in vectors.EnumerateArray())
        {
            if (vector.GetProperty("version").GetInt32() != 1)
                throw new Exception("Expected schema 1 webhook payload.");
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
