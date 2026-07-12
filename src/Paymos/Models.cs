using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paymos;

[JsonConverter(typeof(InvoiceStatusJsonConverter))]
public readonly record struct InvoiceStatus(string Value)
{
    public static readonly InvoiceStatus AwaitingClient = new("awaiting_client");
    public static readonly InvoiceStatus AwaitingPayment = new("awaiting_payment");
    public static readonly InvoiceStatus Confirming = new("confirming");
    public static readonly InvoiceStatus UnderpaidWaiting = new("underpaid_waiting");
    public static readonly InvoiceStatus Paid = new("paid");
    public static readonly InvoiceStatus PaidOver = new("paid_over");
    public static readonly InvoiceStatus Underpaid = new("underpaid");
    public static readonly InvoiceStatus Expired = new("expired");
    public static readonly InvoiceStatus Cancelled = new("cancelled");

    public override string ToString() => Value;
}

[JsonConverter(typeof(WithdrawalStatusJsonConverter))]
public readonly record struct WithdrawalStatus(string Value)
{
    public static readonly WithdrawalStatus Created = new("created");
    public static readonly WithdrawalStatus PendingReview = new("pending_review");
    public static readonly WithdrawalStatus SignatureCreated = new("signed");
    public static readonly WithdrawalStatus Cancelling = new("cancelling");
    public static readonly WithdrawalStatus Completed = new("completed");
    public static readonly WithdrawalStatus Failed = new("failed");
    public static readonly WithdrawalStatus Cancelled = new("cancelled");

    public override string ToString() => Value;
}

public sealed record CreateInvoiceRequest(
    string ProjectId,
    string Amount,
    string Currency,
    string ExternalOrderId,
    string? Network = null,
    bool? AllowMultiplePayments = null,
    byte? CustomerFeePercent = null,
    string? ClientId = null);

public sealed record InvoiceListOptions(
    int? Limit = null,
    string? Cursor = null,
    IReadOnlyList<InvoiceStatus>? Status = null,
    string? ExternalOrderId = null,
    string? ProjectId = null,
    long? CreatedFrom = null,
    long? CreatedTo = null);

public sealed record Order
{
    public required string ExternalId { get; init; }
    public string? ClientId { get; init; }
    public required string Amount { get; init; }
    public required string Currency { get; init; }
    public string? Network { get; init; }
}

public sealed record Transfer
{
    public required string TxHash { get; init; }
    public required string Amount { get; init; }
    public required string Status { get; init; }
    public required long CreatedAt { get; init; }
    public long? ConfirmedAt { get; init; }
    public int? RequiredConfirmations { get; init; }
    public long? EstimatedConfirmationAt { get; init; }
    public string? ExplorerUrl { get; init; }
}

public sealed record Payment
{
    public required string Currency { get; init; }
    public required string Network { get; init; }
    public required long ChainId { get; init; }
    public string? ContractAddress { get; init; }
    public required string Expected { get; init; }
    public string? Address { get; init; }
    public string? ExchangeRate { get; init; }
    public string? Paid { get; init; }
    public string? Remaining { get; init; }
    public string? Fee { get; init; }
    public string? Net { get; init; }
    public IReadOnlyList<Transfer>? Transfers { get; init; }
}

public sealed record Invoice
{
    public required string InvoiceId { get; init; }
    public required string ProjectId { get; init; }
    public required InvoiceStatus Status { get; init; }
    public required bool IsFinal { get; init; }
    public required bool IsTest { get; init; }
    public required string PaymentUrl { get; init; }
    public required Order Order { get; init; }
    public Payment? Payment { get; init; }
    public required long CreatedAt { get; init; }
    public required long UpdatedAt { get; init; }
    public long? ExpiresAt { get; init; }
    public long? CompletedAt { get; init; }
}

public sealed record InvoiceListItem
{
    public required string InvoiceId { get; init; }
    public required string ProjectId { get; init; }
    public required string ExternalOrderId { get; init; }
    public string? ClientId { get; init; }
    public required InvoiceStatus Status { get; init; }
    public required bool IsFinal { get; init; }
    public required bool IsTest { get; init; }
    public required string Amount { get; init; }
    public required string Currency { get; init; }
    public string? Network { get; init; }
    public required long CreatedAt { get; init; }
    public long? ExpiresAt { get; init; }
    public long? CompletedAt { get; init; }
}

public sealed record CreateWithdrawalRequest(
    string DestinationAddress,
    string Network,
    string Currency,
    string Amount,
    string ExternalOrderId);

public sealed record WithdrawalListOptions(
    int? Limit = null,
    string? Cursor = null,
    IReadOnlyList<WithdrawalStatus>? Status = null,
    string? ExternalOrderId = null,
    long? CreatedFrom = null,
    long? CreatedTo = null);

public sealed record Withdrawal
{
    public required string WithdrawalId { get; init; }
    public required string ExternalOrderId { get; init; }
    public required WithdrawalStatus Status { get; init; }
    public required bool IsFinal { get; init; }
    public required bool IsTest { get; init; }
    public required string Amount { get; init; }
    public string? Fee { get; init; }
    public required string Currency { get; init; }
    public required string Network { get; init; }
    public required string DestinationAddress { get; init; }
    public string? TxHash { get; init; }
    public string? ExplorerUrl { get; init; }
    public required long CreatedAt { get; init; }
    public long? CompletedAt { get; init; }
    public long? FailedAt { get; init; }
    public long? CancelledAt { get; init; }
}

public sealed record Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public string? NextCursor { get; init; }
}

public sealed record Balance
{
    public required string Currency { get; init; }
    public required string Available { get; init; }
}

public sealed record ServerTime
{
    [JsonPropertyName("server_time")]
    public required long ServerTimeSeconds { get; init; }
}

public sealed record WebhookEvent<TData>
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required int Version { get; init; }
    public required long OccurredAt { get; init; }
    public required TData Data { get; init; }
}

internal sealed class InvoiceStatusJsonConverter : JsonConverter<InvoiceStatus>
{
    public override InvoiceStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Invoice status must be a string."));

    public override void Write(Utf8JsonWriter writer, InvoiceStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal sealed class WithdrawalStatusJsonConverter : JsonConverter<WithdrawalStatus>
{
    public override WithdrawalStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Withdrawal status must be a string."));

    public override void Write(Utf8JsonWriter writer, WithdrawalStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
