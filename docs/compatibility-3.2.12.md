# TanukiBCL v3.2.12 compatibility checkpoint

Target: released [`v3.2.12`](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.12), commit `a6bfd966525ed971c69ca5d6c8ca163f5c4d7f40`. The only upstream change since [3.2.9](compatibility-3.2.9.md) is that commit; the `v3.2.11` tag is an older September build whose SNR diagnostics were already ported. The .NET port is **not yet fully compatible**; the [3.2.8 checklist](compatibility-3.2.8.md) still lists the open items carried forward.

## Ported from 3.2.12

| 3.2.12 change | .NET port |
| --- | --- |
| Radio voices no longer leak to nearby crew | Already the .NET behavior since the 3.2.7 port: during Tasks a peer on radio is muted (`radio-private`) for anyone who cannot receive that channel, and a sender only transmits radio audio to eligible peers. Added regression checks for nearby crewmates and impostor/SNR radio. |
| Ghosts hear jackal radio | `SpatialVoicePolicy.CanHearJackalRadioAsGhost`: a dead listener hears a living non-impostor on radio when Jackal radio is enabled and impostor-radio-only mode is off (upstream `ghostReceivingRadio`). The sender filter in `VoiceServerProbe.CanReceiveRadioAudio` now includes these ghosts for SNR and NoS jackal channels. |
| Hat/Visor clipped to the avatar circle | `PlayerAvatar` always clips the back and front cosmetic layers with the same ellipse as the speech border, in the main window and the overlay. The overlay `clipEquipment` option (upstream `overflow`) is removed. |
| NoS looks in the lobby | `AmongUsMemoryReaderService` reads the NoS snapshot in the lobby as well, with the upstream session key `<code>:<round>:lobby|game`. The round still advances only when a game starts, and the read-failure badge still applies only during Tasks/Discussion. |
| Version 3.2.12 | The development version is `3.2.12-netdev.0`, so peers receive `3.2.12` in the app-version exchange. |

## Verification on 2026-10-07

- `dotnet build TanukiBCL.Net.sln -c Release`: zero warnings and errors.
- Probe self-tests: policy (new 3.2.12 radio cases), NoS snapshot, audio processing.
- Client self-tests: cosmetics, NoS avatar, voice view, overlay (always-clipped cosmetics), settings, update catalog (`3.2.12` above `3.2.9`), session lifecycle and settings application.
- Not verified live: NoS costumes in a real lobby (the running game was in Tasks), ghost reception of jackal radio with an official 3.2.12 peer, and side-by-side avatar clipping against the Electron build.
