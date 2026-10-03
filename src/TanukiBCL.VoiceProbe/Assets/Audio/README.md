`reverb.48k.wav` is the released TanukiBCL ghost-haunting impulse response,
converted from `static/sounds/reverb.ogx` in the TanukiBCL source tree (asset
commit `b4999ea1435109ec83c6ce183c013ead738c7c65`, original SHA-256
`a0ff83aafc1ecdeeafcf54e8445050451012957fd044ce8ad37e23ea6c2fd002`).
Both projects carry the GPL-3.0 license.

Conversion: `ffmpeg -i reverb.ogx -ar 48000 -ac 2 -c:a pcm_f32le reverb.48k.wav`.
The floating-point format retains samples above 0 dBFS in the original Vorbis
decode. Playback applies the Web Audio ConvolverNode's default normalization.
