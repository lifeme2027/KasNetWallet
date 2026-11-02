# Requests to NKaspa (from KasNet Wallet)

Now pinned to NuGet `NKaspa` 0.1.11-alpha (tip `4767933333d0a3cee6d9960efb10998ff0f4a81c`; previously 0.1.10-alpha / dea2697).

**Resolved at 5c49251:** R1, R6, R9, R10, R11. The wallet adopted the new APIs but keeps its own guards as defense in depth (see DECISIONS D19). Open: R2 (partly: floor is now per policy), R3, R4, R5, R7, R8 (partly). The NKaspa source was **not** modified; the wallet works around each item in Core.

| # | Area | Observation | Wallet workaround | Proposed change |
|---|------|-------------|-------------------|-----------------|
| R1 ✅ resolved at 5c49251 (`Mnemonic.Parse` throws on bad checksum; `DeriveSeed` throws unless `allowInvalidChecksum`) | BIP39 | `new Mnemonic(string)` accepts a bad checksum (only sets `IsValidChecksum`). An import with a typo would silently derive a different wallet. | `MnemonicService.Normalize` throws when `!IsValidChecksum`. | Add `Mnemonic.ParseValidated` (additive, non-breaking): `patches/0001-mnemonic-parse-validated.diff` (`git apply --check` passes). |
| R2 | Fees | Floor is 100 sompi/gram; `SendEstimatedFees` throws below it. Node `getFeeEstimate` returns lower rates. | Clamp to the floor; show the exact fee before confirmation. | Confirm the floor against rusty-kaspa mempool policy; make the floor configurable per network. |
| R3 | Secrets | `Mnemonic.ToString()`, `DeriveExtKey(string? passphrase)` are string-only. | Documented (DECISIONS D5). | Span/char[] overloads so callers can wipe input. |
| R4 | RPC | There is no subscription (`notifyUtxosChanged`) in the minimal wRPC client, so the UI polls on demand. | Manual Refresh. | Add notifications to `KaspaWrpcJsonClient`. |
| R5 | Signing | Schnorr aux randomness in `BuildTransaction(sign:true)` is random, so signed bytes aren't reproducible (the tx **id** is). | Tests check id/fee/mass determinism + `Verify`. | Optional aux-rand injection on `TransactionBuilder` for test vectors. |

## R6 — wRPC timeouts / reconnect

**✅ Resolved at 5c49251:** `KaspaWebSocketOptions` (Connect/RequestTimeout, AutoReconnect, MaxMessageBytes 16 MiB), `State`/`StateChanged`/`Reconnected`. Wallet passes its timeout and explicit 16 MiB cap; AutoReconnect stays off because `ResilientWalletNode` re-checks the network id after reconnect.
`KaspaWebSocketChannel` only accepts a CancellationToken; it has no timeout, keep-alive, or reconnect. Wallet workaround: `Core/Rpc/ResilientWalletNode`. Request: an optional `RequestTimeout`, a reconnect hook, and a `State` event.

## R7 — History / subscriptions
There is no `notifyUtxosChanged`/`notifyVirtualDaaScoreChanged` subscription and no address-history helper. Wallet workaround: polling plus `Core/Tracking/TxTracker`. Request: subscription support in `KaspaWrpcJsonClient`.

## R8 — Secret leakage surface
`ExtKey.ToString()` returns the xprv, and `Mnemonic` keeps its words as strings and is not IDisposable. Request:
- `ToString()` on secret types should return a redacted value, with an explicit `ToWif()`/`ExportXprv()` instead.
- `Mnemonic` should implement IDisposable, or accept a span input.

Wallet workaround: `SecretRedactor` on every log line, and the wallet never calls ToString on keys.

## R9 — Toccata activation per network

**✅ Resolved at 5c49251:** `Network.IsToccataActive(daaScore)` / `MassPolicyAt(daaScore)`, `TransactionBuilder(Network, MassPolicy)`; testnet-10 is active. Wallet uses the node's virtual DAA score (auto mode).
`TransactionBuilder(network, toccataActive)` leaves activation to the caller, and all built-in networks publish the post-Toccata limits. Request: per-network activation metadata, e.g. a DAA score, or `Network.IsToccataActive(daaScore)`. Wallet workaround: `MassPolicy` in settings.

## R10 — Strict mnemonic parsing + secret-safe ToString/JSON (from testcases/nkaspa-linux-report.md NL-010/021/070/072/074). Owner: local1 will fix in NKaspa.

**✅ Resolved at 5c49251:** NFKD/case-insensitive parsing, zero-width stripped, `Words` is `IReadOnlyList`, `ToPhrase()`/`ExportXprv()` for raw export, secret-safe `ToString`, default `JsonSerializer` throws on Key/ExtKey/Mnemonic. Wallet still REJECTS zero-width characters instead of stripping them.


- NL-010 (critical): `new Mnemonic(string)` does not throw on a bad checksum. Request: a strict `Mnemonic.Parse`/ctor that throws `FormatException`. This extends R1; see patch 0001.
- NL-021: `Mnemonic.Words` is a writable `string[]`, and `IsValidChecksum` is cached at construction. Request: make it `IReadOnlyList<string>`.
- NL-070/072: `ExtKey.ToString()` returns the full kprv/ktrv and `Mnemonic.ToString()` returns every word. Request: return a redacted value, with explicit `ExportXprv()`/`ToPhrase()`.
- NL-074: `JsonSerializer.Serialize(Mnemonic)` outputs the words. Request: a refusing converter, as `Key` already has.
- Wallet-side mitigation (done):
  - `MnemonicService.Normalize` hard-rejects bad word counts, unknown words (with position), and bad checksums.
  - `SecretText`/`HdAccount` return `[REDACTED]` from ToString and refuse JSON.
  - Core never calls ToString on Mnemonic/ExtKey/Key; a test guards this.
  - The log redactor runs on every log line.

## R11 — Change silently burned when a change output would exceed the mass limit (retest N4)

**✅ Resolved at 5c49251:** change is folded only if ≤ max(MinimumChange, `MaxFoldedChange` = 0.1 KAS) and ≤ 100× the no-change fee; otherwise `BuildTransaction` throws. `ChangeFolded`/`FoldedChange` are exposed. Wallet still blocks any fold ≥ MinimumChange and asks for acknowledgement of smaller folds.


`TransactionBuilder.Materialize`: when adding the change output makes the tx exceed `MaximumMass`/`MassLimits`, the **whole change** is added to the fee (`fee += change`), even when change ≥ `MinimumChange`. Observed: tn10, 0.1 KAS from one 50 KAS UTXO at 150 sompi/gram → ~49.9 KAS fee.
Request: throw (e.g. `ChangeExceedsMassLimitException`) or expose a flag, and never fold change ≥ `MinimumChange` silently; also expose the "folded change" amount on the built result.
Wallet workaround (done): after building, compute expected fee from `MassCalculator.Calculate` at the chosen rate; if change == 0 and fee − expected ≥ `MinimumChange` the transfer is blocked; smaller folds get one extra-input retry, then a warning that must be acknowledged.

