# TanukiBCL.Net v3.2.20-net-beta.1

Windows x64向けベータ・プレリリースです。公式[タヌキのベタクル v3.2.20](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.20)のMOD検出修正を取り込みました。

- 対象のAmong Usプロセスに読み込まれたMOD DLLを、ゲームフォルダ名より優先して判定します。
- TOH4E／TOH4E_EMの新しいDLL名 `TownOfHostForE.dll` と `TownOfHostForE_EM.dll` を認識します。従来のアンダースコア付きDLL名も引き続き認識します。
- plugins内のTOH4E系DLL名について、アンダースコア・ハイフン・大文字／小文字の違いに対応します。

MOD検出の回帰テスト、Releaseビルド、C#全体のネスト上限3段検査を通過しています。起動中のTOH4E_EMプロセスで新しいDLL名のロードも確認しました。公式3.2.20との実音声・複数人同期は未確認で、βテスト対象です。

通常は `TanukiBCL.Net-Setup-3.2.20-net-beta.1.exe` を使用してください。持ち運び用・アプリ内更新用ZIPもあります。インストーラーは未署名です。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。
