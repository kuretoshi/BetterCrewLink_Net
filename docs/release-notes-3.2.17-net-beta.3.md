# TanukiBCL.Net v3.2.17-net-beta.3

Windows x64向けのベータ・プレリリースです。公式[タヌキのベタクル v3.2.17](https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.17)に対応したβ2から、プレイヤー表示の更新時に同じブラシ・左右反転・接続品質ツールチップを作り直す処理を減らしました。カモフラージュなどの見た目の変更・解除では、キャラ画像を従来どおり更新します。

Releaseビルド、画面・コスチューム・設定・音声の自己テスト、ローカルサーバー再接続テスト、C#のネスト上限3段の検査を通過しました。NoSの1人ロビー待機でβ2と比較したところ、Private Bytesの明確な削減も退行も確認できませんでした。定常時の割り当て率中央値はβ2が0.34 MiB/秒、修正後が0.31 MiB/秒ですが、条件差があるため改善量とは断定しません。

公式3.2.17との実音声の双方向通話、複数人・試合中の長時間メモリ計測は未確認です。NoSの`TBCLFields.nextIndex`読取エラーも観察されており、役職・距離連動の実戦確認には含めていません。

通常は `TanukiBCL.Net-Setup-3.2.17-net-beta.3.exe` を使用してください。持ち運び用とアプリ内更新用の `TanukiBCL.Net-win-x64.zip` もあります。インストーラーはデジタル署名されていません。不具合は[GitHub Issues](https://github.com/kuretoshi/BetterCrewLink_Net/issues)へ報告してください。
