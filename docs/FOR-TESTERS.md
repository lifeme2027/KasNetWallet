# For testers — KasNet Wallet 0.1.0 (alpha)

Answers to section 5 of `testcases/kaspa-desktop-wallet.md`. They are based on the actual code at this commit. **TBD** items are also tracked in `docs/DECISIONS.md` (mostly D11, D12, D14).
Times are Asia/Shanghai (UTC+8).

## 1. Build artifact
- **Current build (retest 97529ab fixes N10/N11/N12, D29; NKaspa dea2697, D25):** `KasNetWallet-0.1.0-97529ab-win-x64.zip`
  - Built from **commit `97529ab0be3aec56906e524f54f3df379894ddb0`** in a fresh `git clone` with an empty `git status` (KDW-01).
  - NKaspa: `dea269794937328b1c93a7985ff88b9e9f462a09` (0.1.10-alpha).
  - SDK: 8.0.427.
  - Build time: 2026-10-31 20:38:12 UTC+8.
  - **SHA256:** `41e6ef4cec2554393271e0b5dbd255749c0770b05b8b5cac23de3bbc0e8c3cc5`, also listed in `SHA256SUMS`.
- **BUILD-INFO:** `BUILD-INFO.txt` is inside the zip (commit, NKaspa pin, SDK, build time, per-file sha256). The same content plus the zip sha256 is in `KasNetWallet-0.1.0-97529ab-win-x64.BUILD-INFO.txt` next to the zip.
- **Check the commit:** `strings -el KasNetWallet.Core.dll | grep 97529ab` (InformationalVersion).
- **Older builds kept in place** (not moved; listed in `SHA256SUMS`; test the current build instead):
  - `KasNetWallet-0.1.0-506a924-win-x64.zip` (`31ae88bb…`, N10–N12 fixes, before N13–N15)
  - `KasNetWallet-0.1.0-1b584ec-win-x64.zip` (`17f5c7dd…`, N7–N9 fixes, before N10–N12)
  - `KasNetWallet-0.1.0-806e349-win-x64.zip` (`acf071b1…`, N4–N6 fixes, before N7–N9)
  - `KasNetWallet-0.1.0-89772b3-win-x64.zip` (`bdb53701…`, M1/M2 fixes, before N4–N6)
  - `KasNetWallet-0.1.0-46ffee7-win-x64.zip` (`492e9536…`, NKaspa dea2697 pin, before M1/M2)
  - `KasNetWallet-0.1.0-6617246-win-x64.zip` (`fcbcd9fc…`, retest 570d403 fixes, NKaspa 5c49251)
- **Superseded** (all in `kdw-artifacts/superseded/`; do not test them):
  - `KasNetWallet-0.1.0-570d403-win-x64.zip` (`38bf820a…`, retest da66c4d fixes)
  - `KasNetWallet-0.1.0-da66c4d-win-x64.zip` (`c284661b…`, retest 5747f6b fixes)
  - `KasNetWallet-0.1.0-5747f6b-win-x64.zip` (`601721bd…`, F1–F5)
  - `KaspaDesktopWallet-0.1.0-0c7e556-win-x64.zip` (`066ace85…`, NKaspa 5c49251 adoption, pre-rename)
  - `…-cc6337e-win-x64.zip` (`e77e9b53…`, round 3, NKaspa f41c86a)
  - `…-f31dc83-win-x64.zip` (`42180cc6…`, round 2)
  - `…-d8e460c-win-x64.zip` (`f09c8ac7…`, MVP-1 fixes)
  - the old zip `93158c3a…`, which was built from a dirty tree (1fc4fb7 + uncommitted changes)
- **Form:** portable zip with no installer. Contents:
  - the publish output of `dotnet publish src/KasNetWallet.WinForms -c Release -r win-x64 --self-contained false`
  - NOTICE
  - NKaspa-LICENSE.txt
- **Runtime:** framework-dependent, so it needs the **.NET 8 Desktop Runtime x64** installed. No admin rights are needed. A self-contained build is TBD.
- **Build settings:**
  - `EnableUnsafeBinaryFormatterSerialization=false` (KDW-03).
  - PDB paths are mapped to `/_/` via PathMap (KDW-02). NKaspa's own PDB still contains its clone path.
