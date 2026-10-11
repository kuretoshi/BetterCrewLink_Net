# TanukiBCL.Net v3.2.24-net-beta.1

Windows x64向けベータ・プレリリースです。公式[タヌキのベタクル v3.2.24](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.24)の変更を.NET/WPF版へ取り込みました。

- SNRの押し出し落下中は、落下前の位置で通話を維持し、受信音声に残響を付けます。落下終了後は通常の生死判定に戻ります。
- NoSのビビリ・おしゃべり・マイクテスト向けに、マイク声量と有効状態をローカルUDPで送信します。
- メガホンレーザーの距離拡大・音声加工ON/OFF、携帯メガホンの追加距離、ビビリ自滅・メガホン使用中の死亡残響に対応します。
- Web・モバイルの声量をホストの.NET版から同じPCのNoSへ中継するベータ機能を追加しました。対応するMobile 3.13以降と、NoS参加者全員のタヌキ丼0.8.0 Build37以降が必要です。

Releaseビルド、合成音声・状態データ、ローカルUDP送信、送信者認証、既存の音声ルールとネスト上限3段の検査を通過しました。実ゲームでの新しい役職効果の聴感、Mobile 3.13との実通信、SNR落下中の生体データ読取は未確認です。これらはβテストで確認してください。

通常は`TanukiBCL.Net-Setup-3.2.24-net-beta.1.exe`を使用してください。持ち運び・アプリ内更新用ZIPもあります。インストーラーは未署名です。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。

| ファイル | SHA-256 |
| --- | --- |
| `TanukiBCL.Net-Setup-3.2.24-net-beta.1.exe` | `b899056405dc7cdbccbcc509332f983e538e17312a344d0cb6c1a6bbda873782` |
| `TanukiBCL.Net-win-x64.zip` | `8ec0b24e9ec2d528eeb5c9d4148cdd045d1ac2c37b72f15e64ec70d3dceba30a` |
