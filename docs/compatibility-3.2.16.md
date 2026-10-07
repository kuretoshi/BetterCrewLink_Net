# TanukiBCL v3.2.16 compatibility checkpoint

Target: [TanukiBCL v3.2.16](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.16), commit `2643149`. This covers the behavior changes in v3.2.15 and v3.2.16 after [v3.2.14](compatibility-3.2.14.md). The .NET port remains a beta.

| Upstream behavior | .NET implementation and verification |
| --- | --- |
| NoS impostor and Jackal radio use the sender's `RadioData` mask. Missing mask does not fall back to nearby/ghost/role hearing. | `NosRadioRules`, outgoing recipient filter, `SpatialVoicePolicy`, and the radio indicator all use the selected channel and listener PlayerId bit. Radio status now reaches every peer, including those not permitted to receive audio, so a pending mask stays private. Mask/privacy policy self-tests pass. |
| NoS F/G separate configurable channels; last pressed wins, release restores the other. | F and G are separate shortcuts. The key monitor reports key down/up, radio keys also activate push-to-talk while held, and `HeldNosRadio` preserves press order. NoS settings show the Jackal row only for NoS. The control status includes `nosRadioKind`; a legacy sender with two channels and no kind fails closed. |
| Stop on death, state change, or disabled channel. | Game-state and lobby-setting updates stop invalid transmission. Existing release button remains available for mouse control. |
| Refactor reader, ZIP lookup, camera audio; add regression and nesting checks. | The .NET game readers and camera geometry already have separate components. The NoS ZIP image index now scans a category only once per archive. NoS snapshot, game-scan, cosmetics, settings, and audio-policy regressions pass. `tools/NestingCheck` parses C# with Roslyn: changed/new methods must be at most three control levels unless they existed deeper in the 3.2.14 beta, in which case depth must not increase. The .NET codebase is not yet globally at the upstream three-level limit. |

Not yet verified: live .NET ↔ official 3.2.16 NoS dual-channel communication, and Web-side separated-channel support. Upstream notes that both PC endpoints must use 3.2.16 for channel separation and Web support was unpublished at its release. The corresponding .NET beta is for feedback, not a claim of complete interoperability.