- **Reproducibility:** the zip entries use fixed timestamps. The publish output itself is not guaranteed bit-reproducible, so verify against SHA256SUMS and BUILD-INFO.
- **Code signing:** none (unsigned), so expect a SmartScreen warning. Signing is TBD.
- **Entry point:** `KasNetWallet.WinForms.exe`. DPI mode is PerMonitorV2.

## 2. NKaspa version
- **Pinned:** `lifeme2027/NKaspa` NuGet package `NKaspa` 0.1.10-alpha (MIT) from nuget.org, referenced as a PackageReference (see D31).
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
- **Location:** `%APPDATA%\KasNetWallet\wallet.<network>.json` (e.g. `wallet.testnet10.json`). Settings are in `settings.json` and local history in `history.<network>.json`, all in the same folder.
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
- **Log:** `%LOCALAPPDATA%\KasNetWallet\logs\wallet-YYYYMMDD.log`. It records only connection warnings, broadcast tx ids, and errors.
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
- **No fixed-amount fee cap (retest N3).** Fees are checked as a *rate*. Abnormal fees produce a **warning**; you can still send after a second confirmation (broadcast is refused unless the warning was acknowledged).
  - "Abnormally high fee": total fee ÷ fee mass > `FeePolicy.RateMultiplier` (default 10) × max(node normal estimate, 100) sompi/gram.
  - "High fee": total fee > max(`FeePolicy.WarnFloorSompi` = 0.01 KAS, `FeePolicy.WarnPercent` = 1% of the amount), while the rate itself is normal (large tx).
  - Ranges (settings.json, see §14): RateMultiplier 1.5–1000, WarnPercent 0.01–100, WarnFloorSompi 10000–100000000000.
- **Dust change (N3):** if the change would be below NKaspa's minimum change (~0.1 KAS pre-Toccata), the wallet first retries with **one extra UTXO** so the change becomes a valid output (the summary then says "added 1 extra input …"). Only if no UTXO helps, the dialog shows `Change of X KAS … ADDED TO THE FEE` and an extra confirmation is needed.
- **Burned change is blocked (N4, NKaspa bug R11):** if NKaspa would drop a *valid* change (≥ minimum change) into the fee because the change output would exceed the mass limit (e.g. 0.1 KAS from a single 50 KAS UTXO at 150 sompi/gram), the wallet compares fee vs expected fee and **refuses** with "Blocked: this transaction would burn X KAS of change …". Try a larger amount, or Send max.
- **Send max (N2):** button "Send max…". Amount = sum of spent UTXOs − fee, exactly one output, no change. If all UTXOs together exceed the mass cap, it uses the largest UTXOs that fit; the summary shows "Inputs: N of M UTXO". Error texts now point to Send max instead of "send the full balance".
- **Submit timeout:** shows "Submission status unknown … refresh". The tx is recorded in history as Pending. Re-submitting the same signed tx cannot double-spend.
  - Low/high priority and a custom fee are TBD.

## 11a. Unlock throttle recovery (N1)
- `unlock-state.<net>.json` unreadable/corrupt → reset to 0 failures, file deleted, info dialog "…was unreadable and has been reset".
- Last-failure time in the future (clock change/tampering) → clamped to now and re-saved, info dialog; no permanent lock. Negative/huge failure counts are clamped to 0–1000.

## 11b. Round 3 (retest-2) behaviour

- **Fee 100 vs 101 sompi/gram (R3-5):**
  - *Minimum relay* mode charges compute mass only.
  - *Node estimate* mode charges rate × (compute + storage mass). For small amounts, storage mass dominates.
  - A fee ≥ 50 % of the amount shows "Abnormally high fee relative to the amount", which must be acknowledged.
  - Example: 0.13 KAS from a 1 KAS UTXO.
- **Send max with too many UTXOs (R3-7):** the wallet uses as many of the largest UTXOs as exactly fit the mass limit. A warning then says "Only N of M UTXOs fit … X KAS will STAY". To consolidate, Send max to your own address until 1 UTXO is left.
- **Very small amounts (R3-4):** "Amount too small … storage mass (KIP-9)". At the 100,000 mass cap the practical minimum is about 0.1–0.2 KAS.
- **Unlock state not writable (R3-1):** e.g. a read-only file or folder, or a directory with the same name. After a wrong password the strictest 30 s delay applies to every attempt, and a notice says the counter could not be saved. Note: as root/Administrator a 0400 file is still writable, so use a directory named `unlock-state.testnet10.json` in the wallet folder to test this.
- **Deleting or corrupting the state file resets the counter (R3-2).** This is intended and documented in DECISIONS D18. Brute-force protection comes from the keystore's Argon2id cost, not from the counter.
- **State file larger than 4 KB (R3-3):** treated as corrupt and reset, with a notice.

