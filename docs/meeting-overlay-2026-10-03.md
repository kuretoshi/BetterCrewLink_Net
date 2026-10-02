# 会議オーバーレイ配置の移植

基準: リリース3.2.7 `9861ccc8137bb63a7d3834f493be0b784a288544` の `src/renderer/views/Overlay.tsx`。

`MeetingOverlayLayout`を追加し、旧HUDの2列と現行HUDの3列を接続した。現行の先頭枠はcontainerのleft 0.4%にmargin-left 2.4%を加える。枠の高さはHUD高の10.5%×109%、行送りにはcontainer幅の1.9%を加える。従来の.NETは左余白を落とし、高さと行間を固定割合で近似していた。

旧HUDはcontainer幅88.45%、left4.7%、top18.4703%、枠幅46.41%、右余白2.34%、下余白2%を使用する。CSSの縦marginの%はcontainer幅基準。旧版の全体寸法は原版が`[hudWidth,hudWidth]`を返し、横長判定時にwindowWidth×0.96×854/579を使用する点もそのまま移植した。見た目の推測でwindowHeightへ変更していない。

検証:

- Releaseビルド: 警告/エラー0。
- `--overlay-self-test --cosmetics-self-test --voice-view-self-test`: 終了0。
- 独立した数値例で旧／現行の先頭・次列・次行の矩形と画面比率3分岐を検証。
- OverlayWindowの旧／現行切替で、計算した全体寸法・枠座標・枠寸法がWPF要素へ反映されることを検証。

未検証: 実ゲーム画面上の枠の一致、旧HUD対応ゲームでの実表示、DPI／解像度切替。原版のCSS box-shadow（blur+spread）と現在のWPF DropShadowEffect、400ms透明度遷移にも未移植差分がある。会議オーバーレイ全体の完全一致を意味しない。起動中のアプリは未更新。

## 会議枠の保持と400msフェード

毎回の更新でBorderを作り直す実装を変更し、会議中はプレイヤーIDごとに同じ枠を保持するようにした。発話状態が変わったときだけ現在の透明度から0/1へ400msで遷移する。曲線は原版の`transition: opacity 400ms`の既定ease（cubic-bezier .25,.1,.25,1）。同じ状態の再通知ではアニメーションを再開始しない。初期描画は原版の初回style同様に即時反映し、会議終了・表示無効化・次の会議開始で枠を破棄する。プレイヤーが状態から消えた場合はその枠を非発話へ移行させる。

Releaseビルド警告/エラー0。オーバーレイ・装備・名前テスト終了0。制御可能なWPFアニメーションクロックを進め、easeの中間値と400ms終点、同じVADでのクロック再利用を検証。OverlayWindowの更新間で同じ枠を保持し、会議間では別の枠になることも検証した。

未検証・残差: 実画面でのChromiumとの比較、フェード途中で反転した場合のCSS reversing-shortening挙動（現在は現在値から400msで再遷移）、CSS box-shadowのspread。完全なフェード互換とはまだ扱わない。起動中のアプリは未更新。

## フェード反転時の時間短縮

[CSS Transitions Level 1 §3](https://www.w3.org/TR/css-transitions-1/#starting)の反転規則を確認し、reversing-adjusted startとshortening factorを保持する処理を追加。進行中の反転では旧イージング出力×旧短縮係数＋(1−旧短縮係数)を0〜1に制限し、400msに乗じる。新しいフェードは反転時点の実透明度から始める。完了済みなら短縮係数1に戻し、到達値と新目標が一致する場合は遷移を解除する。

Releaseビルド警告/エラー0、オーバーレイ自動テスト終了0。WPF制御クロックで初回反転、連続反転、完了後の400ms復帰、進捗0での取消を検証。原版Chromiumとのフレーム単位の比較や実機表示は未検証。CSS box-shadowのspread差分も残る。起動中のアプリは未更新。

## 会議開始時スナップショットとライブ色

3.2.7のMeetingHudは参加者配列を会議開始時に固定するため、.NETもIDだけでなくclientId・colorId・isLocalを値として保持するよう変更した。死亡や参加者配列からの削除で枠を詰め直さず、会議中の新規参加者は次の会議まで追加しない。発話は固定clientIdに対する現時点の状態で更新する。これは前段の「消えたプレイヤーを必ず非発話にする」実装を原版へ合わせて置き換えるもの。

NoSのRGBだけは現在の同一IDのプレイヤーから取得する。公開値が失われた場合は開始時の色IDで現在のパレットを参照し、パレット項目もなければ原版の`#C51111`へフォールバックする。RGBのバイト丸めもJSのMath.round相当（0.5切上げ）に揃えた。

Releaseビルド警告/エラー0、`--overlay-self-test`終了0。開始時情報の固定、途中参加の除外、NoS色の更新と0.5バイト境界、退出後の枠保持と通常色への復帰、欠損パレットの赤フォールバックをWPF枠の色まで検証。実機比較は未実施で、起動中のアプリも未更新。

## CSS外側シャドウの広がりと内側除外

リリースコミット`9861ccc8137bb63a7d3834f493be0b784a288544`の`Overlay.tsx`を再確認。会議枠は`0 0 height/100px height/100px color`であり、従来の.NET側の半透明Borderに対するDropShadowEffect（opacity .95、blur最低5px、spreadなし）とは形状・濃度が異なっていた。

`MeetingBoxShadow`で角丸の不透明マスクをspread分拡張し、Gaussianぼかし後に元のborder-box内部を除外する実装へ変更した。[CSS Backgrounds 3 §6.1](https://www.w3.org/TR/css-backgrounds-3/#shadow-blur)に沿ってsigmaをblur radiusの半分とし、4σまでの分離可能カーネルで計算する。輪郭と除外形状は4×4サブピクセル標本化。角丸半径とspreadが等しい本会議枠専用であり、汎用CSSシャドウ実装ではない。

MeetingVoiceBorderは既存の400msフェードを維持し、枠線とは独立した影を先に描画する。描画寸法・DPI・色・半径が変わる場合にラスタを更新し、同じ条件の再描画では再利用。WPFの2px半透明枠線は維持する。

検証:

- Releaseビルド警告/エラー0。
- `--overlay-self-test --cosmetics-self-test --voice-view-self-test`終了0。
- 100/150/200%のラスタで外側の濃いspread、遠方への減衰、角丸形状、内部透明、premultiplied RGBAを画素検証。
- 実際のWPF RenderTargetBitmapで枠の外側がクリップされないこと、内部透明、同じラスタの再利用、色変更、リサイズを検証。

残項目: Chromiumの原版との画素比較、実ゲームの会議上での配置・DPI切替、4K・多人数・連続色変化時の性能。CSSの形状差分を修正した証拠であって、完全なピクセル一致を証明してはいない。起動中の発行済みクライアントはこの修正では更新していない。

実機試験は.NET連動先のNoS Airship非公開ロビー（4/15、COWFZU）を確認し、.NETの設定画面を開いた段階で、画面操作ヘルパーが`foreground window did not report a process id`を返した。設定ウィンドウ（2883820）を一覧から取り直して前面化・取得を再試行しても同じエラーとなったため、画面入力を停止した。設定値変更・ゲーム開始・クライアント再起動は行っていない。最後に確認できた.NET診断では3相手すべてdata-ready、受信16392/15605/15790 frame。会議オーバーレイの実機比較は未実施。
