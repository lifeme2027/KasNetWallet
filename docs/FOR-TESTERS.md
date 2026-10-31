# For testers — Kaspa Desktop Wallet 0.1.0 (alpha)

Answers to section 5 of `testcases/kaspa-desktop-wallet.md`. They are based on the actual code at this commit. **TBD** items are also tracked in `docs/DECISIONS.md` (mostly D11, D12, D14).
Times are Asia/Shanghai (UTC+8).

## 1. Build artifact
- **Form:** portable zip with no installer, `KaspaDesktopWallet-0.1.0-win-x64.zip`. Built on the Linux box at `` with `dotnet publish src/KaspaWallet.WinForms -c Release -r win-x64 --self-contained false`. It contains NOTICE and NKaspa-LICENSE.txt.
- **Runtime:** framework-dependent, so it needs the **.NET 8 Desktop Runtime x64** installed. No admin rights are needed. A self-contained build is TBD.
- **SHA256:** listed in `SHA256SUMS`. The value for the build from this commit's code is
  `93158c3a6bde318cde786d617ca00bd99dd7452841c56a01d09e21108368a629  KaspaDesktopWallet-0.1.0-win-x64.zip`.
  The zip entries use fixed timestamps. The publish output itself is not guaranteed bit-reproducible, so verify against SHA256SUMS.
- **Code signing:** none (unsigned), so expect a SmartScreen warning. Signing is TBD.
- **Entry point:** `KaspaWallet.WinForms.exe`. DPI mode is PerMonitorV2.

## 2. NKaspa version
- **Pinned:** `lifeme2027/NKaspa` commit `f41c86a6d36da37243bcdd41e76662ac6c25b349` (0.1.9-alpha, MIT), referenced as a ProjectReference to a local clone (`../kdw-src/NKaspa`).
- **Enforced:** `Directory.Build.props` → `VerifyNKaspaPin` **fails the build** if the clone is at another commit or has local modifications.
- **Unmodified:** the wallet does not modify NKaspa. Gaps are recorded in `docs/NKASPA-REQUESTS.md` (R1–R10) and `docs/patches/`.

## 3. Network and node
- **Default network:** **testnet-10**. testnet-11 is not wired (TBD).
- **Default endpoint:** `ws://127.0.0.1:18210`. This is the JSON wRPC default for testnet when running `kaspad --testnet --netsuffix=10 --rpclisten-json=default --utxoindex`.
  - The test plan's `--rpclisten-json=0.0.0.0:17210` also works; type `ws://<host>:17210` in the start screen.
- **Custom node:** yes. Use the URL box on the start screen; it is saved to settings.json.
  - Only `ws://` and `wss://` (JSON wRPC) are accepted.
  - gRPC ports 16110/16210/16310 and non-ws schemes are rejected with a message (KW-062).
  - The node's `networkId` must equal the wallet network, or the connection is refused (KW-066).
  - Sending is refused when `isSynced=false` (KW-067).
- **Mainnet switch:** off by default (`MainnetEnabled=false`).
  - To enable it, select Mainnet and accept a risk dialog (default button = No).
  - Core refuses to connect or broadcast on mainnet without the flag.
  - **Every mainnet send** shows a confirmation dialog plus a **second red dialog** that requires typing `SEND MAINNET`.
  - During acceptance testing, leave mainnet disabled.
- **Timeouts and reconnect:**
  - Per-call timeout is `RpcTimeoutSeconds` (default 15 s).
  - On WebSocket, IO, or timeout failures the wallet reconnects with backoff of 1 s, 2 s, then 4 s (capped at 30 s, 4 attempts), then reports "Node unavailable".
  - **Submit is never auto-retried.**
  - All RPC calls are async, so the UI is not blocked. During a call the window is disabled and shows a wait cursor.

## 4. Wallet file
- **Location:** `%APPDATA%\KaspaDesktopWallet\wallet.<network>.json` (e.g. `wallet.testnet10.json`). Settings are in `settings.json` and local history in `history.<network>.json`, all in the same folder.
- **Format:** JSON v1 with `kdf{m,t,p,salt}`, `nonce`, `ciphertext`, `tag`, `network`, and `address`.
  - The plaintext is the normalized mnemonic, encrypted with **AES-256-GCM**.
  - The AAD binds the version, network, address, and KDF params.
- **KDF:** **Argon2id** (Konscious 1.3.1), m=64 MiB, t=3, p=4, 16-byte salt. KDF parameters below the minimum are rejected when loading.
- **Password:** **empty passwords are forbidden**, and new passwords need at least 8 characters. Passwords are NFKC-normalized (KW-070).
  - After 3 failed unlocks, a growing delay applies (2 s, 4 s … 30 s).
  - Changing the password (`WalletManager.ChangePassword`) re-encrypts the file with a new salt and nonce. Core only; **there is no UI button yet (TBD)**.
