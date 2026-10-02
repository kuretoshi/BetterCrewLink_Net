# オーバーレイの人数連動サイズと背景

参照はリリース3.2.7コミット`9861ccc8137bb63a7d3834f493be0b784a288544`の`src/renderer/views/Overlay.tsx`と`src/renderer/css/overlay.css`。作業開始時の.NETは`a7fe716`。

## 修正

- 左右アイコンの72px固定上限を撤去。原版の`--size: 7.5 * (10 / avatars.length) vh`、`max-width: 7.5vh`に合わせ、`0.075 * viewportHeight * min(1, 10 / renderedCount)`とした。
- 人数は接続・死亡・変装・compact VADの選択後の描画人数を用いる。非表示プレイヤーを分母にしない。上下系の60pxは維持。
- 通常の左右背景を透明化。上側は半透明黒（alpha 0.5）、左下は半透明黒（alpha 0.35）とし、従来の紫灰色を除去。
- compact左右の背景は外側wrapperとは別の内側Borderへ配置し、原版の`#25232ac0`を使用。画面端に接する側を角丸にせず、反対側だけ25pxの角丸にする。left1/right1も同じcompactスタイル。

## 検証

Releaseビルド警告/エラー0。`--overlay-self-test --cosmetics-self-test --voice-view-self-test`を同時実行し終了0。

追加テストはWPFの実生成要素を検査。720px/4人=54px、1080px/10人=81px、1080px/15人=54px、2160px/15人=108pxを固定期待値で確認。15人在室でもcompactで5人のみ発話する場合は81pxとなる。left/left1/right/right1/top/bottom_leftとcompact ON/OFFの組合せで背景レイヤー・色・角丸を検査する。既存の会議枠・装備・名前表示テストも成功。

## 残差・実機状態

この変更はオーバーレイ全体のピクセル一致を証明しない。原版CSSに対する余白、左右の名前とアイコンの配置、compact名札の絶対位置と400msフェード、折返し、フォントなどの差分は引き続き残る。特に原版の内側コンテナpaddingとwrapper寸法は今回一括変更していない。

本ターンでは画面操作・設定変更・発行済みアプリの再起動を行っていない。起動中のクライアントは未更新。実ゲームの会議・DPI・画面サイズ変更での公式版との比較も未実施。

## left1/right1の名前フェードと表示要素の保持

原版`Overlay.tsx`は左右の名前要素を発話停止時にも残し、`left1/right1`でopacityを0/1へ変更する。`overlay.css`はそのopacityに400ms transitionを指定している。従来の.NETは表示更新ごとに行とアバターを作り直し、発話停止時に名前要素そのものを削除していた。これではフェードが起きず、装備画像の非同期読込も毎回やり直しになり得る。

プレイヤーIDごとにアバター・行・名前欄を保持し、必要な行だけ追加・削除・並べ替えするよう変更した。`left1/right1`では名前要素を保持したままopacityを0/1へ400msで変更する。easingは原版のCSSデフォルトease、途中反転はCSSのreversing shorteningに合わせた。通常の左右では常時表示し、名前を使わない位置・変装中は名前欄を取り外す。

Releaseビルド警告/エラー0。`--overlay-self-test --cosmetics-self-test --voice-view-self-test`終了0。制御可能なアニメーションクロックで200msの中間値、同じ発話状態でクロックを再作成しないこと、途中反転時の短縮時間と終端値を検査。実際のOverlayWindow更新で、無言になっても名前要素とアバターが同一参照で保持され、上下位置へ切り替えると名前欄が外れることを検査。

位置と余白、右側のフロート順、字体・装飾、実ゲーム画面とChromium上の画素比較はまだ必要。発行済み実行ファイルはこのコードへ更新していない。
