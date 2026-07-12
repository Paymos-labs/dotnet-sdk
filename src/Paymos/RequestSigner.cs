using System.Security.Cryptography;
using System.Text;

namespace Paymos;

public static class RequestSigner
{
    public static string StringToSign(
        string timestamp, string method, string path, string query = "", string body = "")
    {
        var bodyHash = body.Length == 0
            ? ""
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return $"{timestamp}\n{method.ToUpperInvariant()}\n{path}\n{query}\n{bodyHash}";
    }

    public static string Sign(string secret, string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    public static string AuthorizationHeader(
        string apiKey, string apiSecret, string timestamp, string method,
        string path, string query = "", string body = "") =>
        $"HMAC-SHA256 {apiKey}:{Sign(apiSecret, StringToSign(timestamp, method, path, query, body))}";

    public static string EncodePathSegment(string value) => Uri.EscapeDataString(value);

    public static string BuildQuery(InvoiceListOptions? options)
    {
        if (options is null)
            return "";
        return BuildQuery(new Dictionary<string, object?>
        {
            ["limit"] = options.Limit,
            ["cursor"] = options.Cursor,
            ["status"] = options.Status?.Select(value => value.Value).ToArray(),
            ["external_order_id"] = options.ExternalOrderId,
            ["project_id"] = options.ProjectId,
            ["created_from"] = options.CreatedFrom,
            ["created_to"] = options.CreatedTo
        });
    }

    public static string BuildQuery(WithdrawalListOptions? options)
    {
        if (options is null)
            return "";
        return BuildQuery(new Dictionary<string, object?>
        {
            ["limit"] = options.Limit,
            ["cursor"] = options.Cursor,
            ["status"] = options.Status?.Select(value => value.Value).ToArray(),
            ["external_order_id"] = options.ExternalOrderId,
            ["created_from"] = options.CreatedFrom,
            ["created_to"] = options.CreatedTo
        });
    }

    public static string BuildQuery(IReadOnlyDictionary<string, object?> filters)
    {
        var parts = new List<string>();
        foreach (var pair in filters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null) continue;
            var values = pair.Value switch
            {
                string scalar => [scalar],
                int scalar => [scalar.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                long scalar => [scalar.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                IEnumerable<string> repeated => repeated.Order(StringComparer.Ordinal).ToArray(),
                _ => throw new ArgumentException($"Unsupported Paymos list filter: {pair.Key}")
            };
            if (values.Length == 0) throw new ArgumentException($"Paymos list filter cannot be empty: {pair.Key}");
            foreach (var value in values)
            {
                if (value.Length == 0) throw new ArgumentException($"Paymos list filter cannot be empty: {pair.Key}");
                parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value)}");
            }
        }
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}
