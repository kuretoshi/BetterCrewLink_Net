# メモリ使用量の初回実測（2026-10-05）

目的は、Electron版TanukiBCL 3.2.8と.NET版の実メモリ使用量を、同じ状態のプロセスツリー全体で比較すること。数値はWindowsの`Win32_Process.PrivatePageCount`（Private Bytes）と`WorkingSetSize`を全子プロセスで合計したもの。Working Setには共有ページの重複計上があり得るため、主にPrivate Bytesを比較する。

## アイドル時

- 公式: `D:\VSCode\BetterCrewLink\dist\win-unpacked\TanukiBCL.exe`、ファイルバージョン3.2.8
- .NET: インストール済み`TanukiBCL.Net` beta.4
- Discordは起動中。Among Usとボイスクライアントは測定前に起動していなかった。両アプリを非表示で起動し、約8秒待ってから2秒間隔で5回測定した。測定用に起動した6プロセスは終了し、残存しないことを確認した。

| 測定 | 公式プロセス数 | 公式Private MiB | .NETプロセス数 | .NET Private MiB |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 5 | 212.8 | 1 | 79.8 |
| 2 | 5 | 212.7 | 1 | 80.0 |
| 3 | 5 | 211.0 | 1 | 80.3 |
| 4 | 5 | 211.3 | 1 | 80.4 |
| 5 | 5 | 211.3 | 1 | 80.8 |

この測定では.NET beta.4のPrivate Bytesは公式より約131 MiB小さい。ただし、ゲーム未起動・音声未接続のアイドル比較に限る。両者の設定やキャッシュが完全に同じとは保証できず、現在の開発版で追加した残響バッファ解放の効果も含まない。

## 再測定

両アプリを同時に起動した後、各ルートPIDで読み取り専用のスクリプトを実行する。スクリプトはアプリを起動・終了せず、子プロセスを含めてサンプリングする。

```powershell
$officialProcessId = 12345 # 実際の公式ルートPIDに置き換える
$netProcessId = 67890 # 実際の.NETルートPIDに置き換える
.\tools\measure-memory.ps1 -RootProcessIds @($officialProcessId, $netProcessId) -Samples 30 -IntervalSeconds 2 |
    Export-Csv .\memory-samples.csv -NoTypeInformation -Encoding UTF8
```

次は同じAmong Usロビー、同じ参加人数、音声接続、同じ設定で「待機」「試合中」「幽霊残響ON→OFF」をそれぞれ測る。開発版とbeta.4の差分を比較する場合も、同じ条件と同じ測定期間を使う。実戦でのメモリ削減量はまだ未測定。

## GameAssemblyスナップショット

ゲーム読取はオフセット初期化中にGameAssemblyの全メモリ像をシグネチャ検索用に読み込む。従来はオフセットが確定した後も`ReaderContext`がこの配列を保持していた。開発版は検索完了直後に参照を解放し、4 KiBページ読取用バッファも使い回す。

2026-10-05にバニラAmong Usを1プロセス起動し、`--module-snapshot-self-test --game-process-id 40368`を実行。`moduleRead=63107072/63107072 failedPages=0`、スナップショット容量は60.2 MiB、初期化後の保持容量は0で終了コード0だった。測定用ゲームプロセスは終了した。

同じメニュー状態でインストール済みbeta.4と開発版を約9秒後から5回測ったPrivate Bytesは、beta.4が175.5→165.5 MiB、開発版が165.6→165.9 MiBで、安定した差を実測できていない。未参照になった配列のGC・OSへの返却タイミングや、ゲーム読取の開始時点が異なる可能性がある。したがって現時点で証明できるのは**長期保持していた60.2 MiB配列への参照を初期化後に解消したこと**であり、プロセスPrivate Bytesの削減幅ではない。

## 幽霊残響の共有インパルス

以前はリモート音声の再生経路を作る時点で、幽霊残響を使わなくても共有インパルスを読み込み、FFT用に約8.9 MiB確保していた。開発版は残響をONに設定する時だけ準備する。`--policy-self-test`で、通常のプロバイダー生成・OFF時は未準備、ON後は8.9 MiBの共有データが準備され、残響出力の回帰テストが通過することを確認した。この値も保持オブジェクトの容量であり、プロセスPrivate Bytesの実測削減値ではない。
