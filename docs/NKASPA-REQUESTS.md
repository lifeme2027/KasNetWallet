# Requests to NKaspa (from Kaspa Desktop Wallet)

Verified against NKaspa commit `f41c86a6d36da37243bcdd41e76662ac6c25b349` (0.1.9-alpha). The NKaspa source was **not** modified; the wallet works around each item in Core.

| # | Area | Observation | Wallet workaround | Proposed change |
|---|------|-------------|-------------------|-----------------|
| R1 | BIP39 | `new Mnemonic(string)` accepts a bad checksum (only sets `IsValidChecksum`). An import with a typo would silently derive a different wallet. | `MnemonicService.Normalize` throws when `!IsValidChecksum`. | Add `Mnemonic.ParseValidated` (additive, non-breaking): `patches/0001-mnemonic-parse-validated.diff` (`git apply --check` passes). |
| R2 | Fees | Floor is 100 sompi/gram; `SendEstimatedFees` throws below it. Node `getFeeEstimate` returns lower rates. | Clamp to the floor; show the exact fee before confirmation. | Confirm the floor against rusty-kaspa mempool policy; make the floor configurable per network. |
| R3 | Secrets | `Mnemonic.ToString()`, `DeriveExtKey(string? passphrase)` are string-only. | Documented (DECISIONS D5). | Span/char[] overloads so callers can wipe input. |
| R4 | RPC | There is no subscription (`notifyUtxosChanged`) in the minimal wRPC client, so the UI polls on demand. | Manual Refresh. | Add notifications to `KaspaWrpcJsonClient`. |
| R5 | Signing | Schnorr aux randomness in `BuildTransaction(sign:true)` is random, so signed bytes aren't reproducible (the tx **id** is). | Tests check id/fee/mass determinism + `Verify`. | Optional aux-rand injection on `TransactionBuilder` for test vectors. |
