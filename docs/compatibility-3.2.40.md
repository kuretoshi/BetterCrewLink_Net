# TanukiBCL 3.2.40 互換チェックリスト

公式リリース: https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.40

- NoS UchuAddonのシトラス感染時ボイスチェンジャーを、ホストのロビー設定で0～100%に調整する。
- 0%では加工せず、100%は従来の強さにする。旧版ホストから強さが届かない場合は100%を使う。
- ロビー設定の共有フィールド `nosCitrusVoiceEffectStrength` をPC・.NET・モバイルで一致させる。
- VoiceProbeのポリシーテストとWPFクライアントのビルドで確認する。実ゲームでの音声確認は未実施。
