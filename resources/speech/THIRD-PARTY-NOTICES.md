# Local Japanese speech recognition

This package uses Vosk 0.3.38 and vosk-model-small-ja-0.22, distributed under
Apache License 2.0. See `Licenses/Vosk-Apache-2.0.txt` in the release package.

- Vosk: https://github.com/alphacep/vosk-api
- Kaldi: https://github.com/kaldi-asr/kaldi
- Japanese model: https://alphacephei.com/vosk/models

Desktop clients recognise their own microphone. A host also recognises voice
streams from identified Web/mobile participants, for active hero or forbidden
word jobs. No speech service receives the audio or transcripts.

Run `tools/prepare-speech.ps1` before a development build. The release packaging
script calls it automatically. Model downloads are pinned by SHA-256.
