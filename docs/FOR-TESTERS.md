# For testers — Kaspa Desktop Wallet 0.1.0 (alpha)

Answers to section 5 of `testcases/kaspa-desktop-wallet.md`. They are based on the actual code at this commit. **TBD** items are also tracked in `docs/DECISIONS.md` (mostly D11, D12, D14).
Times are Asia/Shanghai (UTC+8).

## 1. Build artifact
- **Current build (MVP-1 report fixes):** `KaspaDesktopWallet-0.1.0-d8e460c-win-x64.zip`
  - Built from **commit `d8e460c443a46851766d657a615bab45e439b8c9`** in a fresh `git clone` with an empty `git status` (KDW-01).
  - NKaspa: `f41c86a6d36da37243bcdd41e76662ac6c25b349`.
  - SDK: 8.0.427.
  - Build time: 2026-10-31 16:40:51 UTC+8.
  - **SHA256:** `f09c8ac74820c2d0edaf9b79af3dae21545f1109edc6826d9ca2172b9b5212f2`, also listed in `SHA256SUMS`.
- **BUILD-INFO:** `BUILD-INFO.txt` is inside the zip (commit, NKaspa pin, SDK, build time, per-file sha256). The same content plus the zip sha256 is in `KaspaDesktopWallet-0.1.0-d8e460c-win-x64.BUILD-INFO.txt` next to the zip.
- **Check the commit:** `strings -el KaspaWallet.Core.dll | grep d8e460c` (InformationalVersion).
- **Superseded:** the old zip `93158c3a…` was built from a dirty tree (1fc4fb7 + uncommitted changes). It was moved to `kdw-artifacts/superseded/`; do not test it.
- **Form:** portable zip with no installer. Contents:
  - the publish output of `dotnet publish src/KaspaWallet.WinForms -c Release -r win-x64 --self-contained false`
  - NOTICE
  - NKaspa-LICENSE.txt
- **Runtime:** framework-dependent, so it needs the **.NET 8 Desktop Runtime x64** installed. No admin rights are needed. A self-contained build is TBD.
- **Build settings:**
  - `EnableUnsafeBinaryFormatterSerialization=false` (KDW-03).
  - PDB paths are mapped to `/_/` via PathMap (KDW-02). NKaspa's own PDB still contains its clone path.
- **Reproducibility:** the zip entries use fixed timestamps. The publish output itself is not guaranteed bit-reproducible, so verify against SHA256SUMS and BUILD-INFO.
- **Code signing:** none (unsigned), so expect a SmartScreen warning. Signing is TBD.
- **Entry point:** `KaspaWallet.WinForms.exe`. DPI mode is PerMonitorV2.

## 2. NKaspa version
- **Pinned:** `lifeme2027/NKaspa` commit `f41c86a6d36da37243bcdd41e76662ac6c25b349` (0.1.9-alpha, MIT), referenced as a ProjectReference to a local clone (`../kdw-src/NKaspa`).
- **Enforced:** `Directory.Build.props` → `VerifyNKaspaPin` **fails the build** if the clone is at another commit or has local modifications.
- **Unmodified:** the wallet does not modify NKaspa. Gaps are recorded in `docs/NKASPA-REQUESTS.md` (R1–R10) and `docs/patches/`.

## 3. Network and node
- **Default network:** **testnet-10**. testnet-11 is not wired (TBD).
- **Default endpoint:** `ws://127.0.0.1:18210`. **Confirmed:** the tn10 JSON wRPC port is 18210 (17210 is Borsh, 16210 is gRPC; source: rusty-kaspa `network.rs default_json_rpc_port`).
  - **kaspad must be started with `--rpclisten-json`.** Without it, nothing listens on 18210. Example: `kaspad --testnet --netsuffix=10 --rpclisten-json=default --utxoindex`.
  - The wallet speaks JSON only. A Borsh port (17210) does not work.
  - Public nodes: the `/wrpc/json` path on the same host also works, e.g. `wss://<host>/kaspa/testnet-10/wrpc/json`.
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
  - The failure count is **saved in `unlock-state.<network>.json`, so it survives restarts** (KDW-09).
- **Error handling (KDW-05):** none of these crash the app or show a stack trace; each gives a friendly message:
  - an empty password in the unlock box
  - a 4th attempt during the delay (shows "please wait N s")
  - a corrupted, truncated, or tampered wallet file (bad JSON, bad base64, unknown network, KDF out of range)
  - a corrupt settings.json (falls back to testnet-10 with mainnet disabled)
  - With a corrupt wallet file, the start screen shows "Wallet file problem: …" instead of crashing at launch.
  - Global exception handlers (ThreadException, UnhandledException, UnobservedTaskException) log only the redacted message.
- **KDF limits (KDW-08):** the file must have m ≤ 1 GiB, t ≤ 10, p ≤ 16, and the file must be ≤ 64 KiB. Anything above is rejected *before* Argon2 runs.
- **File permissions (KDW-06):** wallet and unlock-state files are created 0600 on Linux/macOS. On Windows they inherit the %APPDATA% ACL (WU-K05 still needs a real test).
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
- **Backup verification (KDW-04):** after the phrase window closes, you must enter the words at **3 random positions**. The wallet file is not written until this check passes; a wrong answer clears the boxes so you can try again or go back and view the phrase.
- **Log redaction (KDW-10):** these phrase formats are also masked from 8 words: comma-separated, `1.word` / `1. word` numbering, hyphen-joined, and JSON arrays. Mixed-case base64 strings of 43 or more characters (32-byte keys) are masked too.
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
- **Fee cap (KDW-11):** cap = max(`MaxFeeFloorSompi`, amount × `MaxFeePercent`%). The default is max(0.01 KAS, 1%). Both values can be changed in settings.json, and a fee above the cap is **rejected** with the reason.
- **Change folded into the fee (KDW-11):** change below NKaspa's minimum change (about 0.1 KAS pre-Toccata) cannot become a change output.
  - The confirm dialog shows `WARNING: change of X KAS … ADDED TO THE FEE`.
  - A **second** confirmation is required.
  - Under the default cap this usually exceeds the limit and is rejected. Adjust the amount or raise the cap.
- **Submit timeout:** shows "Submission status unknown … refresh". The tx is recorded in history as Pending. Re-submitting the same signed tx cannot double-spend.
  - Low/high priority, a custom fee, and Max/sweep are TBD.

## 12. UI languages
English only. The amount format is invariant: always `.`, 8 decimal places, no scientific notation. zh-CN is TBD.

## 13. Not implemented (expect failures)
QR code, history rows for incoming txs, explorer links, a change-password UI, mnemonic re-display, an installer/uninstaller, i18n, and full accessibility names/Narrator review.
