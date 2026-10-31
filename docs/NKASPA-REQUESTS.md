# Requests to NKaspa (from Kaspa Desktop Wallet)

Verified against NKaspa commit `f41c86a6d36da37243bcdd41e76662ac6c25b349` (0.1.9-alpha). The NKaspa source was **not** modified; the wallet works around each item in Core.

| # | Area | Observation | Wallet workaround | Proposed change |
|---|------|-------------|-------------------|-----------------|
| R1 | BIP39 | `new Mnemonic(string)` accepts a bad checksum (only sets `IsValidChecksum`). An import with a typo would silently derive a different wallet. | `MnemonicService.Normalize` throws when `!IsValidChecksum`. | Add `Mnemonic.ParseValidated` (additive, non-breaking): `patches/0001-mnemonic-parse-validated.diff` (`git apply --check` passes). |
| R2 | Fees | Floor is 100 sompi/gram; `SendEstimatedFees` throws below it. Node `getFeeEstimate` returns lower rates. | Clamp to the floor; show the exact fee before confirmation. | Confirm the floor against rusty-kaspa mempool policy; make the floor configurable per network. |
| R3 | Secrets | `Mnemonic.ToString()`, `DeriveExtKey(string? passphrase)` are string-only. | Documented (DECISIONS D5). | Span/char[] overloads so callers can wipe input. |
| R4 | RPC | There is no subscription (`notifyUtxosChanged`) in the minimal wRPC client, so the UI polls on demand. | Manual Refresh. | Add notifications to `KaspaWrpcJsonClient`. |
| R5 | Signing | Schnorr aux randomness in `BuildTransaction(sign:true)` is random, so signed bytes aren't reproducible (the tx **id** is). | Tests check id/fee/mass determinism + `Verify`. | Optional aux-rand injection on `TransactionBuilder` for test vectors. |

## R6 — wRPC timeouts / reconnect
`KaspaWebSocketChannel` only accepts a CancellationToken; it has no timeout, keep-alive, or reconnect. Wallet workaround: `Core/Rpc/ResilientWalletNode`. Request: an optional `RequestTimeout`, a reconnect hook, and a `State` event.

## R7 — History / subscriptions
There is no `notifyUtxosChanged`/`notifyVirtualDaaScoreChanged` subscription and no address-history helper. Wallet workaround: polling plus `Core/Tracking/TxTracker`. Request: subscription support in `KaspaWrpcJsonClient`.

## R8 — Secret leakage surface
`ExtKey.ToString()` returns the xprv, and `Mnemonic` keeps its words as strings and is not IDisposable. Request:
- `ToString()` on secret types should return a redacted value, with an explicit `ToWif()`/`ExportXprv()` instead.
- `Mnemonic` should implement IDisposable, or accept a span input.

Wallet workaround: `SecretRedactor` on every log line, and the wallet never calls ToString on keys.

## R9 — Toccata activation per network
`TransactionBuilder(network, toccataActive)` leaves activation to the caller, and all built-in networks publish the post-Toccata limits. Request: per-network activation metadata, e.g. a DAA score, or `Network.IsToccataActive(daaScore)`. Wallet workaround: `MassPolicy` in settings.

## R10 — Strict mnemonic parsing + secret-safe ToString/JSON (from testcases/nkaspa-linux-report.md NL-010/021/070/072/074). Owner: local1 will fix in NKaspa.
- NL-010 (critical): `new Mnemonic(string)` does not throw on a bad checksum. Request: a strict `Mnemonic.Parse`/ctor that throws `FormatException`. This extends R1; see patch 0001.
- NL-021: `Mnemonic.Words` is a writable `string[]`, and `IsValidChecksum` is cached at construction. Request: make it `IReadOnlyList<string>`.
- NL-070/072: `ExtKey.ToString()` returns the full kprv/ktrv and `Mnemonic.ToString()` returns every word. Request: return a redacted value, with explicit `ExportXprv()`/`ToPhrase()`.
- NL-074: `JsonSerializer.Serialize(Mnemonic)` outputs the words. Request: a refusing converter, as `Key` already has.
- Wallet-side mitigation (done):
  - `MnemonicService.Normalize` hard-rejects bad word counts, unknown words (with position), and bad checksums.
  - `SecretText`/`HdAccount` return `[REDACTED]` from ToString and refuse JSON.
  - Core never calls ToString on Mnemonic/ExtKey/Key; a test guards this.
  - The log redactor runs on every log line.
