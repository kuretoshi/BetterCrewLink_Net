# TanukiBCL 3.2.28 互換チェックリスト

公式リリース: https://github.com/kuretoshi/TanukiBCL/releases/tag/v3.2.28

## 反映

- お問い合わせ前に、件名・本文・選択した添付ファイル・自動添付ログが公開されることを明示し、確認チェックを必須とした。
- `.NET` 版のお問い合わせは `product=net` と `public_issue_consent=true` を受付APIへ送り、返された `https://github.com/kuretoshi/BetterCrewLink_Net/issues/<番号>` のみを成功として扱う。作成されたIssueへのリンクを画面に表示する。
- サーバー側は `.NET` 版を `INQUIRY_GITHUB_REPO_NET=kuretoshi/BetterCrewLink_Net` に振り分ける。本家とは別のDiscordフォーラムへ転送し、返信・編集・削除・件名変更を対応する.NET版Issueへ同期する。
- 旧クライアントが公開確認を送らない場合は従来のDiscord直送を維持する。
- 通話プロトコル、ゲームメモリ読取、音声処理には公式3.2.28で変更なし。

## 確認

- .NET Release/x64ビルド、問い合わせ画面・送信契約の自己テスト、ネスト深さ上限3の検査。
- サーバー側の18件の単体テスト。NET向け公開Issue・Discord転送・コメント・件名変更をモックで確認。
- 実際のPi受付APIへの展開、.NETリポジトリに権限を持つGitHubトークン設定、IssueとDiscordの実送信・返信同期は公開前の必須確認。

