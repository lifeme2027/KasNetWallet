# Technical decisions (open / resolved)

Status legend: **OPEN** = needs owner confirmation; **DONE** = decided for the MVP, revisit if needed.

## D1 — UI code origin — RESOLVED
- All UI code in `src/KasNetWallet.WinForms` is original code written for this project. No third-party wallet UI code was copied or adapted, and no such project is a dependency, so none is listed in NOTICE.
- Any future copied or derived file must carry a header naming its source and must be listed in NOTICE and here.

## D2 — BIP39 checksum enforcement — DONE (wrapper) / upstream request
NKaspa's `Mnemonic(string)` constructor accepts a phrase with a bad checksum and only sets `IsValidChecksum`. Core's `MnemonicService.Normalize` rejects it. Upstream request + patch: docs/NKASPA-REQUESTS.md, docs/patches/0001-*.diff.

## D3 — Default wRPC endpoints — OPEN
- Mainnet default: `wss://eva.kaspa.green/kaspa/mainnet/wrpc/json` (the NKaspa sample's default public node). Testnet-10 default: `ws://127.0.0.1:18210` (local kaspad `--testnet --netsuffix=10 --rpclisten-json=default`; rusty-kaspa default JSON wRPC port for testnet is 18210, while 17210 is Borsh. The tester plan binds JSON explicitly to :17210, which works if entered as a custom URL). Both can be changed in the start screen and are saved in `settings.json`.
- Need to decide: ship a public endpoint list / resolver, or require users to run their own node. The wallet checks the node's `networkId` and `isSynced` before broadcasting.

## D4 — Fee policy — OPEN
- NKaspa 0.1.9-alpha uses 100 sompi/gram as the minimum relay rate. A standard 1-in/2-out tx comes to about 0.002 KAS. Before mainnet use, check this against the current rusty-kaspa mempool policy. The NKaspa doc says this "minimum relay" floor is 100× the value usually quoted for kaspad (1000 sompi/kg).
- The MVP offers `MinimumRelay` (default) or `NodeEstimate` (getFeeEstimate normal bucket, clamped up to the NKaspa floor), and always shows the exact fee in the confirmation dialog. NKaspa rejects rates below its floor, so fees below 100 sompi/gram can't be built yet.

## D5 — Secret zeroization limits — DONE (documented)
- Byte buffers (derived AES key, password bytes, plaintext) are zeroed with `CryptographicOperations.ZeroMemory`. `HdAccount` holds an NKaspa `Key` and disposes it on Lock.
- .NET `string` (the mnemonic as typed/generated, and WinForms `TextBox.Text`) is immutable and can't be reliably wiped. Accepted for the MVP. A future option is SecureString-like char[] input plus an NKaspa API that takes a span.

## D6 — Keystore format — DONE
AES-256-GCM. The key comes from Argon2id (Konscious 1.3.1), defaults m=64 MiB, t=3, p=4, with a random 16-byte salt and a 12-byte nonce. The AAD binds version, network, address and the KDF params, and loading enforces minimums on the KDF params. The encrypted payload is the normalized mnemonic, not an xprv, so the account can be re-derived. There is one file per network: `%APPDATA%/KasNetWallet/wallet.<network>.json`.

## D7 — Address scope — DONE (MVP)
Only receive index 0 (`m/44'/111111'/0'/0/0`); change goes back to the same address (no /1/i change chain yet — TBD, see D14). Multiple addresses, change chain, and address discovery are deferred.

## D8 — Testnet-10 mapping — DONE
`WalletNetwork.Testnet10` maps to NKaspa `Network.Testnet` (prefix `kaspatest`, `ktrv/ktub`). Expected node networkId is `testnet-10`.

## D9 — NKaspa reference mode — DONE
Core uses a `ProjectReference` to a local read-only clone (default `../kdw-src/NKaspa`, override with `-p:NKaspaRoot=...`). It was verified at NKaspa commit `5c49251dfc2e1e5396344f802514bc6ee5e828fa`. The NKaspa source is never modified here.

## D10 — Default network & mainnet gate — DONE
Default is **testnet-10**. Mainnet needs two things: selecting it plus confirming an "Enable mainnet" risk dialog, which sets `MainnetEnabled=true` in settings.json. Core refuses to connect or broadcast on mainnet without that flag. Every mainnet send then shows a **second** red dialog that requires typing `SEND MAINNET`.

## D11 — Mass limit / Toccata — OPEN (configurable)
`settings.json → MassPolicies[network] = { ToccataActive, MaxTransactionMass }`. With `ToccataActive=false` the cap is NKaspa's pre-Toccata 100000; with `true` it is the network's post-Toccata compute limit (500000). The default is **false (conservative) for every network**.
TBD: confirm the Toccata activation status on tn10, tn11, and mainnet, then change the defaults.

## D12 — Tx history & confirmations — DONE (MVP) / indexer TBD
NKaspa has no history or subscription API. Outgoing txs are recorded locally in `history.<network>.json`. The tracker re-checks them against the UTXO set and virtual DAA score:
- Pending → Accepted (change output seen; confirmations = virtualDaa − blockDaaScore)
- Accepted → Confirmed (≥ `ConfirmationDepthDaa`, default 100)
- → Reorged (a spent input reappears)
- → Dropped (inputs still unspent 600 DAA after submit)

Incoming txs are only visible as UTXOs/balance. TBD: an indexer (e.g. the api.kaspa.org REST for tn10) for full history. Also TBD: confirmation depth, and tracking of no-change txs (inferred from inputs disappearing).

## D13 — Node resilience — DONE
`ResilientWalletNode`:
- per-call timeout (`RpcTimeoutSeconds`, default 15)
- reconnect with exponential backoff (1, 2, 4… s, capped at 30 s, 4 attempts)
- **submit is never auto-retried**, to avoid duplicate broadcasts
- only `ws://`/`wss://` URLs; gRPC ports 16110/16210 are rejected
- node networkId mismatch → refuse to connect

## D14 — Open product questions (TBD)
- Multiple accounts, the /1/i change chain, new receive address per tx, and gap limit: TBD (MVP = single address /0/0).
- BIP39 passphrase: TBD (not exposed in the UI; Core uses an empty passphrase).
- Mnemonic word counts: generation offers 12/24; import accepts 12/15/18/21/24.
- Re-displaying/exporting the mnemonic after creation: TBD (not implemented).
- Sending to P2SH/ECDSA addresses: TBD. The builder may accept them, but this is untested. PSKT is not supported.
- QR code: not implemented, TBD.
- UI language: English only; zh-CN TBD.
- Installer, code signing, and self-contained build: TBD (currently a framework-dependent zip).
- Max/sweep send, fee priority buckets (low/high), and custom fee: TBD.
- Wallet file ACL hardening (beyond the %APPDATA% user profile): TBD.

## D15 — Logging — DONE
The log goes to the local file `%LOCALAPPDATA%/KasNetWallet/logs/wallet-YYYYMMDD.log`. Every line passes through `SecretRedactor`, which masks:
- xprv/kprv/ktrv/tprv strings
- hex runs of ≥64 characters (this includes tx ids)
- runs of ≥12 BIP39 words

Exceptions are logged only as type + message, never `ToString()`. There is no telemetry, no crash upload, and no auto-update.

## D16 — MVP-1 tester report fixes (2026-10-31)
- **KDW-04:** a new wallet is written only after `BackupChallenge` (3 CSPRNG-chosen positions) is verified for the *same* phrase (`WalletManager.CreateNew`). Import skips this, because the user already owns the phrase.
- **KDW-05:**
  - `WalletManager.Unlock` throws only user-presentable types: `ArgumentException`, `UnlockThrottledException`, `WrongPasswordException`, `KeystoreCorruptException`.
  - `WalletKeystore.Load` converts every malformed-content error into `KeystoreCorruptException`.
  - `Address` and `TryLoadKeystore` never throw.
  - WinForms has global ThreadException, UnhandledException, and UnobservedTaskException handlers that log a redacted type and message only.
  - A corrupt settings.json falls back to the defaults (testnet-10, mainnet off).
- **KDW-09:** the failed-unlock counter persists in `unlock-state.<network>.json` (0600). An unreadable state file fails safe to a short wait. Clock skew is capped to the current delay.
- **KDW-11:**
  - Change below NKaspa's minimum change is detected as `FoldedChangeSompi`: when change = 0, it is fee minus max(min relay fee, rate × mass). It is shown as a warning with the amount, and the UI asks a second time.
  - Fee cap = max(`MaxFeeFloorSompi` = 0.01 KAS, `MaxFeePercent` = 1% × amount). Both are configurable in settings.json, and a fee above the cap is rejected.
  - Trade-off: with the default cap, a folded change of ~0.1 KAS on small sends is rejected. The user can adjust the amount, send the whole balance, or raise the cap.
- **KDW-08:** KDF caps from file are m ≤ 1 GiB, t ≤ 10, p ≤ 16, and the file size must be ≤ 64 KiB.
- **KDW-06:** keystore and unlock-state files are created 0600 on Linux/macOS. Windows inherits the %APPDATA% ACL.
- **KDW-03 / KDW-02:** `EnableUnsafeBinaryFormatterSerialization=false`, `PathMap` and `Deterministic` are set in Directory.Build.props.
- **KDW-10:** the redactor also treats `, - . " [ ] ( ) : ; / |`, digits and numbering as separators. These structured runs are masked from 8 words, while plain space runs still need 12. Mixed-case base64 runs of 43 or more characters are masked; bech32 addresses are lowercase, so they stay readable.
- **Submit timeout:** reported as `TransferStatusUnknownException` ("status unknown, refresh"), and the tx is still tracked.

## D17 — Retest round 2 (kdw-mvp1-retest N1–N7, 2026-10-31) — supersedes the D16 fee cap
- Fixed-amount fee cap removed. Fee sanity = rate check (fee ÷ fee mass ≤ 10 × max(node estimate, 100)) plus an absolute "high fee" threshold; both are **warnings** that need explicit acknowledgement (`warningsAcknowledged` on broadcast), never hard rejects.
- Dust change: retry with one extra UTXO (smallest first, up to 64 tries) via `MustSpend`; fold only if nothing works (warning + ack).
- NKaspa R11: folded change ≥ `MinimumChange` (detected as fee − expected fee) is a hard block.
- Send max: `SpendAllCoins` + `SendFees(fee)` iterated until fee == expected fee; largest-first UTXO subset when over the mass cap.
- Unlock throttle: corrupt → reset; future timestamp → clamp (with UI notice). Security trade-off: an attacker with file write access could reset the counter, but such an attacker can already copy the keystore offline, so the throttle is only a UI speed bump.
- Settings are range-validated on load (`WalletSettings.Normalize`).


## D18. Retest-2 (round 3) decisions

- **R3-5 fee jump at 100 → 101 sompi/gram:** not a bug in the fee arithmetic. *Minimum relay fee* mode pays the relay minimum, which is computed from compute mass only. *Node estimate* mode pays rate × fee mass, and fee mass includes KIP-9 storage mass, which grows roughly as 10^12 / output value. Small outputs therefore cost much more in that mode. The wallet keeps both modes but now warns **"Abnormally high fee relative to the amount"** when fee ≥ `FeePolicy.AbnormalFeeToAmountRatio` (default 0.5) × amount. When storage mass is the cause, the warning says so. The user must acknowledge the warning before broadcasting.
- **R3-7 Send max over the mass cap:** coins are sorted largest-first. A binary search, plus a short probe for storage-mass non-monotonicity, finds the largest count that still builds within the cap, so the mass limit is filled exactly. If not all UTXOs fit, an acknowledgement-required warning names the UTXOs and KAS left behind and tells the user to consolidate first (Send max to their own address).
- **R3-1 unlock state not writable:** fail closed. `UnlockThrottle.Save` returns false. The manager then applies the strictest delay (Failures = 1000 → 30 s), keeps the state in memory for the process lifetime, and shows a notice.
- **R3-2 trade-off (documented, not "fixed"):** anyone who can delete, corrupt, or roll back the state file resets the counter. The throttle is only a speed bump for someone at the keyboard. The real protection against password guessing is the Argon2id cost of the keystore, because an attacker with file access can copy the keystore and attack it offline anyway.
- **R3-3:** state file reads are capped at 4 KB. A larger file counts as corrupt and is reset.
- **R3-4:** when a mass error occurs for an amount below about 2 × 10^12 / cap sompi (0.2 KAS at the 100,000 cap), the error says "Amount too small … storage mass".
- **R3-6:** settings accept the network as a string (any case, `-`/`_` ignored) or a number, and write it as a string. An unknown value (e.g. "Testnet11") resets only that field, with a notice. Unknown `MassPolicies` keys are dropped.
- **R3-8 redaction:**
  - 64-hex strings (txids, block hashes) stay visible unless a secret label (private/priv/secret/seed/mnemonic/entropy/key/sk/prv/signing) appears within the 32 characters before them. Hex of 128+ characters is always masked.
  - Base64 and base64url runs are masked only at key sizes (43/44 or 86–88 characters with a digit or mixed case), or when mixed-case and 120+ characters long. Request ids and GUIDs stay visible.
  - Mnemonic detection now also covers whitespace gaps of any width up to 400 characters, HTML lists, numbered lists of 6 or more words, and words joined by "and/then/or/plus/followed by".

## D19. NKaspa 5c49251 adoption (0.1.10-alpha)

- **Pin:** `Directory.Build.props` pins `5c49251dfc2e1e5396344f802514bc6ee5e828fa`. The local clone was fetched and checked out; the NKaspa source is unmodified.
- **Mass policy:** `MassPolicy.ToccataActive` is now `bool?`. `null` is the new default ("auto"): it uses `Network.MassPolicyAt(virtualDaaScore)`, with the DAA score fetched from the node before every build. Without a DAA score:
  - testnet uses post-Toccata rules (testnet-10 has activated);
  - mainnet uses a conservative mix: the pre-Toccata 100,000 mass cap with a 100 sompi/gram relay rate.
  `true` and `false` force the NKaspa `PostToccata` and `PreToccata` policies. The `MaxTransactionMass` cap still applies on top.
- **Fee floor:**
  - The minimum relay rate comes from the policy: 100 sompi/gram post-Toccata, **1** pre-Toccata (NKaspa's value).
  - The expected fee is RelayMass × rate (Minimum relay mode) or FeeMass × rate (Node estimate mode).
  - The wallet still independently recomputes the expected fee and checks mass, outputs and signatures (`Verify`).
- **Change folding:** NKaspa now refuses to burn large change. The wallet:
  - uses NKaspa's `FoldedChange` together with its own fee − expected check (whichever is larger);
  - on NKaspa's refusal, first tries adding one input, then blocks with a clear message ("Amount too small … storage mass" for small amounts);
  - still blocks folds ≥ MinimumChange and needs acknowledgement for smaller ones.
- **Mnemonics:**
  - `MnemonicService.Normalize` calls `Mnemonic.Parse`, which enforces the checksum, and keeps its own word-count, word-list and checksum checks.
  - Zero-width characters are still **rejected**, not stripped. Silently accepting an altered phrase is worse than asking the user to retype it.
  - `Generate` uses `ToPhrase()`.
- **wRPC:** `KaspaWebSocketOptions` sets an explicit 16 MiB frame cap and uses the wallet RPC timeout for connect and requests. AutoReconnect stays off: the wallet's own reconnect re-checks the network id after reconnecting.
- **JSON:** the wallet never serializes NKaspa key types. NKaspa's default serializer now throws on them, which adds a second layer of protection.

## D20. Product rename: KaspaDesktopWallet / KaspaWallet → KasNetWallet

- **What changed:** solution `KasNetWallet.sln`; projects and namespaces `KasNetWallet.Core`, `KasNetWallet.WinForms`, `KasNetWallet.Core.Tests` (moved with `git mv`, history kept); window titles, assembly names, README, docs and NOTICE now say "KasNet Wallet". NKaspa is untouched.
- **Wallet file format:** unchanged. Keystore, settings and history JSON are byte-compatible; no AAD or version field contains the product name.
- **Data folder:** the default is now `%APPDATA%\KasNetWallet`. On first start, if that folder does not exist and the old `%APPDATA%\KaspaDesktopWallet` does, `WalletSettings.MigrateLegacyDirectory` copies all files into `KasNetWallet.migrating` and then renames it atomically to `KasNetWallet`. The old folder is **left in place** as a backup (never deleted). If the copy fails (I/O or permissions), the app keeps using the old folder so existing wallets still open.
- **Log folder:** new logs go to `%LOCALAPPDATA%\KasNetWallet\logs`; old logs are not migrated (they hold no wallet state).

## D21. Retest 0c7e556 fixes (F1–F5)

- **F1:** `ResilientWalletNode.IsDeadConnection` — an `InvalidOperationException` whose message mentions WebSocket plus "exceeds the maximum", "not connected" or "closed" is transient (drop and reconnect). Other `InvalidOperationException`s are not retried.
- **F2:** the extra-input retry now runs for any folded change (not only below `MinimumChange`). The block message describes the real cause (change too small for its own output, too large to burn).
- **F3:** `FoldBlockThreshold = MinimumChange − MinimumChange/20`. Folds at or above it, and NKaspa fold-cap refusals, give one block message; this removes the oscillation between BLOCK-nofit / FOLD+WARN / BLOCK-burn. Trade-off: change in [0.95, 1.0) × MinimumChange that used to fold with a warning is now blocked (conservative).
- **F4:** `WalletManager.EnsureNodeFresh` blocks building when the node is unsynced, the DAA score is 0, below the network's Toccata activation (both networks activated), or lower than the highest score seen this session. Status read failure also blocks (previously fell back to the default policy).
- **F5:** the wallet keeps rejecting format (Cf) characters, now with an explicit message naming the word and code point; NKaspa strips them. Documented in FOR-TESTERS §11d.

## D22 — Retest 5747f6b fixes (G1, G2/F3, G3, G5; G4 unchanged)

- **G1 old data folder.** `LegacyData` checks `%APPDATA%\KaspaDesktopWallet` at startup (`LegacyDataPrompt`).
  - If the new folder already exists and the old folder has `wallet.*.json` files the new one lacks, the user is told and offered **Import**. Import copies the wallet, history and unlock-state files and never overwrites anything.
  - After a migration or import, the user is told that the old folder still holds an **encrypted copy**, and that changing the password leaves that copy on the old password. The user is offered a delete, which needs two Yes clicks; No is the default on both prompts.
  - `DeleteLegacy` throws unless `userConfirmed`. It also refuses if any old wallet is missing from the new folder.
- **G2/F3 one fold amount.** Fold = actual fee − expected fee, used for every decision. NKaspa's reported fold is no longer used. Fold, block and normal change now follow the change amount in one order, both before and after Toccata.
  - A block throws `ChangeBlockedException`. It carries a **verified** lower amount: the suggestion is built for real and gives a normal change output.
  - The UI offers three choices: use the lower amount; add the remainder to the payment (Send max on exactly those inputs, so there is no change output); or Cancel / Send max.
- **G3 DAA check.** A node may report a DAA score up to `DaaRegressionTolerance` = 600 (about 1 minute at 10 BPS) below the highest seen before it is blocked. The highest seen resets to 0 on connect or `UseNode` (node change).
- **G5 read-only files.** Migration and import copy through `CopyWritable`. It clears ReadOnly; on Unix it adds user read/write, so the throttle and history files can still be written.
- **G4** is not changed (per instruction).
- Tests: `Retest5747f6bTests` (8 tests). Earlier tests now use `ThrowsAny<TransferException>`, because block is now the subclass `ChangeBlockedException`. The F4 regression test now uses tolerance + 1.

## D23 — Retest da66c4d fixes (S5, S6, N-G2, D-btn, S7 and follow-ups, docs)

- **S5: compare by content.** Wallets are compared by SHA-256 of their content, not by file name.
  - `DeleteLegacy` refuses unless every old `wallet.*.json` has a byte-identical copy (under any name) in the new folder. The error names the missing file(s).
  - Import never overwrites. A same-name wallet with different content is imported as `wallet.<net>.legacy-<sha8>.json`, and the user is told how to switch to it by renaming. History and unlock-state are copied only when the wallet keeps its name.
- **S6.** The "old folder found" prompt appears whenever an old wallet has no identical copy in the new folder, including when tn10 is the only wallet.
- **N-G2: NodeEstimate fixed, not just a warning.** NodeEstimate now uses the same fee rule as MinimumRelay: relay mass × rate, applied through NKaspa's `MinimumRelayFeeRate` + `SendMinimumRelayFee`. The only difference is the higher node rate. Previously it was fee mass × rate, with storage mass included.
  - Expected fee is relay mass × rate in both modes. So fold = actual − expected, the monotonic fold → block → change order, and the verified suggested amount all carry over.
  - The node accepts this because its relay rule charges compute/transient mass. Storage mass is still capped by the mass limit, which the wallet checks.
  - Effect: "Fee estimate did not converge" no longer occurs. The ~0.2 KAS blocked band shrinks to the same narrow band as default mode, and fees for small change are no longer inflated.
  - Tests R3-5 and N4, which pinned the old storage-mass fee jump, were updated to the new behaviour.
- **D-btn.** Cancel is the default (Enter) button in both dialog forms: Yes/No/Cancel → Button3, OK/Cancel → Button2 (`ChangeBlockedException.CancelButtonNumber`).
- **Follow-ups**
  - **Delete reminder.** A declined delete is recorded in `legacy-reminder.json` in the new folder. The offer is repeated once on a later start and then never again (`MaxDeleteOffers` = 2). This is not part of the wallet file format.
  - **Settings.** Import brings `settings.json`. It is copied if the new folder has none; otherwise each old value (e.g. `RpcTimeoutSeconds`) is taken only where the new value is still the default.
  - **S7.** `DeleteLegacy` and `Import` refuse when the old and new paths resolve to the same folder.
- **Docs.** Claims about a third-party wallet UI origin were removed from NOTICE, README, Theme.cs and D1. No such file was ever copied, and that project is not a dependency. NOTICE now has the correct NKaspa pin, 5c49251.
- Tests: `RetestDa66c4dTests` (17 tests, counting theory cases).

## D24 — Retest 570d403 fixes (N1, N3, N2, settings merge, mainnet notice, known issues)

- **N1: deleting the old folder is selective, never recursive.** `LegacyData.PlanDelete` decides per top-level file:
  - deleted: `wallet.*.json` and `history.*.json` with a byte-identical (SHA-256) copy anywhere in the new folder; `unlock-state.*.json`, `settings.json` (already merged) and `legacy-reminder.json`;
  - kept: every sub-folder (e.g. `backup/`), `*.bak`, logs, unknown files, and a history file whose new copy has since diverged.
  - The kept items are listed in the delete offer, the confirmation and the result message. The old folder itself is removed only when it ends up empty (non-recursive delete).
  - The existing guard stays: nothing is deleted unless every old wallet has an identical copy in the new folder.
- **N3.** A wallet imported under a new name (`wallet.<net>.legacy-<sha8>.json`) brings its history as `history.<net>.legacy-<sha8>.json`, so renaming both files back makes the app use them together. The hint now says to rename the history file too. Unlock-state is not copied for a renamed wallet (it starts fresh).
- **N2: fixed.** Pre-Toccata (MinimumChange 10,000,000), the fold/block boundary is now the remainder (inputs − amount) ≥ 9,500,000, which does not depend on the fee rate. Before, it was the folded amount (remainder − fee), and NKaspa's fee-dependent fold-cap refusals fell between folds, so at rate ≥500 the order was FOLD → BLOCK → FOLD → BLOCK. The suggestion search adds 60 coarse (5×) steps after the 60 fine ones, so blocks whose minimum change came from the fallback estimate also get a suggestion. Post-Toccata is unchanged.
- **Settings merge: only absent keys.** A key present in the new `settings.json` was saved by this version and counts as user-set, even if it equals the default. Only keys missing from the new file are taken from the old one; if the new file is missing, the old one is copied. Consequence: the app saves the whole settings file when a wallet is opened, so after that first save there is nothing left to merge.
- **Mainnet notice.** If the old settings use mainnet and the new settings have no `Network` key, the import prompt says the app will start on MAINNET.
- **Known issue (not fixed): node estimate does not charge storage mass.** NodeEstimate charges relay mass × node rate (D23). Storage mass is only checked against the mass limit, not paid for. If a node starts requiring fees for storage mass, transactions with small outputs may be rejected.
- Tests: `Retest570d403Tests` (17 tests, counting theory cases). `Import_MergesSettings_KeepsUserChangedNewValues` was updated to the absent-key rule.

## D25 — NKaspa pin 5c49251 → dea2697 (0.1.10-alpha)

- **Pin.** `Directory.Build.props` `NKaspaPinnedCommit` and NOTICE now name `dea269794937328b1c93a7985ff88b9e9f462a09` (origin/main). NKaspa itself is unmodified; the clone is only checked out at that commit. See NKaspa CHANGELOG 0.1.10-alpha.
- **API: `ChangeFolded` / `FoldedChange` → `TransactionBuilder.ChangeFoldedIntoFee`.** The two properties were merged into one `Money` (zero = nothing folded). `TransferService` reads `ChangeFoldedIntoFee.Sompi` for the diagnostic `NkFolded` field. Fold/block decisions are unchanged: they still use the wallet's own measure, actual fee − expected fee (D22/G2).
- **1 sompi/gram pre-Toccata relay rate is never used.** dea2697 restored 1 sompi/gram for `MassPolicy.PreToccata` (rusty-kaspa `90dbf074` `LEGACY_MINIMUM_RELAY_TRANSACTION_FEE`), which current post-Toccata nodes may reject. `MassPolicy.Resolve` now:
  - with a known DAA at or after the network's Toccata activation, always returns `PostToccata`, even when `ToccataActive = false` is forced in settings;
  - otherwise (forced pre-Toccata, DAA below activation, or mainnet without a DAA) uses the pre-Toccata 100,000 mass cap with the post-Toccata 100 sompi/gram rate;
  - so the relay rate is never below 100. The earlier check covered only the mainnet-without-DAA case; forced pre-Toccata and a low DAA gave 1 sompi/gram, and that is now fixed.
  - The switch is `Network.ToccataActivationDaaScore` (testnet-10 467,579,632, mainnet 474,165,565). `Network.TestnetZkHardeningActivationDaaScore` (476,232,000) is a different checkpoint and is deliberately not used as the switch.
  - `WalletManager` still passes the node's virtual DAA score (`WithDaaScore`), so live tn10 resolves to `PostToccata`.
- **Tests.** `NKaspaDea2697Tests` (13 tests, counting theory cases). Two `NKaspa5c49251Tests` assertions and the pre-Toccata `G2_FoldBlockChange_IsMonotonic` case (base fee 2,036 → 203,600) were updated to the 100 sompi/gram rule.
- The mainnet warning now names NKaspa 0.1.10-alpha.
