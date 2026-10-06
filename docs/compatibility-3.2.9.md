# TanukiBCL v3.2.9 compatibility checkpoint

Target: released [`v3.2.9`](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.9), commit `6f9a5a5516d14d284df30f8c7a80738efee0f215`. The .NET port is **not yet fully compatible**; the [3.2.8 checklist](compatibility-3.2.8.md) still lists the open items carried forward. The supported game scope remains 64-bit Among Us.

## Ported from 3.2.9

| 3.2.9 change | .NET port |
| --- | --- |
| NoS TBCLFields `20261005` Skin/Hat/Visor | `tools/TbclSnapshotReader/Program.cs` is the released source (win-x64 build). `NosSnapshotReader` accepts schemas `20260918`, `20260928` and `20261005`, requires all three costume layouts for `20261005`, and rejects names longer than the published capacity. |
| NoS read failure → automatic re-acquire | Five seconds of failed or stalled reads drop the layout and resolve it again. Helper failures, including an abnormal exit, retry after 5, 10, 20 and then 30 seconds instead of stopping for the game process. The status strings and failure reasons match `nosSnapshotTracker.ts`. |
| Dark red MOD badge with the reason | `AmongUsState.NosReadStatus` is set while NoS is loaded; it fails during Tasks/Discussion when no snapshot or a connected player's data is missing. The compact-view MOD badge and the game overlay watermark turn `#581e24` and show the message. |
| NoS cosmetics in avatars | `NosCosmeticContents` ports `nosContents.ts`/`nosAddonImages.ts`: it reads `BepInEx/MoreCosmic/LoadedContents.json` (version `20261005`), local PNGs under the game folder and `Addons/*.zip` `MoreCosmic/Contents.json` entries. It renders the top-left sprite frame, the extra layer, adaptive color and the hat mask on the released 300×375 canvas. `PlayerAvatar` uses the published names instead of vanilla IDs and masks the body with the hat mask. |
| App-version exchange | `AppVersionPolicy` ports `appVersion.ts`. Peers exchange `{"type":"app-version"}` every 3 seconds in a lobby. A .NET build advertises its compatibility release (`3.2.9` for `3.2.9-net-beta.1`). Only the older side relative to the host gets the update request; both sides see the list of players on another version. |
| Debug authentication | The packaged `debug-auth.json` may select the HTTPS invitation endpoint (`{"url": ...}`; the release default) or up to 16 local PBKDF2 records. Remote mode never falls back to local hashes. The dialog distinguishes denied and unavailable results with the released messages. `tools/create-debug-auth.ps1 -Add` keeps existing local passwords. |
| Debug window | One page instead of tabs: searchable player cards with per-player collapse kept across refreshes, true/false/未取得 chips, game judgement, MOD values, position/size, appearance including NoS/SNR costume IDs and per-player Radio/RadioData. SNR assigned/winner team and team tag come from the automatic helper discovery; the manual "SNR役職を取得" button is gone. NoS, SNR, game state, voice and log sections are collapsible. |
| SNR automatic retry | A failed initial SNR discovery retries after 10 seconds. |

Upstream's `server/debug-auth` service and registration page are server-side and are not part of the .NET client.

## Verification on 2026-10-06

- `dotnet build TanukiBCL.Net.sln`: zero warnings and errors.
- Probe self-tests: policy (including app-version), VAD, NoS snapshot (20261005 costumes, oversized name, incomplete layout), NoS palette, server reconnect, mobile host, audio processing, scalar read, loopback and Opus decoder.
- Client self-tests: settings (debug authentication, dialog messages, debug window), cosmetics (synthetic LoadedContents, addon ZIP, sprite frame, adaptive color, body mask, `PlayerAvatar` layers), voice view (MOD badge and version notice), overlay (NoS failure watermark), update catalog, update package and registration.
- Live NoS (Steam, PID `19192`, Tasks): the released helper reported schema `20261005` with Skin/Hat/Visor layouts. The .NET diagnostic read `skin_Science`, `noshat_Lucia_ぽんぽこたぬき` and `visor_mira_card_blue`. The live install's LoadedContents.json (1,096 hats, 453 visors, 5 addons) rendered the addon hat image.
- Not verified live: the version notice against an official 3.2.9 peer, the HTTPS invitation endpoint with a real password, and side-by-side avatar pixel parity.
