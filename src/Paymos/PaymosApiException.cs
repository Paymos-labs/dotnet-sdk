using System.Text.Json;
using System.Net.Http.Headers;

namespace Paymos;

public class PaymosException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class WebhookSignatureException(string message) : PaymosException(message);
public sealed class WebhookTimestampException(string message) : PaymosException(message);

public sealed class PaymosApiException : PaymosException
{
    public int StatusCode { get; }
    public string ResponseBody { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string Code { get; }
    public string? Field { get; }
    public string Kind { get; }
    public TimeSpan? RetryAfter { get; }

    internal PaymosApiException(int statusCode, string body, HttpResponseHeaders headers)
        : base(BuildMessage(statusCode, body, out var code, out var field))
    {
        StatusCode = statusCode;
        ResponseBody = body;
        Code = code;
        Field = field;
        Headers = headers.ToDictionary(x => x.Key.ToLowerInvariant(), x => string.Join(",", x.Value));
        Kind = statusCode switch
        {
            400 => "validation",
            401 or 403 => "authentication",
            404 => "not_found",
            409 => "conflict",
            410 => "gone",
            429 => "rate_limit",
            503 => "unavailable",
            >= 500 => "server",
            _ => "api"
        };
        RetryAfter = headers.RetryAfter?.Delta;
    }

    private static string BuildMessage(int status, string body, out string code, out string? field)
    {
        code = ""; field = null; var detail = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                code = first.TryGetProperty("code", out var itemCode) ? itemCode.GetString() ?? "" : "";
                field = first.TryGetProperty("field", out var itemField) && itemField.ValueKind == JsonValueKind.String ? itemField.GetString() : null;
                detail = first.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";
            }
            else
            {
                code = root.TryGetProperty("code", out var topCode) ? topCode.GetString() ?? "" : "";
                field = root.TryGetProperty("field", out var topField) && topField.ValueKind == JsonValueKind.String ? topField.GetString() : null;
                detail = root.TryGetProperty("detail", out var topDetail) ? topDetail.GetString() ?? "" : "";
            }
        }
        catch (JsonException) { }
        return $"Paymos API {status}: {(detail.Length > 0 ? detail : code.Length > 0 ? code : body.Length > 0 ? body : "empty response")}";
    }
}