- **Writes:** atomic (temp file, then rename). An existing wallet is never overwritten.
- **ACL:** inherits the user profile's permissions. Explicit ACL hardening is TBD.

## 5. Logs, telemetry, updates
- **Log:** `%LOCALAPPDATA%\KaspaDesktopWallet\logs\wallet-YYYYMMDD.log`. It records only connection warnings, broadcast tx ids, and errors.
  - Every line goes through `SecretRedactor`, which masks xprv/kprv/ktrv, hex runs of 64+ characters, and runs of 12+ BIP39 words.
  - Exceptions are logged as type + message only. Unit tests cover this.
  - Note: because the redactor masks all 64+ hex runs, tx ids appear as `[REDACTED]` in the log.
- **Outbound connections:** no telemetry, crash upload, or auto-update. The only connection is to the configured wRPC node.

## 6. Transaction history
- **Source:** NKaspa has no history API, so the source is **local records** of transactions sent from this wallet (`history.<network>.json`), checked against the node's UTXO set and virtual DAA score.
- **States:**
  - Pending: submitted.
  - Accepted: change output seen. Confirmations = virtualDaa − blockDaaScore.
  - Confirmed: confirmations ≥ `ConfirmationDepthDaa` (default 100).
  - **Reorged:** a spent input is back in the UTXO set.
  - Dropped: inputs still unspent 600 DAA after submit.
- **When it updates:** on Refresh and after each send.
- **Not covered:**
  - Incoming transactions are visible only as balance/UTXOs, not as history rows.
  - Explorer links: not implemented.
  - Indexer: TBD.

## 7. Mnemonic and accounts
- **Generation:** 24 words (Core also supports 12).
- **Import:** 12/15/18/21/24 words.
- **Hard reject** with a clear error and no wallet file created:
  - bad checksum, e.g. one word changed
  - unknown word (the error gives the word's position)
  - wrong word count
  - zero-width characters or BOM
- **Normalization:** NFKC + lowercase. Tab, newline, U+3000, and U+00A0 are treated as spaces.
- **BIP39 passphrase:** supported in Core; **not exposed in the UI (TBD)**.
- **Multi-account:** no. Account 0 only.
- **Viewing/exporting the mnemonic after creation:** not implemented (TBD).
- **Legacy paths** (e.g. 972): not supported (TBD).

## 8. Addresses and change
- **Address:** a single receive address, `m/44'/111111'/0'/0/0`. No new address per tx and no gap-limit scan.
- **Change:** **returned to the same address** (no /1/i change chain). TBD, see D7/D14.
- **Address checks:** the address is trimmed; the wrong-network prefix and bad checksums are rejected.
- **Coin selection:** NKaspa's builder over a deterministically sorted UTXO set.
  - Change below the NKaspa minimum is folded into the fee, and the confirmation dialog shows the real fee.
  - Dust outputs below 0.02 KAS are rejected.

## 9. Clipboard and auto-lock
- **Clipboard:** copying the address puts it on the clipboard and **clears it after 30 s** if unchanged (`ClipboardClearSeconds`). It is excluded from Win+V history and cloud clipboard.
- **Mnemonic copy:** the mnemonic screen has no copy button.
- **Auto-lock:** after **5 minutes** with no keyboard or mouse input (`AutoLockMinutes`). After locking, restart to unlock.

## 10. P2SH / ECDSA / PSKT
- The wallet's own keys use Schnorr P2PK only.
- **Sending to** P2SH or ECDSA addresses: untested, TBD (KW-027).
- **PSKT:** not supported.

## 11. Mass and fees
- **Mass limit:** configurable per network in settings.json:
  ```
  "MassPolicies": { "Testnet10": { "ToccataActive": false, "MaxTransactionMass": null } }
  ```
  - With `ToccataActive=false` the cap is 100000 (pre-Toccata); with `true` it is 500000.
  - The default is false (conservative). The Toccata activation status on testnet/mainnet is **TBD**.
  - Over the cap, the send is rejected with "consolidate UTXOs or send less".
- **Fees:** MinimumRelay (100 sompi/gram, the NKaspa floor) or NodeEstimate (normal bucket).
  - Low/high priority, a custom fee, and Max/sweep are TBD.

## 12. UI languages
English only. The amount format is invariant: always `.`, 8 decimal places, no scientific notation. zh-CN is TBD.

## 13. Not implemented (expect failures)
QR code, history rows for incoming txs, explorer links, a change-password UI, mnemonic re-display, an installer/uninstaller, i18n, and full accessibility names/Narrator review.
