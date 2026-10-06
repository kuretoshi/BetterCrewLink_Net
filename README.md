# タヌキのベタクル .NET ベータ (TanukiBCL.Net)

TanukiBCL.Netは、[タヌキのベタクル v3.2.12](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.12)をWindows向け.NET/WPFで作り直している、Among Us用の非公式近接ボイスチャットアプリです。[BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink)と[CrewLink](https://github.com/ottomated/CrewLink)に由来するプロジェクトですが、これらやAmong Us、Innerslothの公式版ではありません。

現在の配布版は **`3.2.12-net-beta.1`（プレリリース）** です。公式タヌキのベタクル v3.2.8との通信・音声・ゲーム連動の主要経路は実機で相互確認し、v3.2.9～v3.2.12の修正内容を反映していますが、バグが存在する可能性があります。64ビット版Among Usのみ対応します。

## 主な機能

- Among Usの位置・会議・生死・陣営に連動した近接ボイスチャット
- 公式TanukiBCL v3.2.8／v3.2.9との同一ロビーでの双方向通話と、ホストとのバージョン差の通知
- マイク・スピーカー選択、個別ミュート、音量と音声エフェクトの設定
- 発話状態やプレイヤーを表示するオーバーレイ、ロビー設定の同期
- SuperNewRoles、Nebula on the Ship、TOH4E_EMの音声ルールへの対応
- Nebula on the Shipのコスチューム（Skin・Hat・Visor）のアバター表示

## ダウンロード

[このリポジトリのReleases](https://github.com/kuretoshi/BetterCrewLink_Net/releases)から `TanukiBCL.Net-Setup-3.2.12-net-beta.1.exe` をダウンロードして実行してください。ユーザー別のフォルダーへインストールされ、スタートメニューから起動・アンインストールできます。持ち運び用には `TanukiBCL.Net-win-x64.zip` も用意しています。ZIP版は任意のフォルダーへ展開して `TanukiBCL.Net.exe` を実行してください。どちらも.NETランタイムを同梱しています。インストーラーはデジタル署名されていません。Windowsの警告やネットワーク許可画面が出た場合は、配布元と内容を確認してください。

タヌキのベタクルのインストーラーやLite版とは別の配布物です。公式版は[こちら](https://github.com/kuretoshi/TanukiBCL/releases)から入手できます。

## 使い方

1. Among UsとTanukiBCL.Netを起動します。ゲームを検出すると自動で接続します。ゲームが未起動ならコンパクト画面で待機します。
2. 必要に応じて設定画面からマイクとスピーカーを変更します。
3. 同じロビーの相手もTanukiBCL.Netまたはタヌキのベタクルを起動し、同じボイスサーバーへ接続します。
4. 必要に応じてマイク、スピーカー、音量、ボイスエフェクト、オーバーレイを設定します。

接続できない場合は、両者のボイスサーバー設定とネットワーク状態を確認し、画面左上のリフレッシュを試してください。ベータ版では初回接続や再接続が不安定になる場合があります。

## ボイスエフェクトとアップデート

キノコカオスやカモフラージュなど、ゲーム状態に応じたボイスエフェクトを備えています。強さの調整やテスト再生は設定画面から行えます。追加役職を含む細部の聞こえ方はベータテスト中です。

設定画面の「アップデート」から、このリポジトリの新しい.NET版リリースを確認できます。更新は利用者が開始したときだけ行います。公開リリースからの実更新と新規Windows環境での起動確認は、ベータ期間中の検証項目です。

## 不具合報告

[このリポジトリのIssues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ、発生した版、Among UsとMODの版、再現手順、期待した動作と実際の動作を添えて報告してください。ゲームのロビーコード、サーバー認証情報、開発者パスワードなどの秘密情報は載せないでください。タヌキのベタクルの[Discord](https://discord.gg/cUX5KUkZPD)も参照できます。

## 開発・検証

現在の完全移植目標は公式TanukiBCL v3.2.12 (`a6bfd966525ed971c69ca5d6c8ca163f5c4d7f40`) です。[3.2.12互換チェックリスト](docs/compatibility-3.2.12.md)と[3.2.9互換チェックリスト](docs/compatibility-3.2.9.md)に反映内容と実機検証を、[3.2.8互換チェックリスト](docs/compatibility-3.2.8.md)に引き続きの残課題を記録しています。[3.2.7チェックリスト](docs/compatibility-3.2.7.md)は履歴です。

以下は開発時の診断コマンドと作り直し初期からの検証メモです。配布版の利用には必要ありません。

### WPFクライアントの開発起動

```powershell
dotnet run --project src/TanukiBCL.Client
```

起動時またはゲーム起動後にAmong Usを自動検出し、ゲーム状態からロビー情報を読み取ってボイスサーバーへ接続します。複数プロセスの開発検証では`--game-process-id <PID>`で対象を明示できます。通常の配布版には手動の「接続開始」診断画面を表示しません。画面には接続状態、ゲーム状態、ロビー、参加人数、マイク発話状態を表示します。開発者用の詳細情報は設定から共通パスワードで開けます。接続中はマイクとスピーカーを個別にミュートできます。マイクミュート時は音声送信を止め、VADも即座にOFFにします。他プレイヤーの役職や未発見の死亡情報は表示しません。設定画面から接続先、入出力デバイス、マスター音量、マイクゲインなどを保存できます。

Socket.IO、WebRTC、Opus音声、音声デバイス、ゲームメモリ読み取り、ゲーム状態に応じた音声ミックスまで段階的に実装しています。

### 開発に必要なもの

- .NET 8 SDK
- Windows x64と64bit版Among Us（WPFクライアント・NoS補助リーダー）

### ビルド

```powershell
dotnet build TanukiBCL.Net.sln
```

Windows向けのself-contained配布ZIPは、バージョンを指定してローカルで作成できます。

```powershell
& tools/package-release.ps1 -Version 3.2.12-netdev.0
```

`dist/<version>/TanukiBCL.Net-win-x64.zip`とSHA-256が生成・表示されます。配布物にはNoS/SNR補助リーダーと更新補助ツールが入ります。ベータ版では続けてNSIS 3.04以降で`tools/build-installer.ps1`を実行してください。この工程はインストーラーを作り、そのアンインストーラーを更新ZIPにも加えるため、ZIPの最終SHA-256はここで変わります。NSISのパスは`-NsisPath`でも指定できます。ベータ版ZIPを先の工程だけで公開しないでください。アプリ内の「アップデート」は、このリポジトリに同名のZIPを含む新しいReleaseがある場合にだけ有効になります。公開Releaseの検出・ダウンロード・検証・展開は確認済みですが、更新後の入れ替えと再起動、新規Windows環境での起動、完全互換はまだ未検証です。

3.2.9以降の配布版は、公式版と同じ招待コード方式のHTTPS認証（`https://debug-auth.kuretoshi.work/v1/debug-auth/verify`）でデバッグ画面を開きます。管理者が発行したコードで担当者が[登録ページ](https://debug-auth.kuretoshi.work/debug-register)からパスワードを登録します。パッケージ作成時は認証先URLだけを `debug-auth.json` に同梱し、パスワードのハッシュは同梱しません。認証先に接続できない場合もローカル認証へは切り替えません。

```powershell
& tools/package-release.ps1 -Version 3.2.12-net-beta.1
& tools/build-installer.ps1 -Version 3.2.12-net-beta.1
```

認証先は `-DebugAuthUrl` で変更できます。ローカル認証版を作る場合は `-DebugAuthUrl ''` と `-DebugAuthFile` を指定してください。ローカルのパスワードは配布者本人が以下で作成し、画面に表示されない入力欄へ16文字以上のパスワードを2回入力します。`-Add` を付けると既存のパスワードを残したまま確認担当者用を追加できます（最大16個）。パスワードをチャット・コマンド引数・Gitに記録しないでください。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/create-debug-auth.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/create-debug-auth.ps1 -Add -Name tester
& tools/package-release.ps1 -Version 3.2.12-net-beta.1 -DebugAuthUrl '' -DebugAuthFile (Join-Path $env:APPDATA 'TanukiBCL.Net\release-debug-auth.json')
```

ローカル認証版のZIPへはソルト付きPBKDF2-SHA256ハッシュのみを同梱します。ハッシュは配布物から解析できるため、十分長い独自のパスフレーズを使ってください。アプリは同梱設定を優先し、`TANUKI_DEBUG_AUTH_URL`／`TANUKI_DEBUG_AUTH` 環境変数は同梱設定のない開発環境でだけ使います。これは公式版と同様にアプリの操作制限であり、アプリや配布ファイルを改変する利用者を防ぐ仕組みではありません。

### サーバー接続だけを確認

```powershell
dotnet run --project src/TanukiBCL.VoiceProbe -- --server https://bettercrewl.ink --seconds 10
```

`[OK] Socket.IO接続成功` が表示されれば、Socket.IO / WebSocket層の疎通は成功です。

### テスト用ロビーへ参加

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

### WebRTCセルフテスト

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

### 実マイク・スピーカーで確認

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

### Among Us状態の読み取り

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

### 5クライアント音声統合テスト

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

### ゲーム状態と音声を連動

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

### 旧実装

作り直し前の.NET/WPF実装は、次のGit参照に保存しています。

- branch: `backup/pre-v3.2.5-rewrite`
- tag: `backup-pre-v3.2.5-rewrite-20260927`
- commit: `09f225c`

## 元プロジェクトとライセンス

このプロジェクトは[タヌキのベタクル](https://github.com/kuretoshi/TanukiBCL)、[BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink)、[CrewLink](https://github.com/ottomated/CrewLink)を基にしています。元プロジェクトの開発者、協力者、翻訳者に感謝します。ライセンスは[GNU GPL v3.0](LICENSE)です。

TanukiBCL.NetはAmong UsまたはInnersloth LLCと関係ありません。Innersloth LLCによる承認、支援、提供を受けたものではなく、Among Usに関する権利はInnersloth LLCに帰属します。
