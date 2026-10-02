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

## Remaining proof and work

- Run the packaged x64 WPF client against the official 3.2.8 client and verify live bidirectional speech, NoS radio/vent/meeting/ghost policies, process switching, localization, and GUI behavior.
- Finish the existing 3.2.7 compatibility checklist's incomplete feature, audio, settings, and visual parity items; the 3.2.8 additions do not make those complete.
- Test package contents and update behavior on a clean Windows x64 environment.
- Verify SNR role, modifier, ghost-role, Jumbo size, and secondary cosmetics against a live 64-bit SNR game; synthetic layout checks alone cannot establish this.
