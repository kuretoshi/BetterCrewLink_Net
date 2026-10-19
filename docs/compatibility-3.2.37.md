# TanukiBCL 3.2.37 互換チェックリスト

公式リリース: https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.37
PC版修正: https://github.com/kuretoshi/TanukiBCL/pull/22

- 「視界内のプレイヤーの声だけが聞こえる」が有効なときも、NoS第三陣営は視界半径ではなくロビー設定の音声距離を使う。`NosPlayer.IsNeutral`と`IsThirdParty`の両方を判定する。
- クルーの視界連動、インポスターの固定距離、他MODの従来動作を維持する。
- Airshipのアーカイブ―コックピット相当の遠距離、近距離、陣営別、他MOD、設定OFFを音声ポリシー自己テストで確認。Releaseビルドとネスト深さ3以下も確認。実ゲームでの聞こえ方はベータテスト対象。
