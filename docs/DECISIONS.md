# Technical decisions (open / resolved)

Status legend: **OPEN** = needs owner confirmation; **DONE** = decided for the MVP, revisit if needed.

## D1 — UI base: plenteum-wallet-winforms license — OPEN
- https://github.com/plenteum/plenteum-wallet-winforms (commit 79881068, archived) has **no LICENSE file**; GitHub reports no license. Its README says it was forked from turtlecoin/turtle-wallet-winforms, which is no longer reachable on GitHub (404), so the upstream license could not be checked.
- Without a license, default copyright applies, so **no code, `.Designer.cs`, `.resx`, icons or images were copied.** The repo was used only as a reference for screen flow (splash/select → create/import/password → main wallet view with balance, copy address, send, status bar) and the dark visual style. All UI code in `src/KaspaWallet.WinForms` was written from scratch.
- Pending: (a) ask the plenteum authors to add a license (e.g. MIT/GPL), after which real migration could be considered; or (b) keep the clean-room rewrite (current state).

## D2 — BIP39 checksum enforcement — DONE (wrapper) / upstream request
NKaspa's `Mnemonic(string)` constructor accepts a phrase with a bad checksum and only sets `IsValidChecksum`. Core's `MnemonicService.Normalize` rejects it. Upstream request + patch: docs/NKASPA-REQUESTS.md, docs/patches/0001-*.diff.

## D3 — Default wRPC endpoints — OPEN
- Mainnet default: `wss://eva.kaspa.green/kaspa/mainnet/wrpc/json` (the NKaspa sample's default public node). Testnet-10 default: `ws://127.0.0.1:17210` (local kaspad, `--rpclisten-json`). Both can be changed in the start screen and are saved in `settings.json`.
- Need to decide: ship a public endpoint list / resolver, or require users to run their own node. The wallet checks the node's `networkId` and `isSynced` before broadcasting.

## D4 — Fee policy — OPEN
- NKaspa 0.1.9-alpha uses 100 sompi/gram as the minimum relay rate. A standard 1-in/2-out tx comes to about 0.002 KAS. Before mainnet use, check this against the current rusty-kaspa mempool policy. The NKaspa doc says this "minimum relay" floor is 100× the value usually quoted for kaspad (1000 sompi/kg).
- The MVP offers `MinimumRelay` (default) or `NodeEstimate` (getFeeEstimate normal bucket, clamped up to the NKaspa floor), and always shows the exact fee in the confirmation dialog. NKaspa rejects rates below its floor, so fees below 100 sompi/gram can't be built yet.

## D5 — Secret zeroization limits — DONE (documented)
- Byte buffers (derived AES key, password bytes, plaintext) are zeroed with `CryptographicOperations.ZeroMemory`. `HdAccount` holds an NKaspa `Key` and disposes it on Lock.
- .NET `string` (the mnemonic as typed/generated, and WinForms `TextBox.Text`) is immutable and can't be reliably wiped. Accepted for the MVP. A future option is SecureString-like char[] input plus an NKaspa API that takes a span.

## D6 — Keystore format — DONE
AES-256-GCM. The key comes from Argon2id (Konscious 1.3.1), defaults m=64 MiB, t=3, p=4, with a random 16-byte salt and a 12-byte nonce. The AAD binds version, network, address and the KDF params, and loading enforces minimums on the KDF params. The encrypted payload is the normalized mnemonic, not an xprv, so the account can be re-derived. There is one file per network: `%APPDATA%/KaspaDesktopWallet/wallet.<network>.json`.

## D7 — Address scope — DONE (MVP)
Only receive index 0 (`m/44'/111111'/0'/0/0`); change goes back to the same address. Multiple addresses, change chain, and address discovery are deferred.

## D8 — Testnet-10 mapping — DONE
`WalletNetwork.Testnet10` maps to NKaspa `Network.Testnet` (prefix `kaspatest`, `ktrv/ktub`). Expected node networkId is `testnet-10`.

## D9 — NKaspa reference mode — DONE
Core uses a `ProjectReference` to a local read-only clone (default `../kdw-src/NKaspa`, override with `-p:NKaspaRoot=...`). It was verified at NKaspa commit `f41c86a6d36da37243bcdd41e76662ac6c25b349`. The NKaspa source is never modified here.
