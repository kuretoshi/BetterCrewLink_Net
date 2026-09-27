# TanukiBCL.Net

TanukiBCL v3.2.5 (`33f8d252400d74756ce3bfd7e59b8011bf76d798`) を通信仕様の基準に、.NET 8で段階的に作り直すプロジェクトです。

Socket.IO、WebRTC、Opus音声、音声デバイス、ゲームメモリ読み取り、ゲーム状態に応じた音声ミックスまで段階的に実装しています。

## 必要環境

- .NET 8 SDK
- Windows、Linux、macOSのいずれか（現在のプローブはコンソールアプリ）

## ビルド

```powershell
dotnet build TanukiBCL.Net.sln
```

## サーバー接続だけを確認

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- --server https://bettercrewl.ink --seconds 10
```

`[OK] Socket.IO接続成功` が表示されれば、Socket.IO / WebSocket層の疎通は成功です。

## テスト用ロビーへ参加

実際のロビー情報が分かっている場合だけ指定します。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --server https://bettercrewl.ink `
  --lobby ABCDEF `
  --player-id 0 `
  --client-id 12345 `
  --seconds 30
```

送信順序はv3.2.5と同じです。

1. `leave`
2. `id(playerId, clientId, friendCode, playerUid, playerIdentifier)`
3. `join(lobbyCode, playerId, clientId, isHost)`

受信した `clientPeerConfig`、`setClients`、`join`、`signal` などは標準出力へ記録します。別クライアントが同じロビーへ参加した場合はWebRTC接続も開始しますが、音声送受信はまだ行いません。

## WebRTCセルフテスト

2つのSocket.IOクライアントを一時ロビーへ参加させ、次を自動検証します。

- `join`イベントの受信
- offer / answerの交換
- ICE candidateの交換
- WebRTC接続の確立
- データチャネル上のprobe / ACK双方向通信
- 440Hzテスト信号のOpusエンコード、RTP送信、受信、デコード
- 復号PCMのフレーム数、RMS、推定周波数の検証

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --server https://bettercrewl.ink `
  --self-test `
  --seconds 30
```

成功時は `[PASS] Socket.IO、WebRTCデータチャネル、Opus音声トラックの検証に成功しました。` と表示し、一時ロビーから退出します。サーバーから受け取るTURN認証情報はログへ出力しません。

合成音による自動検証に加えて、実マイク入力、スピーカー再生、簡易VADまで実装しています。ゲーム状態に応じた近接音量計算は後続段階で追加します。

## 実マイク・スピーカーで確認

利用可能なデバイス番号を表示します。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- --list-audio-devices
```

2台で同じロビーコードを指定して起動します。`client-id` と `player-id` は重複させないでください。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --server https://bettercrewl.ink `
  --lobby ABCDEF `
  --player-id 0 `
  --client-id 10001 `
  --host `
  --live-audio `
  --input-device 0 `
  --output-device 0
```

マイクは48kHz / 16bit / monoで20ms単位に取得し、stereo Opusへ変換して送信します。受信Opusは48kHz / 16bit / stereo PCMへ復号し、400msのバッファを介して再生します。RMSによる簡易VADもサーバーへ通知します。

同じ部屋で2台を試す場合は、ハウリング防止のため両方でヘッドホンを使用してください。

## Among Us状態の読み取り

起動中の全Among Usプロセスを読み取り専用でスキャンします。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- --scan-game
```

各プロセスについて、ロビーコード、ゲーム状態、ローカルプレイヤー、役職、生死、全プレイヤーの座標を表示します。5プロセスでの検証では、次の条件が揃うとPASSになります。

- 全5プロセスの読み取りに成功
- 全プロセスが同じロビーを認識
- 各視点でプレイヤーが5人
- ローカルプレイヤーのclient IDが5種類
- 全視点でゲーム状態と人数が一致
- 現在のゲーム状態に応じた音声ルールが一致

状態や人数を指定して、状態変化を明示的に検証できます。期待する状態が通知されるまで最大20秒待つため、起動直後の古い状態を誤採用しません。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- --scan-game `
  --expected-game-state Discussion `
  --expected-alive 3 `
  --expected-dead 2 `
  --expected-impostors 1
```

`Tasks`中に全員が近距離であることも検証する場合は `--expect-nearby` を追加します。

## 5クライアント音声統合テスト

起動中の5つのAmong Usプロセスへ仮想音声クライアントを1つずつ対応させ、同じ実ロビーへ接続します。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --game-audio-self-test `
  --seconds 45
```

5視点から相手4人への計20方向について、WebRTC接続、双方向Opusテスト音の受信、client IDの対応、ゲーム状態から算出した音量・パン・遮断理由を検証します。実マイクやスピーカーは使用しません。

オフセットはTanukiBCL v3.2.5と同様にBetterCrewLink offsetsから取得し、GameAssemblyのシグネチャで現在の実アドレスを解決します。プロセスメモリへの書き込みは行いません。

## ゲーム状態と音声を連動

対象のAmong Usプロセスを指定すると、ロビーコード、player ID、client ID、ホスト状態を自動取得してボイスサーバーへ参加します。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --server https://bettercrewl.ink `
  --game-process-id 3192 `
  --live-audio `
  --input-device 0 `
  --output-device 0
```

ゲーム状態は500ms周期で更新し、Socket.IOのsocket IDとAmong Usのclient IDを対応付けます。受信音声には次を適用します。

- TanukiBCL v3.2.5既定値と同じ最大距離5.32
- Web Audioのlinear距離モデル相当の音量減衰
- 左右位置によるパン
- 生存者から死亡者の音声を遮断
- 会議中は距離とパンを無効化し、生存者から死亡者だけを遮断
- 通気口内音声と切断済みプレイヤーを遮断

複数peerのPCMはpeer別バッファから同じタイムライン上でミックスしてスピーカーへ出力します。

## 旧実装

作り直し前の.NET/WPF実装は、次のGit参照に保存しています。

- branch: `backup/pre-v3.2.5-rewrite`
- tag: `backup-pre-v3.2.5-rewrite-20260927`
- commit: `09f225c`
