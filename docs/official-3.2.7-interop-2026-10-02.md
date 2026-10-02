# Official TanukiBCL v3.2.7 interoperability checkpoint (2026-10-02)

The official `v3.2.7` Windows release was downloaded, checked against the SHA-512 in `latest.yml`, extracted without running the installer, and launched with an isolated user-data directory. It joined the existing Airship lobby `ETQYMR` as Among Us client `3937`. The .NET probe joined as client `3938`.

Run the focused test with `--tanuki-interop-test --game-process-id 60232 --expected-peer-client-id 3937 --seconds 30`. The expected-client filter is important: an earlier unfiltered test passed by connecting to a different vDEV client. The focused test now passes against the official release: WebRTC reached `connected`, the data channel exchanged a NoS radio payload, 30 Opus test-tone frames were sent, and PCM frames were received from that same client. This verifies transport and audio frames, not yet subjective audible quality in the official GUI.

The initial failure was a glare race: both peers offered concurrently and an already superseded connection could still receive data while the active connection could not send audio. The .NET side now follows the upstream offer tie-break more closely, does not create a peer from an orphan candidate or answer, and treats received data as evidence that the channel is usable even when SIPSorcery has not fired `onopen`. ICE uses `relay` only when the voice server explicitly requests it, matching upstream 3.2.7.

The two-client .NET self-test also passes after these changes. Remaining work includes audible bidirectional testing, reconnection under churn, the rest of the 3.2.7 game/audio policies, GUI parity, and packaging. This checkpoint is not a compatibility-complete claim.

The .NET Audio settings now expose the 3.2.7 microphone-sensitivity slider and enable switch. The selected threshold is applied to captured audio, with VAD hysteresis and silent output while below the threshold. This currently uses a PCM RMS approximation of the upstream frequency-band analyser, so its exact activation boundary still requires a more faithful port and live comparison.
