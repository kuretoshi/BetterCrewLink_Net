# TanukiBCL.Net

## WPFクライアント

Among Usプロセス、マイク、スピーカーを選択して近距離ボイスへ接続できる最小クライアントです。

```powershell
dotnet run --project src/TanukiBCL.Client
```

起動時に実行中のAmong Usを自動検出します。複数プロセスがある場合は対象PIDを選択し、`接続開始`を押すとゲーム状態からロビー情報を読み取ってボイスサーバーへ接続します。画面には接続状態、ゲーム状態、ロビー、参加人数、マイク発話状態に加え、参加者ごとのPeer状態、音声ルール、現在の音量を表示します。接続中はマイクとスピーカーを個別にミュートできます。マイクミュート時は音声送信を止め、VADも即座にOFFにします。他プレイヤーの役職や未発見の死亡情報は表示しません。設定画面から接続先、入出力デバイス、マスター音量、マイクゲインなどを保存できます（3.2.7設定項目の移植は継続中）。

当初はTanukiBCL v3.2.5 (`33f8d252400d74756ce3bfd7e59b8011bf76d798`) を通信仕様の基準として再構築しました。現在の完成目標は[リリース v3.2.7](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.7) (`9861ccc8137bb63a7d3834f493be0b784a288544`) の機能・GUI・実機相互運用の完全互換です。現時点では未完成で、差分と検証状況は[3.2.7互換チェックリスト](docs/compatibility-3.2.7.md)に記録しています。

Socket.IO、WebRTC、Opus音声、音声デバイス、ゲームメモリ読み取り、ゲーム状態に応じた音声ミックスまで段階的に実装しています。

## 必要環境

- .NET 8 SDK
- Windows、Linux、macOSのいずれか（現在のプローブはコンソールアプリ）

## ビルド

```powershell
dotnet build TanukiBCL.Net.sln
```

Windows向けのself-contained配布ZIPは、バージョンを指定してローカルで作成できます。

```powershell
& tools/package-release.ps1 -Version 3.2.7-netdev.0
```

`dist/<version>/TanukiBCL.Net-win-x64.zip`とSHA-256が生成・表示されます。配布物にはNoS/SNR補助リーダーと更新補助ツールが入ります。このコマンドはGitHub Releaseを公開しません。アプリ内の「アップデート」は、このリポジトリに同名のZIPを含む新しいReleaseがある場合にだけ有効になります。公開Releaseからの実更新、新規Windows環境での起動、完全互換はまだ未検証です。

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

起動中のTanukiBCLと同じAmong Usロビーへ参加し、実装間の互換性を確認する場合は、TanukiBCLが追跡していない別視点のプロセスIDを指定します。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --tanuki-interop-test `
  --game-process-id 19600 `
  --seconds 30
```

WebRTC接続とTanukiBCLへのOpusテスト音送信を必須判定し、TanukiBCLから音声フレームを受信できた場合はそのclient IDも表示します。

WPFクライアントは起動時に追跡対象のAmong Usプロセスを選択できます。

```powershell
dotnet run --project src/TanukiBCL.Client -- --game-process-id 19600
```

Peer一覧の「Opus受信」が増えていれば、相手からの音声データを実際に受信・復号できています。
「Opus送信」が増えていれば、マイク音声をOpusへ変換して接続中Peerへ送信できています。

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

会議開始・終了をまたぐ追従テストも実行できます。開始後の案内に従って、`Tasks`状態から会議を開始し、その後会議を終了します。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --game-audio-transition-test `
  --seconds 180
```

WebRTC接続を切らずに `Tasks → Discussion → Tasks` の各段階で、計20方向の音量・パン・生死による遮断が更新されたことを検証します。

ロビー退出・再参加後の音声復旧も検証できます。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --game-audio-recovery-test `
  --seconds 60
```

最初に計20方向の音声を確認し、1クライアントを同じロビーへ再参加させます。古いpeerを破棄したうえで参加者一覧から接続を張り直し、対象に関係する8方向を含む全20方向へ復旧したことを検証します。

ボイスサーバー接続そのものを切断・再接続する復旧試験も実行できます。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --game-audio-server-recovery-test `
  --seconds 60
```

新しいSocket.IO socket IDでロビーへ戻り、同一client IDに残った旧peerを除去します。WebRTCの再接続が競合して失敗した場合はsocket IDの順序で片側だけが再オファーし、全20方向へ自動復旧することを検証します。

1つの視点だけ実マイク・スピーカーを使用し、残り4視点から仮想Opus音声を送るハイブリッド試験も実行できます。ヘッドホンを使用してください。

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- `
  --live-game-audio-test `
  --game-process-id 2892 `
  --input-device 0 `
  --output-device 0 `
  --seconds 60
```

対象視点で4方向の音声を復号し、ゲーム状態から求めた音量・パンを実再生ミキサーへ適用します。同時に実マイクのPCMが4つの仮想視点へ届くことと、VADの発話通知も検証します。片方向メディアを検出した場合は該当peerだけを自動再接続します。距離外または生存者から見た死亡者の音声は、受信できていても規則どおり無音になります。

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
