# Avatar source assets

`player.png`, `ghost.png`, `rainbow-alive.png`, and `rainbow-dead.png` are copied without modification from [TanukiBCL v3.2.7](https://github.com/kuretoshi/TanukiBCL/tree/9861ccc8137bb63a7d3834f493be0b784a288544/static/images). The first two are recolored at runtime using the algorithm in that release's `src/main/avatarGenerator.ts`; this allows the game-reported palette, including custom MOD colors, to drive the .NET view. TanukiBCL is distributed under GPL-3.0; the license text is at the repository root.

The status badge vector paths in `PlayerAvatar.xaml.cs` come from the Material UI `@mui/icons-material` package used by the same upstream release. Its MIT license text is in `licenses/MUI-icons-MIT.txt`.
