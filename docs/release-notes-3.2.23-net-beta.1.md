# TanukiBCL.Net v3.2.23-net-beta.1

Windows x64向けベータ・プレリリースです。公式[タヌキのベタクル v3.2.23](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.23)の修正を取り込みました。

- AirshipでXまたはY座標が0のプレイヤーを有効な位置として扱うことを確認し、近接通話の回帰テストを追加しました。
- 64bit版SNRの役職・Jumboサイズ取得に向け、補助リーダーのライブレイアウト生成条件を公式版に合わせました。64bitアドレスとJumboの現在／最大サイズの合成テストを強化しました。
- 前のβ版までのNoS役職・音声効果、読取再試行の修正を引き継ぎます。

Releaseビルド、SNR合成読取、音声ポリシー、ゲームスキャン、設定画面、C#全147ファイルのネスト上限3段検査を通過しました。公式3.2.23との実音声、Airship実マップでの座標0の通話、64bit SNR実プロセスからの役職・Jumboサイズ取得は未確認です。NoS画像のWeb実機表示など、既知のβ検証項目も残っています。

通常は`TanukiBCL.Net-Setup-3.2.23-net-beta.1.exe`を使用してください。持ち運び・アプリ内更新用ZIPもあります。インストーラーは未署名です。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。

| ファイル | SHA-256 |
| --- | --- |
| `TanukiBCL.Net-Setup-3.2.23-net-beta.1.exe` | `386d0d0ab427f109711f856593181d5fce6d41cc9b6ae4643e23bd75ed49c78b` |
| `TanukiBCL.Net-win-x64.zip` | `182768e2ee44bd323697daad8e5203359410dc4d74ab699f957eb01897897f1c` |
