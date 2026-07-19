using System.Text.Json;
using System.Net.Http.Headers;

namespace Paymos;

public class PaymosException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class WebhookSignatureException(string message) : PaymosException(message);
public sealed class WebhookTimestampException(string message) : PaymosException(message);

public sealed record PaymosProblemError(string Code, string? Field, string Message);

public sealed class PaymosApiException : PaymosException
{
    public int StatusCode { get; }
    public string ResponseBody { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string? Type { get; }
    public string? Title { get; }
    public int? ProblemStatus { get; }
    public string? Detail { get; }
    public string Code { get; }
    public string? Field { get; }
    public IReadOnlyList<PaymosProblemError> Errors { get; }
    public string? TraceId { get; }
    public string Kind { get; }
    public TimeSpan? RetryAfter { get; }

    internal PaymosApiException(int statusCode, string body, HttpResponseHeaders headers)
        : this(statusCode, body, headers, ParseProblem(statusCode, body))
    {
    }

    private PaymosApiException(
        int statusCode,
        string body,
        HttpResponseHeaders headers,
        ParsedProblem? problem)
        : base(BuildMessage(statusCode, body, problem))
    {
        StatusCode = statusCode;
        ResponseBody = body;
        Type = problem?.Type;
        Title = problem?.Title;
        ProblemStatus = problem?.Status;
        Detail = problem?.Detail;
        Code = problem?.Code ?? "";
        Field = problem?.Field;
        Errors = problem?.Errors ?? [];
        TraceId = problem?.TraceId;
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

    private static ParsedProblem? ParseProblem(int httpStatus, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "type", out var type)
                || !TryString(root, "title", out var title)
                || !root.TryGetProperty("status", out var statusElement)
                || !statusElement.TryGetInt32(out var status)
                || status != httpStatus
                || !TryString(root, "detail", out var detail)
                || !TryString(root, "code", out var code))
            {
                return null;
            }

            var field = root.TryGetProperty("field", out var fieldElement)
                        && fieldElement.ValueKind == JsonValueKind.String
                ? fieldElement.GetString()
                : null;
            var traceId = root.TryGetProperty("trace_id", out var traceElement)
                          && traceElement.ValueKind == JsonValueKind.String
                ? traceElement.GetString()
                : null;
            var errors = new List<PaymosProblemError>();
            if (root.TryGetProperty("errors", out var errorsElement)
                && errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errorsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !TryString(item, "code", out var itemCode)
                        || !TryString(item, "message", out var message))
                    {
                        continue;
                    }

                    var itemField = item.TryGetProperty("field", out var itemFieldElement)
                                    && itemFieldElement.ValueKind == JsonValueKind.String
                        ? itemFieldElement.GetString()
                        : null;
                    errors.Add(new PaymosProblemError(itemCode, itemField, message));
                }
            }

            return new ParsedProblem(type, title, status, detail, code, field, errors, traceId);
        }
        catch (JsonException) { }
        return null;
    }

    private static bool TryString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && (value = property.GetString() ?? "") is not null;
    }

    private static string BuildMessage(int status, string body, ParsedProblem? problem)
    {
        var summary = problem?.Detail;
        if (string.IsNullOrEmpty(summary)) summary = problem?.Code;
        if (string.IsNullOrEmpty(summary)) summary = problem?.Title;
        if (string.IsNullOrEmpty(summary)) summary = body.Length > 0 ? body : "empty response";
        return $"Paymos API {status}: {summary}";
    }

    private sealed record ParsedProblem(
        string Type,
        string Title,
        int Status,
        string Detail,
        string Code,
        string? Field,
        IReadOnlyList<PaymosProblemError> Errors,
        string? TraceId);
}
