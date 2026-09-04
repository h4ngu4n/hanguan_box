# AGENTS.md

Single-project WPF app (.NET 10, `net10.0-windows`, Windows-only): 寒竿工具箱 — a Minecraft launcher helper (launcher download + `MCL.core.dll` hook injection). No tests, no CI, no lint/format config.

## Build & verify

- `dotnet build HanguanBox.csproj` is the only verification available. Requires the .NET 10 SDK.
- Solution file is `HanguanBox.slnx` (XML solution format). No NuGet package references — pure WPF + P/Invoke.

## Gotchas

- **`bin/` and `obj/` are tracked in git (there is no .gitignore).** Every build regenerates `.g.cs` files under `obj/`; never stage them when committing.
- `Assets\MCL.core.dll` is embedded into the assembly via `EmbeddedResource` with `LogicalName="MCL.core.dll"` (HanguanBox.csproj:16). The `Condition="Exists(...)"` means a missing file builds fine but injection fails at runtime with 缺少 DLL (fallback: loose `MCL.core.dll` next to the exe).
- Injection status is decided purely by MD5 comparison between the embedded DLL and the copy in the launcher dir (Helpers/McLauncher.cs) — no flag file. To ship a new hook DLL, just replace `Assets\MCL.core.dll`.

## Architecture

- `MainWindow` is a borderless custom-chrome shell. Navigation = RadioButton `Tag` string → view instance in the `_views` dictionary built in the ctor (MainWindow.xaml.cs). Wired pages: `mclauncher`, `hook`.
- `Views/HomeView|DataView|SettingsView` are scaffolded but **not wired into navigation**.
- Shared colors/styles (`TextPrimary`, `AccentGrad`, `Card`, `PrimaryBtn`, `GhostBtn`) live in App.xaml resources — reuse them via `StaticResource` instead of redeclaring.
- Chrome/blur wiring is interlocked: `WindowStyle=None` + `AllowsTransparency`, acrylic blur via DWM `SetWindowCompositionAttribute` (Helpers/BlurBackground.cs), and a WndProc hook in MainWindow that downgrades to cheap blur during drag/resize and clamps maximized bounds via `WM_GETMINMAXINFO`. Preserve this when touching chrome, blur, or maximize behavior.

## Conventions

- Code comments and all UI strings are in Chinese — follow that style.
- Icon glyphs are `Segoe MDL2 Assets` font characters (e.g. `&#xE922;`), not image assets.
