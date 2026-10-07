# TanukiBCL v3.2.14 compatibility checkpoint

Target: [TanukiBCL v3.2.14](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.14), commit `1d7002a`. This checkpoint covers changes since [v3.2.13](compatibility-3.2.13.md). The .NET port is still a beta and full interoperability is not yet proven.

| v3.2.14 change | .NET port |
| --- | --- |
| Send processed NoS Skin, Hat, Visor, HatBack and BodyMask PNGs to Web clients. | Renders local NoS layers with the existing WPF renderer, PNG-encodes them, hashes their bytes, and sends `nosCosmeticAssets` in the upstream `gameState` signal protocol. The ordinary player state contains only `nos-web://<hash>` IDs. |
| Initial/equipment-change transfer, missing-image retry and late Web join. | An 8 MiB bounded image cache sends at most one active PNG per 100 ms. Missing IDs in `mobilePlayerInfo.nosCosmeticIds` resend selectively; legacy Web detection requests a full resend. Lobby/game installation changes reset the cache. |
| Lite edition remains without cosmetics. | Not applicable: the .NET client has no Lite edition. |

Release build and the mobile-host/cosmetics self-tests passed. A synthetic NoS costume fixture verified all layers, deduplication and targeted retry. The actual Web image display has not been verified: [public Web v3.6 does not support receiving and showing these assets](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.14). NoS game-side live play remains a beta feedback item.
