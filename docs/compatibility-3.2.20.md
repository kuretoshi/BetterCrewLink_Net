# TanukiBCL v3.2.20 互換チェック

基準は公式 [`v3.2.20`](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.20) (`88c039523b73bbe0a03f832552ffd6ad9489c279`)。.NET版は従来どおり64bit版Among Usのみを対象とする。

| 公式での変更 | .NET版 |
| --- | --- |
| `TownOfHostForE_EM.dll` と `TownOfHostForE.dll` をTOH4E系として検出 | ロード済みDLL名の優先リストへ追加。既存のアンダースコア付き2種類も維持。 |
| 対象PIDのロード済みDLLをフォルダ名より優先 | SNR・NoS・TOH4Eのロード済みDLLを先に判定し、見つからなければTOH4Eフォルダ名へフォールバック。遅延ロード中の判定も維持。 |
| plugins内のTOH4E DLL名でアンダースコア・ハイフン・大文字小文字の違いを許容 | ファイル名の区切り記号を除去して厳密比較。別MODやバックアップ拡張子への誤判定を防止。プラグイン列挙は大文字の`.DLL`も対象。 |

開発ソースの版は `3.2.20-netdev.0`。Releaseビルド、MOD検出の回帰テストを含む音声ポリシー自己テスト、ゲームスキャン自己テスト、全C#ソースの制御構文ネスト上限3段検査を通過。起動中のTOH4E_EMプロセスで新形式 `TownOfHostForE_EM.dll` のロードも確認した。新ビルドの実ゲーム画面でのMOD認識と公式3.2.20との複数人通話は未確認。公開中のβ版は引き続き `3.2.19-net-beta.1`。
