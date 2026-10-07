# TanukiBCL v3.2.13 compatibility checkpoint

Target: released [TanukiBCL v3.2.13](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.13), commit `7558dd9adbde713ed333fb4f3acdee9fcd7879e3`. This checkpoint covers changes since [v3.2.12](compatibility-3.2.12.md). The .NET port is not yet proven fully compatible; older open items remain in the [3.2.8 checklist](compatibility-3.2.8.md).

| v3.2.13 change | .NET port |
| --- | --- |
| Normal edition: SNR custom PNGs come only from the selected game's `SuperNewRolesNext/CustomCosmetics`; they are shown without waiting for external hat definitions. | Custom SNR layers now resolve exclusively to local files. Remote definitions may still supply optional adaptive/visor layout metadata, but cannot supply the image. While catalogs are loading, local layers render immediately; a later update can add metadata or vanilla catalog layers. |
| Normal edition: NoS lobby equipment appears before round PlayerData. | The lobby uses current appearance Skin/Hat/Visor IDs, NoS lobby RGB (or the appearance palette color), and the existing LoadedContents renderer. Equipment changes and unequipping invalidate the avatar key. Main and overlay views pass game state to their avatars. |
| Lite edition: remove all costume rendering. | Not applicable: TanukiBCL.Net currently has one WPF client corresponding to the normal edition, not a Lite distribution. |
| Version 3.2.13 | Development assembly version is `3.2.13-netdev.0`; the published .NET beta remains `3.2.12-net-beta.1` until separately packaged and released. |

Verification on 2026-10-07: Release solution build had zero warnings/errors. `--cosmetics-self-test` passed local-only SNR rendering before catalog completion, local visor metadata, NoS lobby Skin/Hat/Visor rendering without round data, dynamic color conversion, unequipping, and prior costume tests. `--snr-cosmetics-live-test` resolved 216 local hat images from the installed SNR game directory. No live Among Us SNR or NoS lobby was launched for this checkpoint, and a side-by-side visual comparison with the Electron v3.2.13 build remains outstanding.
