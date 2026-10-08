# TanukiBCL.Net v3.2.19-net-beta.1

Windows x64向けベータ・プレリリースです。公式[タヌキのベタクル v3.2.19](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.19)のTOH4E／TOH4E_EM変更を取り込みました。

- インポスター判定を、ゲーム本体のフラグとロード済みMOD DLLの陣営情報の組合せへ変更。新役職も一覧に含まれれば手書きの役職名追加なしで判定します。
- キル可能な第三陣営・アニマルズ役職について、幽霊の声を聞くか個別または陣営一括で設定できます。ホストの設定と役職一覧はロビーから参加者へ同期され、参加者は閲覧専用です。
- デバッグ画面にMOD由来の陣営情報を表示します。64bit版Among Usのみ対応します。

TOH4E_EMの起動中プロセスから公式と同数の149役職（対象の第三陣営16・アニマルズ7）を取得しました。自動テストとC#全体のネスト上限3段検査を通過しています。公式3.2.19との実音声・複数人同期は未確認です。

通常は `TanukiBCL.Net-Setup-3.2.19-net-beta.1.exe` を使用してください。持ち運び用・アプリ内更新用ZIPもあります。インストーラーは未署名です。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。
