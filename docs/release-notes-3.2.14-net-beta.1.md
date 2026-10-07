# TanukiBCL.Net v3.2.14-net-beta.1

公式[タヌキのベタクル v3.2.14](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.14)の変更を.NET/WPF版に取り込んだWindows x64向けベータ・プレリリースです。

- NoSのSkin・Hat・Visor・背面画像・体マスクを加工済みPNGとしてWeb版へ転送する経路を追加しました。通常のゲーム状態更新では画像IDだけを送り、初回・着替え・途中参加・不足画像の再要求時に画像を送ります。
- 画像キャッシュは8 MiBを上限とし、転送は100 msあたり最大1枚に制限しています。
- 3.2.13のSNRローカルコスチュームとNoSロビー表示も含みます。

**注意:** 公開済みWeb版3.6には画像の受信・表示処理がないため、このPC版だけを更新してもWeb上のNoSコスチュームは表示されません。対応Web版との実機表示は未検証です。完全互換版ではありません。

通常は `TanukiBCL.Net-Setup-3.2.14-net-beta.1.exe` を使用してください。持ち運び用とアプリ内更新用の `TanukiBCL.Net-win-x64.zip` もあります。インストーラーはデジタル署名されていません。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。
