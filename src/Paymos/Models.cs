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

/// <summary>The three lifecycle stages the sandbox can drive a simulated deposit to.</summary>
[JsonConverter(typeof(SimulateDepositStageJsonConverter))]
public enum SimulateDepositStage
{
    Confirming,
    Reorged,
    Confirmed
}

public sealed record CreatePaymentChannelRequest(string ProjectId, string ExternalId);

/// <summary>
/// Sandbox deposit input. <c>Network</c> is REQUIRED. <c>Stage</c> defaults to confirmed and is
/// omitted from the body entirely when left null — never sent as null or an empty string.
/// </summary>
public sealed record SimulateDepositRequest(
    string Amount,
    string Currency,
    string Network,
    SimulateDepositStage? Stage = null);

public sealed record PaymentChannelListOptions(
    int? Limit = null,
    string? Cursor = null,
    IReadOnlyList<string>? Status = null,
    string? ExternalId = null,
    string? ProjectId = null);

/// <summary>
/// Feed filters. <c>ConfirmedFrom</c> is an initial-sync lower bound for the very first poll, NOT the
/// resume mechanism — afterwards a merchant resumes from the stored <c>NextCursor</c>.
/// </summary>
public sealed record PaymentChannelDepositFeedOptions(
    int? Limit = null,
    string? Cursor = null,
    string? ProjectId = null,
    string? PaymentChannelId = null,
    long? ConfirmedFrom = null);

public sealed record PaymentChannelToken
{
    public required string Symbol { get; init; }

    /// <summary>
    /// Absent means "cannot quote a minimum right now" — never "no minimum".
    /// </summary>
    public string? MinimumDeposit { get; init; }
}

public sealed record PaymentChannelNetwork
{
    /// <summary>Rail code, for example <c>TRC20</c> — not a chain nickname.</summary>
    public required string Network { get; init; }

    public required string Status { get; init; }

    /// <summary>Absent until this rail provisions, and never changes afterwards.</summary>
    public string? Address { get; init; }

    public required IReadOnlyList<PaymentChannelToken> Tokens { get; init; }
}

public sealed record PaymentChannel
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ExternalId { get; init; }
    public required string Status { get; init; }
    public required bool IsAcceptingPayments { get; init; }
    public required bool IsFullyProvisioned { get; init; }
    public required bool IsTest { get; init; }
    public required decimal AppliedFeePercent { get; init; }
    public required byte CustomerFeePercent { get; init; }
    public required IReadOnlyList<PaymentChannelNetwork> Networks { get; init; }
    public required long CreatedAt { get; init; }
    public required long UpdatedAt { get; init; }
}

/// <summary>
/// The full payment-channel deposit wire shape — also the <c>data</c> payload of all three
/// <c>payment_channel.deposit.*</c> webhook events, so <see cref="WebhookVerifier.ConstructEvent{TData}"/>
/// decodes one straight into this record with no extra code.
/// <para>
/// ORDERING — <c>confirming</c> and <c>reorged</c> are ADVISORY and may arrive out of order: a
/// <c>confirming</c> event can land after the <c>confirmed</c> for the same deposit, and a
/// <c>reorged</c> event can be superseded by a later <c>confirmed</c>. Credit a merchant only on
/// <c>payment_channel.deposit.confirmed</c> with <see cref="IsFinal"/> true, and never let an
/// advisory event regress a deposit already known to be confirmed — crediting on <c>confirming</c>
/// releases goods against money a reorg can still take back.
/// </para>
/// </summary>
public sealed record PaymentChannelDeposit
{
    public required string Id { get; init; }
    public required string PaymentChannelId { get; init; }
    public required string ProjectId { get; init; }
    public required string PaymentChannelExternalId { get; init; }
    public required string Status { get; init; }
    public required bool IsFinal { get; init; }
    public required bool IsTest { get; init; }
    public required string Currency { get; init; }
    public required string Network { get; init; }
    public required long ChainId { get; init; }
    public string? ContractAddress { get; init; }
    public required string Gross { get; init; }
    public required string Fee { get; init; }
    public required string Net { get; init; }
    public required decimal AppliedFeePercent { get; init; }
    public required byte CustomerFeePercent { get; init; }
    public required string TxHash { get; init; }
    public required string TransferId { get; init; }
    public string? SourceAddress { get; init; }
    public required string DestinationAddress { get; init; }
    public required long BlockHeight { get; init; }
    public long? FirstIncludedBlockTimestamp { get; init; }
    public string? ExplorerUrl { get; init; }
    public required long CreatedAt { get; init; }
    public required long UpdatedAt { get; init; }
    public long? ConfirmedAt { get; init; }
}

/// <summary>
/// The one feed position that could not be rendered, and why. Without it a stuck feed is
/// byte-identical to a quiet day, and a merchant cannot tell "no new payments" from
/// "I am not seeing my money". Reason codes: <c>booked_auth_missing</c>, <c>inconsistent</c>,
/// <c>deposit_missing</c>, <c>channel_missing</c>, <c>transfer_missing</c>, <c>not_confirmed</c>.
/// </summary>
public sealed record PaymentChannelDepositFeedBlockage
{
    public required string DepositId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// One page of the confirmed-deposit feed. Deliberately NOT <see cref="Page{T}"/>:
/// <see cref="NextCursor"/> is non-nullable and always present, including on an empty page, because
/// feed positions are global across all merchants and a filtered poller still has to advance past the
/// ones it skipped. There is no last page — money keeps arriving — so never stop on the cursor.
/// </summary>
public sealed record PaymentChannelDepositFeedPage
{
    public required IReadOnlyList<PaymentChannelDeposit> Items { get; init; }
    public required string NextCursor { get; init; }
    public PaymentChannelDepositFeedBlockage? Blocked { get; init; }
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

internal sealed class SimulateDepositStageJsonConverter : JsonConverter<SimulateDepositStage>
{
    public override SimulateDepositStage Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "confirming" => SimulateDepositStage.Confirming,
            "reorged" => SimulateDepositStage.Reorged,
            "confirmed" => SimulateDepositStage.Confirmed,
            var other => throw new JsonException($"Unknown deposit simulation stage: {other}.")
        };

    public override void Write(Utf8JsonWriter writer, SimulateDepositStage value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            SimulateDepositStage.Confirming => "confirming",
            SimulateDepositStage.Reorged => "reorged",
            SimulateDepositStage.Confirmed => "confirmed",
            _ => throw new JsonException($"Unknown deposit simulation stage: {value}.")
        });
}
