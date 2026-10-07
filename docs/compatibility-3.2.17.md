# TanukiBCL v3.2.17 compatibility checkpoint

Target: [TanukiBCL v3.2.17](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.17), commit `a7d07de5ef6af73227274125943aa0c453c7fe89`. This checkpoint covers the changes after [v3.2.16](compatibility-3.2.16.md). The corresponding .NET prerelease is `3.2.17-net-beta.1`.

| Upstream change | .NET/WPF implementation | Verification |
| --- | --- | --- |
| Voice footer stays at the bottom for every player count | Removed the 7+ player hide condition; reserved the bottom Grid row while the roster scrolls above it | 20-player narrow-window self-test |
| Player grid responds to available width | Recalculate 1–12 columns and capped avatar size when the roster viewport changes | Narrow/wide layout self-test |
| Inquiry moves into Settings | Added the Inquiry category and embedded form; removed the separate window and footer launcher | Settings and inquiry self-tests |
| Inquiry submit and optional support-log attachment | Existing `InquirySubmission` path retained | Payload self-test without external transmission |

The upstream 3.2.17 diff changes UI and package version only; it does not change voice signaling or game-memory protocol. The full .NET C# tree now passes a three-level control-nesting check (135 source files, no exceptions). Build, WPF self-tests, audio policy/processing, NoS snapshot and loopback server-reconnect tests passed. Manual visual comparison of the resizable player grid and the embedded inquiry page, and live interop with official 3.2.17, remain beta feedback items.
