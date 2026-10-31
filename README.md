# KasNet Wallet (MVP, alpha)

A Windows (WinForms, .NET 8) desktop wallet for Kaspa. It is a UI plus a thin Core layer over [NKaspa](https://github.com/lifeme2027/NKaspa).

> Alpha software. It is not audited and comes with no warranty. It makes no promise about the safety of funds or any other outcome. Try it on **testnet-10** first. You are responsible for backing up your recovery phrase.

## Scope boundary
- **This repo:** WinForms UI plus `KasNetWallet.Core`, a thin wallet layer (keystore, account, transfer flow, node wrapper). All Kaspa primitives come from NKaspa: BIP39/BIP32, addresses, tx building, sighash, Schnorr signing, mass/fees, and wRPC.
- **NKaspa:** the protocol library. It is consumed as the NuGet package `NKaspa` 0.1.10-alpha from nuget.org and never forked or modified here. Gaps are written up in `docs/NKASPA-REQUESTS.md`.
- **kaswallet (.NET port):** the CLI reference implementation. It was used only to check behavior: its independent derivation of the `abandon…about` vector matches NKaspa's output, which is pinned in the tests. Its code is not duplicated here, and this repo is not a replacement for kaspadotnet/kaswallet.

## Structure
```
KasNetWallet.sln
Directory.Build.props            # shared build settings (PathMap, no BinaryFormatter)
src/KasNetWallet.Core/            # net8.0 class library
  MnemonicService.cs             # generate/validate BIP39 (checksum enforced)
  HdAccount.cs                   # m/44'/111111'/0'/0/0 key + address
  Keystore/                      # AES-256-GCM + Argon2id encrypted keystore
  Rpc/                           # IWalletNode + wRPC (NKaspa KaspaWebSocketChannel/KaspaWrpcJsonClient)
  Transfer/                      # build/sign/verify, confirm-gated broadcast, KAS formatting
  WalletManager.cs               # facade used by the UI
  WalletNetwork.cs, WalletSettings.cs
src/KasNetWallet.WinForms/        # net8.0-windows UI (clean-room)
  StartForm, MnemonicForm, PasswordForm, MainForm, Theme
tests/KasNetWallet.Core.Tests/    # xUnit, fully offline
docs/DECISIONS.md, docs/NKASPA-REQUESTS.md, docs/patches/
```

## Implementation notes
- **Create:** generates 24 BIP39 words, shown once, and requires a backup acknowledgement. **Import:** accepts 12/24 words and validates the checksum.
- **Keystore:** the mnemonic is encrypted with AES-256-GCM. The key comes from Argon2id (64 MiB, t=3, p=4), and network, address and KDF params are bound as AAD. A wrong password or tampering fails authentication. The keystore is stored at `%APPDATA%\KasNetWallet\wallet.<network>.json`.
- **Unlock/Lock:** unlocking holds the derived private key in memory. Locking disposes it.
- **Node:** wRPC JSON WebSocket. The node's `networkId` and sync state are checked before broadcast. The balance is the sum of UTXOs from `getUtxosByAddresses`.
- **Send:** parses the amount (max 8 decimals), validates the recipient against the wallet network, sorts inputs deterministically, and builds and signs with the NKaspa `TransactionBuilder` (Schnorr). It then checks the outputs, the balance of inputs vs outputs, and `Transaction.Verify`, and computes the fee and mass. A confirmation dialog (default **No**) shows from/to/amount/fee/change/tx id. Broadcast is refused unless confirmed, and each plan can be broadcast only once.
- **Networks:** mainnet and testnet-10, selected on the start screen together with the wRPC URL. There is one keystore per network.
- Mnemonics and private keys are never logged or written to disk in plaintext.

## Safety defaults
- The default network is **testnet-10**. Mainnet requires an explicit opt-in, and every mainnet send shows a separate warning that requires typing a phrase.
- Imports with a bad checksum, an unknown word, or a wrong word count are rejected.
- Secrets are wrapped in types that print `[REDACTED]` and refuse JSON serialization, and log lines are redacted.
- The tester guide is docs/FOR-TESTERS.md.

## Build & run
Prerequisites: .NET 8 SDK and access to nuget.org. NKaspa is restored as a NuGet package (`NKaspa` 0.1.10-alpha); no NKaspa clone or git is needed:
```
dotnet restore
dotnet test                                      # Core tests (Linux/macOS/Windows)
dotnet run --project src/KasNetWallet.WinForms    # Windows only
```
`EnableWindowsTargeting=true` lets the WinForms project build on Linux. It only runs on Windows.

## Third-party notices
- NKaspa 0.1.10-alpha: MIT, © NKaspa contributors (NuGet package, not vendored).
- Konscious.Security.Cryptography.Argon2: MIT (NuGet).
