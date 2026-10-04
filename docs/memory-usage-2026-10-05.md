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
