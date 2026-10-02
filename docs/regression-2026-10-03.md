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

## 固定起動先への更新と実機再接続

後続検証で.NETクライアントのみ通常終了し、終了を確認後、`0a1bb2b`のReleaseを既存の`publish/nos-colors-20261003`へself-contained win-x64で発行した。発行物でも装備・NoS色・名前・オーバーレイ・設定トランザクションの自己テスト終了0を確認。

`--game-process-id 60232`で再起動し、同じNoSロビーCOWFZUへ参加。実画面でローカルの赤い体と服、相手3人の緑／茶／青の体と服、名前末尾を確認した。診断画面で3相手ともdata-ready、Opus受信1246／459／643フレーム、送信1242フレーム・3peerを観測。途中では一部接続／受信待ちがあったが、相手側のリロード操作なしで全員の受信に至った。

Among Usの既存4プロセス（36532/41704/55924/60232）と公式3.2.7・vDEVは終了していない。新.NETプロセスは53708。DLL SHA256は`6033FCD7F801EF94B4FE33D40B5D1A7E1A5B898CCE021EC0612140D4E804BE6E`。最後に通常音声画面へ戻した。

この結果は実機への反映とロビー内の再接続・受信の証拠であり、実音声の聴取、SNR装備、変装、ゲーム上の会議枠の見た目やフェードの比較を完了したものではない。
