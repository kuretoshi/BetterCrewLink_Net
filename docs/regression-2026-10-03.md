# アバター・会議表示変更後の回帰試験

対象: `fb15989`までの実装と、今回の設定トランザクション自己テスト終了コード修正。

## 実行結果

Releaseビルドは警告/エラー0。クライアントを別プロセスで起動し、以下を同時指定して終了0を確認した。

```
--cosmetics-self-test --nos-avatar-self-test --overlay-self-test
--voice-view-self-test --settings-self-test --session-lifecycle-self-test
--settings-application-self-test --settings-transaction-self-test
--input-processing-self-test
```

設定テストは合成状態・差し替えた保存処理、音声入力処理は合成信号で検証する。出力では合成AEC 25.8dB、NS 14.5dB、AGC 2.6倍・クリップ0を観測したが、実機音響やChromium相当を証明する値ではない。

VoiceProbeを各オプション別に実行し、すべて終了0。

- `--policy-self-test`: 音声可聴条件、ラジオ、音声効果、Airship会議判定など。
- `--vad-self-test`: 周波数帯VAD、PTT/PTM。
- `--nos-palette-self-test`: パレット構造、不正浮動小数、途中更新、RGB丸め。
- `--game-scan-self-test`: 合成読取結果の期待値判定。負例のためのFAIL行は予定どおりで、実ゲームをこのコマンドで読んだわけではない。
- `--server-reconnect-self-test`: 127.0.0.1の専用サーバーでtransport切断後の同一ロビー再参加、古いpeerの消去、手動reloadの重複join防止、reload中の終了、静的ロビー再参加。

## テストハーネスの修正

ClientSettingsTransactionSelfTest.Runは失敗時に例外を捕捉して1を返すが、Appはその戻り値を無視していた。1なら外側の失敗処理へ渡すよう修正した。自己テスト専用の失敗注入を追加し、`--settings-transaction-self-test --settings-transaction-failure-exit`が実際に終了1となることを確認。通常起動やユーザーの設定保存動作は変更していない。

起動中のゲームと音声クライアントはこの試験では更新・終了していない。公式3.2.7実機との再接続・実音声、SNRの装備・オーバーレイ、ゲーム上の会議枠比較は別途必要。全体目標の完了判定ではない。
