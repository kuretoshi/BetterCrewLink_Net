# 3.2.7「サンプルレートデバッグ」の移植と検証

比較対象はTanukiBCL 3.2.7リリースコミット`9861ccc8137bb63a7d3834f493be0b784a288544`。同版の`AudioController.ts`は通常`getUserMedia`へsampleRateを指定せず、`oldSampleDebug`がONのときだけ48000を指定する。`settingsStore.ts`の既定はOFF。詳細設定の日本語表示は「サンプルレートデバッグ」、ON時の確認文は「依頼された場合のみ有効にするテスト機能です。」。

.NET版はWinMM録音にフォーマット指定が必須なので、OFFでは選択したマイクに対応するWindows音声エンドポイントの共有モード既定レートを選ぶ。WinMM名が短縮される場合は前方一致で一意に解決し、複数候補・取得失敗・異常値では48kHzへフォールバックする。ONでは48kHzを要求する。既定レートのPCM16モノラルをWinMMドライバーが`WAVERR_BADFORMAT`で拒否した場合に限り48kHzで再試行する。デバイス占有など別の失敗は隠さない。

録音レートが48kHz以外の場合はNAudio 2.2.1のWDL resamplerで48kHzモノラルへ変換し、既存の20msフレーマーへ渡す。エコーキャンセル・VAD・ゲイン・Opusは従来どおり48kHz/20msの完成フレームを受ける。設定のON/OFFは保存され、稼働中の変更時は音声セッションを再生成する。詳細設定画面には原版のチェック項目とON時確認・取消を追加した。

検証:

- Releaseビルド警告/エラー0。`--settings-self-test --settings-application-self-test --settings-transaction-self-test --input-processing-self-test`の終了コード0。設定ONの確認・取消・保存、OFFの即時保存、設定変更時の再起動分類を検査。
- 44.1/48/96kHzの合成1kHz音声を不規則なコールバック幅で入力し、出力が48kHz/20msの完全フレームで、長さと周波数・RMSが許容範囲にあることを検査。
- 実Windows録音: AG06/AG03（WinMM入力4）はOFFで44.1kHzから36フレーム/750ms、ONで48kHzから37フレーム/750ms。NVIDIA Broadcast（入力0）はOFF/ONとも48kHz、各37フレーム/750ms。録音データは送信・保存せず、フレーム数のみ観測。
- self-contained win-x64配布物`publish/sample-rate-20261003`を生成し、同配布物でも設定・入力処理の自己テスト終了コード0。Client DLL SHA-256 `B73639F477999473234E0524778FB287B2D1A0BB8F60642A6B1188295C11F6F0`。

これは原版Chromiumのデバイス選択、音声処理アルゴリズム、実際の録音フォーマットをビット単位で再現する証拠ではない。選択マイクのWinMM名とCoreAudio名が一意に対応しない場合は48kHzに戻る。実ロビーでのON/OFF切替後の双方向音声、音質聴感、会議・ラジオとの同時動作は未検証。起動中の.NETクライアントは前の公開ビルドのままであり、この配布物へ切り替えていない。
