# Changelog

## [Unreleased]

## [1.2.1] - 2026-09-13

- fix(sdk-conformance): восемь копий контракта разошлись с каноном — 403 снова permission
- fix(sdk): HTTPS обязателен, а 403 перестал притворяться ошибкой ключа

- **403 is no longer reported as an authentication failure.** `PaymosApiException.Kind` is `"permission"` for 403; 401 keeps `"authentication"`.
- **Base URL must use HTTPS.** Plain HTTP is accepted only against a loopback host, which is what a local integration test points at. The API secret signs every request, so a plaintext base URL put that credential on the wire.

- Payment channels: `PaymentChannels` (create, get, list, block, unblock, simulate deposit, bounded
  iterate) and `PaymentChannelDeposits` (get, read). The deposit endpoint is a feed, not a list — its
  `PaymentChannelDepositFeedPage.NextCursor` is never null, `Blocked` explains a stalled position, and
  it deliberately ships no auto-iterator. The three `payment_channel.deposit.*` webhook events decode
  straight into `PaymentChannelDeposit` through the existing generic `WebhookVerifier.ConstructEvent<TData>`
  — no new decoding code. `confirming`/`reorged` are advisory and may arrive out of order; only
  `confirmed` with `IsFinal` true should credit a merchant.

## [1.2.0] - 2026-08-27

- release: the changelog rot had a cause, and it was not the one I named
- docs(sdk): the READMEs say static wallets, not payment channels
- feat(sdk): payment-channel webhooks and the flow in every README
- feat(dotnet-sdk): payment channels, and a feed reader that cannot stop early
- feat(sdk-conformance): declare the payment-channel operations
- docs(api): pin the empty-body signing rule and fix the published POST vector

- Payment channels: `PaymentChannels` (create, get, list, block, unblock, simulate deposit, bounded
  iterate) and `PaymentChannelDeposits` (get, read). The deposit endpoint is a feed, not a list — its
  `PaymentChannelDepositFeedPage.NextCursor` is never null, `Blocked` explains a stalled position, and
  it deliberately ships no auto-iterator. The three `payment_channel.deposit.*` webhook events decode
  straight into `PaymentChannelDeposit` through the existing generic `WebhookVerifier.ConstructEvent<TData>`
  — no new decoding code. `confirming`/`reorged` are advisory and may arrive out of order; only
  `confirmed` with `IsFinal` true should credit a merchant.

## [1.1.1] - 2026-08-02

- fix(ecosystem): recover SDK releases

## [1.1.0] - 2026-07-21

- feat(docs): make the developer surface consumable by LLM agents

## [1.0.1] - 2026-07-19

- fix: align SDK problem details semantics

## [1.0.0]

- Initial official SDK.