## 11c. NKaspa 5c49251 — behaviour changes to retest

- **Toccata auto-detection:** the mass rules now follow the node's DAA score. On testnet-10 (activated) the fee floor is 100 sompi/gram, the mass cap is the post-Toccata limit and MinimumChange is about 0.02 KAS (previously 0.1 KAS). `"ToccataActive": false` in settings.json forces pre-Toccata rules, which use a **1 sompi/gram** floor, so fees become about 100× lower.
- **Change between about 0.02 and 0.1 KAS** is now a normal change output on testnet-10. Before, it was "dust folded into the fee".
- **Small change (below MinimumChange)** is still added to the fee, with a warning that needs acknowledgement.
- **Large change that cannot fit (NKaspa now refuses to burn it):**
  - The wallet first tries adding an input.
  - Otherwise it blocks with "Blocked: the change output does not fit…", or "Amount too small … storage mass" for small amounts.
- **Mnemonic import:**
  - Bad checksums are rejected (by both NKaspa and the wallet).
  - Different case and Unicode compatibility forms are accepted.
  - Zero-width characters are still rejected by the wallet.
- **Logs and errors:** `ExtKey.ToString()` and `Mnemonic.ToString()` no longer contain secrets.
- **wRPC:** frames above 16 MiB are refused. The connect and request timeouts follow `RpcTimeoutSeconds`.

