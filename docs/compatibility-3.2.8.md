# TanukiBCL v3.2.8 compatibility checkpoint

Target: released [`v3.2.8`](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.8), commit `8d52d02beee8410e0c4dfa06f704c7a41f5fe962`. The .NET port is **not yet fully compatible**. The user-approved game scope is 64-bit Among Us only.

## Newly ported from 3.2.8

- The NoS `TbclSnapshotReader` source matches the released upstream file byte-for-byte. It resolves `nextIndex`/`Latest` and reads snapshots without writing `RequireUpdate` or requesting game-process write access. Only the `win-x64` helper is built and bundled.
- The .NET NoS snapshot and dynamic-palette readers now decode 64-bit pointers and 64-bit array headers, including addresses above 4 GiB. A 32-bit snapshot is explicitly rejected as unsupported.
- Process pickers and the game scanner exclude terminated Among Us processes with zero threads, corresponding to the new upstream process filter.
- The compact-view footer includes the new Ko-fi and X buttons and their released URLs.
- Localization assets and settings/window localization work from the earlier 3.2.7 port are included in the same working set; this is partial GUI parity, not a claim of completion.

## Verification on 2026-10-03

- `dotnet build TanukiBCL.Net.sln -c Release`: succeeded with zero warnings and errors.
- Synthetic high-address NoS palette and snapshot self-tests: passed, including torn-read handling.
- The x64 helper's `layout` and `palette` commands resolved the live NoS 3.5.3 game process (PID `21604`) with pointer size 8 and high memory addresses.
- The .NET `--nos-snapshot --game-process-id 21604` diagnostic read lobby `SWWIDN` in Tasks state: four players and one impostor radio. This verifies the .NET snapshot path against a live 64-bit game, not just helper output.
- A self-contained `win-x64` WPF publish succeeded, and its bundled `NoSReader/TbclSnapshotReader.exe layout 21604` returned pointer size 8 and the expected live structure offsets.
- The SNR role helper, its live role/Jumbo reader, and the packaged helper RID now use 64-bit object/array pointers. The synthetic high-address role/Jumbo/torn-read policy test and self-contained WPF publish pass. Running the packaged helper against a NoS-only live process reached the expected "no SNR player array" diagnostic, confirming attachment but **not** SNR role correctness in an SNR game.
- The TOH4E branch of the same helper and the TOH4E dictionary/active-killer reader now use 64-bit object and entry pointers. High-address synthetic role/Opportunist/IKiller/torn-read tests pass. The combined x64 WPF publish succeeds. No live TOH4E game was available to validate actual role addresses.
- The live game scanner excluded four zero-thread stale Among Us entries and read the four active 64-bit NoS processes (`21604`, `31532`, `41640`, `58288`) in the same `SWWIDN` Tasks lobby, with four consistent players. Separate NoS snapshot reads succeeded for all four PIDs; PID `21604` exposed the impostor radio. This establishes a concrete multi-process assignment for the next interoperability run, but does not prove voice signaling or audio.
- The local upstream checkout is at the released `v3.2.8` commit and its `dist/win-unpacked/TanukiBCL.exe` has file version `3.2.8.0`. Its independently launched copy initially displayed `v3.2.5`, but a later renderer observation showed `v3.2.8` and `app://bundle/index.html?view=app&version=3.2.8`; the settings window also identified `3.2.8`. The version transition has not been explained, but the subsequent interoperability test targeted a renderer identifying itself as 3.2.8.
- The packaged x64 WPF client attached to Among Us PID `31532` (client `29`) and displayed the same `SWWIDN` lobby and all four players as the official client on PID `21604` (client `26`). This verifies game selection and basic lobby/UI state; the WPF peer connection and live microphone audio still need their own direct proof.
- A separate `.NET` interop probe on game PID `41640` (client `28`) initially failed to connect to official client `26` within 35 seconds while the official app's saved **NATの修正** setting was ON and the probe's was OFF. After temporarily turning that official setting OFF and reloading its connection, the same probe connected to client `26`, exchanged lobby/NoS radio configuration, sent 30 Opus tone frames, and received Opus audio from client `26`. This is a live bidirectional protocol/audio-frame check against the official 3.2.8 renderer, not a human listening or full WPF-client audio check. The setting difference correlated with the failure; reloading also changed connection state, so causality is not yet isolated. The official setting was restored to its original ON value and its saved config was verified.
- With the official setting restored to ON, repeating the probe with `--nat-fix` also passed: ICE reached connected for the official peer, 30 Opus frames were sent, and Opus audio was received from client `26`. Thus both matched NAT modes have passed against the live official renderer. The mixed-mode timeout should still be investigated rather than assumed to be an unavoidable limitation.
- The compact WPF view now passes the measured Engine.IO server ping to connected peer quality indicators, matching the upstream sampler's server-ping fallback rather than leaving every remote bar unmeasured. The merge preserves future peer-specific RTT, jitter, and loss values. Release build and `--voice-view-self-test` pass. Peer-specific WebRTC measurements themselves are still unimplemented, so this is only a partial quality-indicator port.

## Remaining proof and work

- Run the packaged x64 WPF client against the official 3.2.8 client and verify live bidirectional speech, NoS radio/vent/meeting/ghost policies, process switching, localization, and GUI behavior.
- Finish the existing 3.2.7 compatibility checklist's incomplete feature, audio, settings, and visual parity items; the 3.2.8 additions do not make those complete.
- Test package contents and update behavior on a clean Windows x64 environment.
- Verify SNR role, modifier, ghost-role, Jumbo size, and secondary cosmetics against a live 64-bit SNR game; synthetic layout checks alone cannot establish this.
- Verify TOH4E role, active-killer, and Opportunist CanKill behavior in a live 64-bit TOH4E game.
