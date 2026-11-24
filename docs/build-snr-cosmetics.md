# SNR local image decoder

The Windows .NET client includes a small Rust helper for local SNR UnityFS bundles.
Install the Rust stable MSVC toolchain in addition to the existing .NET SDK and
Windows C++ build tools. `dotnet build` and `dotnet publish` run
`tools/build-snr-cosmetics.ps1` and include the decoder under `RoleReaders`.

The helper reuses Cargo's normal registry/build caches. Set `CARGO_TARGET_DIR`
to an existing compatible build cache when appropriate. It retains one bounded
bundle index and a bounded PNG cache; the WPF client serializes requests, times
out stalled workers, and closes an idle worker. It reads only the selected
game directory and performs no game-process writes.

Run `cargo test --manifest-path tools/SnrCosmeticsReader/Cargo.toml --locked`,
`node tests/test-snr-native.mjs`, the client's `--cosmetics-self-test`, and the
voice probe's `--policy-self-test` and `--mobile-host-self-test` for local
regression checks. Native decoder notices are included under `Licenses/SNR`.
