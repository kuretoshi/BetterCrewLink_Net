# TanukiBCL.Net 3.2.30 β1

公式[タヌキのベタクル3.2.30](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.30)相当の、Windows x64向けベータ・プレリリースです。

- Among Us終了時にNoSのプレイヤー一覧が一時的に欠落しても、音声役職送信・ホスト中継と画面側の状態更新が例外を起こさないようにしました。
- プレイヤー一覧が欠落した場合の回帰テストを追加しました。
- 配布ZIPで `opus.dll` と `speexdsp.dll` の同梱を検査し、ZIPを展開した状態での起動も確認しました。Electron本家の `registry.node` は.NET版の依存ファイルではありません。

通常は `TanukiBCL.Net-Setup-3.2.30-net-beta.1.exe` を使用してください。持ち運び・アプリ内更新用ZIPもあります。インストーラーは未署名です。
