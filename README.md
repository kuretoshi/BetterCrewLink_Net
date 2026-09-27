# BetterCrewLinkKai .NET

BetterCrewLinkKai の通常版をベースに、Electron の挙動を .NET 8 / WPF へ移植するためのデスクトップ版です。

## 方針

- 通常版 BetterCrewLinkKai と同じ Socket.IO / WebRTC シグナリング互換を保つ
- Among Us の状態・ロビーコード・プレイヤー情報は通常版と同じ offsets を使って読む
- Electron 固有の機能は C# / WPF / Windows API で同等の挙動に置き換える
- 公開ロビー機能は削除し、ロビー公開イベントは送信しない

## 起動

```powershell
dotnet run --project BetterCrewLinkKai.DotNet
```

## ビルド

```powershell
dotnet build BetterCrewLink_DotNet.sln
```

設定ファイルは `%APPDATA%\BetterCrewLinkKai.DotNet\settings.json` に保存されます。
