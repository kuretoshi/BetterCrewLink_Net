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
