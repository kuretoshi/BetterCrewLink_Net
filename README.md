# TanukiBCL.Net

TanukiBCL v3.2.5 (`33f8d252400d74756ce3bfd7e59b8011bf76d798`) を通信仕様の基準に、.NET 8で段階的に作り直すプロジェクトです。

最初の段階では、音声デバイスやゲームメモリ読み取りを実装せず、ボイスサーバーとの Socket.IO 通信だけを検証します。

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

現在は合成音によるP2P音声経路の検証までです。マイク取得、スピーカー再生、VAD、近接音量計算は後続段階で追加します。

## 旧実装

作り直し前の.NET/WPF実装は、次のGit参照に保存しています。

- branch: `backup/pre-v3.2.5-rewrite`
- tag: `backup-pre-v3.2.5-rewrite-20260927`
- commit: `09f225c`
