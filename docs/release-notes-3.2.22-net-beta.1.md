# TanukiBCL.Net v3.2.22-net-beta.1

Windows x64向けベータ・プレリリースです。公式[タヌキのベタクル v3.2.22](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.22)の修正を取り込みました。前の配布版から、3.2.21で追加されたNoS音声・役職の変更も含みます。

- NoSのデータを取得できないまま試合へ移った場合、ロビーで予定した再試行を待たずに読み取り位置を再取得します。
- 読み取り位置取得の失敗後は1秒、2秒、以後最大3秒の間隔で再試行します。
- NoS v3.5.3.6の`BodyType`・`NeckLength`と役職・能力状態を読み、サイズ変化、ろくろ首、バーサーカー、シトラス、虹色スターの音声効果に対応しました。フィクサー妨害のローパス設定も追加しました。

Releaseビルド、NoSの時刻制御テスト、音声ポリシー、設定画面、ゲーム状態の回帰テスト、C#全147ファイルのネスト上限3段検査を通過しました。ZIPにはアンインストーラーを含み、更新パッケージの安全性検査も通過しています。

一部の起動中NoSプロセスでは`TBCLFields.nextIndex`の位置自体を解決できず、ライブ診断がタイムアウトしました。今回の再試行短縮はその根本原因の解消を保証しません。新しい音声効果の聴感、公式3.2.22との双方向通話、NoS画像のWeb実機表示は未確認で、βテスト対象です。

通常は`TanukiBCL.Net-Setup-3.2.22-net-beta.1.exe`を使用してください。持ち運び・アプリ内更新用ZIPもあります。インストーラーは未署名です。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。

| ファイル | SHA-256 |
| --- | --- |
| `TanukiBCL.Net-Setup-3.2.22-net-beta.1.exe` | `a84fa8bd166de3600e2c35c09c60ab059bdd40012184697e9260aa221b019bdf` |
| `TanukiBCL.Net-win-x64.zip` | `b18bd424eb5d2dfbaa32fc824274bb0d82a72abde8d53b71b923ea99c1850140` |