## 11d. Retest 0c7e556 fixes (F1–F5)
- **F1:** `ResilientWalletNode` treats NKaspa's `InvalidOperationException` for an oversized (>16 MiB) WebSocket frame, and the follow-up "WebSocket is not connected", as a dead connection: it drops the socket and reconnects with backoff.
- **F2:** when the change lands just around `MinimumChange` (tn10 ≈ 1,958,800–2,000,004 sompi), the wallet first tries adding one more UTXO. If that does not help, the block message now says the change is *too small to be its own output* (storage mass) and too large to burn as fee — not "change output exceeds the mass limit".
- **F3:** one deterministic rule near the boundary: a fold of ≥ 95% of `MinimumChange` (or NKaspa's fold-cap refusal) is always blocked; only smaller change is folded with a warning. With forced pre-Toccata the 9,997,964–10,000,000 band is now always "blocked", and outcomes go fold → block → normal change monotonically.
- **F4:** before building (normal send and Send max), the node must report `IsSynced=true` and a DAA score that is non-zero, at or above the Toccata activation (mainnet 474,165,565 / tn10 467,579,632), and not lower than a score seen earlier in the session. Otherwise building is blocked with a message. If the status cannot be read, building is blocked too.
- **F5 — zero-width characters, wallet vs library:** the **wallet** (`MnemonicService.Normalize`, used by every UI path) **rejects** any Unicode format character (U+200B/200C/200D, U+FEFF BOM, U+00AD, …) with "Word #N of the recovery phrase contains an invisible character (U+XXXX …)". **NKaspa** `Mnemonic.Parse` **strips** them silently and accepts the phrase. This difference is intentional: the wallet never uses a phrase different from what the user typed. Code that calls NKaspa directly gets the lenient behaviour.

## 12. UI languages
English only. The amount format is invariant: always `.`, 8 decimal places, no scientific notation. zh-CN is TBD.

## 13. Not implemented (expect failures)
QR code, history rows for incoming txs, explorer links, a change-password UI, mnemonic re-display, an installer/uninstaller, i18n, and full accessibility names/Narrator review.

## 14. Settings ranges (N6)
Out-of-range values in settings.json are reset to the default on start and listed in a "Settings" dialog (also logged):
RpcTimeoutSeconds 1–300 · ClipboardClearSeconds 5–600 · AutoLockMinutes 1–1440 · ConfirmationDepthDaa 1–1000000 · Network must be a known value · WrpcUrl must be ws:// or wss:// · MassPolicies[*].MaxTransactionMass 5000–1000000 · FeePolicy as in §11.
The old `MaxFeeFloorSompi`/`MaxFeePercent` keys are ignored.

## 15. Error messages and redaction (N5, N7, R3-6, R3-8)

- **Settings:** `"Network": "testnet10"`, `"Mainnet"` or `1` are all accepted. An unknown value (e.g. `"Testnet11"`) resets only `Network`, with a notice.
- **Redaction:** txids and other 64-hex values stay visible in logs and errors unless labelled as a key or seed. Request ids and GUIDs stay visible. Mnemonics are masked in these forms: wide spacing, `<li>` lists, numbered lists of 6 or more words, and "and/then"-joined words. Lowercase base64url keys (43 chars) are masked.

- Nested errors are no longer duplicated ("corrupted or unsupported … Restore it from backup" appears once).
- The redactor also masks base64url values (`-`/`_`, ≥43 chars, mixed case) and mnemonic word runs separated by wide gaps (up to 48 separator characters: spaces, tabs, newlines, `|`, bullets, numbering).



## 11e. Retest 5747f6b fixes

1. **G1**
   - Have both `%APPDATA%\KasNetWallet` and `%APPDATA%\KaspaDesktopWallet`, with a wallet only in the old one. Start the app: you should get "Old wallet folder found", and Import should make the wallet appear.
   - After a fresh migration you should get the "encrypted copy / OLD password" notice. Deleting needs two Yes clicks; No keeps the folder.
2. **G2:** tn10, single 10 KAS UTXO, amount = 10 − 0.002036 − 0.0198 KAS.
   - You should get "Change cannot be sent" with a "LOWER the amount to X KAS" hint.
   - Yes sends X with normal change.
   - No sends everything except the fee (no change output).
   - Cancel leaves the amount for you to edit.
   - Moving the amount gradually should never alternate between fold and block.
3. **G3:** switching to another node that is a few hundred DAA behind should not block. Being more than 600 behind the highest DAA seen on the same node blocks.
4. **G5:** mark files in the old folder read-only, then migrate. Wrong-password attempts (unlock-state) must still save.


## 11f. Retest da66c4d fixes

1. **S5:** the new folder has tn10 wallet B and the old folder has tn10 A + mainnet.
   - The prompt lists both and warns about the same-name DIFFERENT wallet.
   - After Import, A sits next to B as `wallet.testnet10.legacy-xxxxxxxx.json`, and B is unchanged.
   - Deleting the old folder before importing is refused with a message naming the file.
2. **S6:** the old folder holds only tn10 A and the new one holds a different tn10 B. You are prompted to import.
3. **NodeEstimate (post-Toccata, tn10 rate ≈102):**
   - No "Fee estimate did not converge".
   - Order is fold → block → change, the same as default mode.
   - Every block offers a lower amount that builds.
   - The fee for ~0.12 KAS change is ≈0.002 KAS (it was 0.084).
4. **Blocked dialog:** pressing Enter is Cancel in both forms (Yes/No/Cancel and OK/Cancel). *Windows check.*
5. **Follow-ups**
   - Declining the delete brings one reminder on the next start (marked "Reminder"), then nothing.
   - Import brings `settings.json` (e.g. RpcTimeout=42).
   - If the old and new folders are the same folder, delete and import are refused.

## 11g. Retest 570d403 fixes
1. **N1:** old folder with an imported wallet plus `backup/`, `wallet.testnet10.json.bak` and `notes.txt`.
   - The delete offer and the confirmation list `backup/`, `notes.txt` and the `.bak` as kept.
   - After deleting, only the verified wallet, its identical history, unlock-state and settings are gone; the kept items are still there and the old folder remains.
   - With nothing else in it, the old folder is removed.
2. **N3:** a wallet imported under a new name has `history.<net>.legacy-xxxxxxxx.json` next to it; the hint says to rename both.
3. **N2 (fixed):** pre-Toccata, rate 500 or 1000, balance 0.5 / 1 / 10 KAS: the order is fold → block → change (block from a remainder of 0.095 KAS), and every block offers a lower amount that builds.
4. **Settings:** a value you saved in the new version (even the default, e.g. RpcTimeout 15) is not overwritten by the old one; only settings missing from the new file are imported.
5. **Mainnet:** if the old settings use mainnet and the new ones have no network saved yet, the import prompt says the app will start on MAINNET.
6. **Known issues:**
   - The node fee estimate does not charge storage mass (only the mass limit is checked).
