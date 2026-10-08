# TanukiBCL v3.2.17 compatibility checkpoint

Target: [TanukiBCL v3.2.17](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.17), commit `a7d07de5ef6af73227274125943aa0c453c7fe89`. This checkpoint covers the changes after [v3.2.16](compatibility-3.2.16.md). The corresponding .NET prerelease is `3.2.17-net-beta.1`.

| Upstream change | .NET/WPF implementation | Verification |
| --- | --- | --- |
| Voice footer stays at the bottom for every player count | Removed the 7+ player hide condition; reserved the bottom Grid row while the roster scrolls above it | 20-player narrow-window self-test |
| Player grid responds to available width | Recalculate 1–12 columns and capped avatar size when the roster viewport changes | Narrow/wide layout self-test |
| Inquiry moves into Settings | Added the Inquiry category and embedded form; removed the separate window and footer launcher | Settings and inquiry self-tests |
| Inquiry submit and optional support-log attachment | Existing `InquirySubmission` path retained | Payload self-test without external transmission |

The upstream 3.2.17 diff changes UI and package version only; it does not change voice signaling or game-memory protocol. The full .NET C# tree now passes a three-level control-nesting check (135 source files, no exceptions). Build, WPF self-tests, audio policy/processing, NoS snapshot and loopback server-reconnect tests passed. Manual visual comparison of the resizable player grid and the embedded inquiry page, and live interop with official 3.2.17, remain beta feedback items.

## Manual beta follow-up (2026-10-08)

The installed official 3.2.17 and .NET beta.1 were connected to the same NoS local lobby (`46282`) on separate Among Us processes. With two, three, and four game participants, both compact views showed the peer row and bottom footer without clipping or overlap. The official view reported a good voice connection to the .NET peer, and both clients displayed a connected indicator. This verifies signaling/display, not audible two-way voice: the tester could not speak during this run. The official client also intermittently displayed `Failed to resolve TBCLFields.nextIndex address` for NoS, so in-game distance/role behavior was not verified.

At the normal settings size, the embedded inquiry pages were usable in both clients. Maximized, the official form remained about 720 px wide and centered while beta.1 stretched across the content area. The WPF form now limits its container to 752 px (including 16 px side margins) and centers it; layout tests cover 500 px and 1800 px widths, and a 500 px render preview showed no clipping. The changed build has not yet been compared side by side in the live Settings window. The three extra game processes and both voice clients were closed after testing, leaving the pre-existing one game process running.
