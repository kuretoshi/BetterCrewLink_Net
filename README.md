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

受信した `clientPeerConfig`、`setClients`、`join`、`signal` などは標準出力へ記録します。この段階ではWebRTCのoffer/answer処理や音声送受信はまだ行いません。

## 旧実装

作り直し前の.NET/WPF実装は、次のGit参照に保存しています。

- branch: `backup/pre-v3.2.5-rewrite`
- tag: `backup-pre-v3.2.5-rewrite-20260927`
- commit: `09f225c`
