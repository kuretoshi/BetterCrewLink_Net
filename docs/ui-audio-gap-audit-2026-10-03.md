# GUI・音声の追加差分確認

基準はリリース3.2.7 `9861ccc8137bb63a7d3834f493be0b784a288544`。

## サンプルレートデバッグ（未移植）

`AudioController.ts` の `createInputChain` は `getUserMedia` のsampleRate制約を `oldSampleDebug ? 48000 : undefined` とする。.NETのAudioDeviceSessionは通常時からWaveInを48kHz/16bit/monoに固定している。単なる設定スイッチ追加やOpusのクロック変更では同等にならない。通常時の入力形式選択と、DSP/Opusへ入る前の48kHz変換を実装・検証してから設定を接続する必要がある。現時点ではUIだけの動かない項目は追加していない。

## 本人の長い名前

`VoiceView.tsx` は20px・nowrap・最大幅115pxだがoverflow/ellipsisを指定しない。一方.NETはCharacterEllipsisで「開発者くれとし3」などを省略していた。固定幅の領域に等倍の非クリップViewboxを置き、テキストを制約なしで測定するよう修正した。

`--voice-view-self-test` で20px・nowrap・省略なし、115pxより長い名前の実測幅を確認。Releaseビルドと設定回帰テストも成功。現在起動中のNoS色対応版には名前修正をまだ適用しておらず、実画面の比較は次の同一パス更新後に行う。
