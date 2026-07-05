# Third-party notices

Weva (`com.wevaui`) is licensed under the **MIT License** (see [`LICENSE.md`](./LICENSE.md)).
It bundles and/or depends on the third-party assets listed below. Each retains
its own license; the full license text accompanies every redistributed asset at
the path noted. Bundling OFL/CC0 assets inside an MIT-licensed package is
permitted by those licenses.

## Fonts (bundled in the package — `Packages/com.wevaui/Runtime/Resources/Fonts/`)

| Font | Upstream | License | Notice |
|------|----------|---------|--------|
| **Weva-Default** (`Weva-Default*.ttf`) | [Inter](https://github.com/rsms/inter) by The Inter Project Authors | SIL OFL 1.1 | `Weva-Default-LICENSE.txt` |
| **Noto Color Emoji** (`NotoColorEmoji.ttf`) | [Noto](https://github.com/googlefonts/noto-emoji) by Google Inc. | SIL OFL 1.1 | `NotoColorEmoji-LICENSE.txt` |
| **Noto Sans Symbols 2** (`NotoSansSymbols2-Regular.ttf`) | [Noto](https://github.com/notofonts/symbols) by The Noto Project Authors | SIL OFL 1.1 | `NotoSansSymbols2-LICENSE.txt` |

Inter declares **no Reserved Font Name**, so its redistribution under the
`Weva-Default` name is permitted by OFL §3. The fonts themselves remain under
the OFL; only the surrounding engine code is MIT.

## Fonts (demo project only — `Assets/UI/Fonts/`, not part of the shipped package)

| Font | Upstream | License | Notice |
|------|----------|---------|--------|
| **Patrick Hand** (`PatrickHand-Regular.ttf`) | Patrick Wagesreiter | SIL OFL 1.1 | `PatrickHand-Regular-OFL.txt` |
| **Sniglet** (`Sniglet-ExtraBold.ttf`) | Haley Fiege, Brenda Gallo, Pablo Impallari | SIL OFL 1.1 | `Sniglet-ExtraBold-OFL.txt` |
| **Liberation Sans** (`Assets/TextMesh Pro/Fonts/LiberationSans.ttf`) | Red Hat / TextMesh Pro essentials | SIL OFL 1.1 | `LiberationSans - OFL.txt` |

## Images / sprites (demo project only — `Assets/UI/Sprites/`)

| Asset | Upstream | License |
|-------|----------|---------|
| `ButtonFrame.png`, `ButtonFrameHover.png`, `PanelFrame.png` | [Kenney UI Pack](https://kenney.nl/assets/ui-pack) | CC0 1.0 (public domain) |

See `Assets/UI/Sprites/README.md` for the per-file source mapping.

## Binaries (`Assets/Plugins/Roslyn/`)

| Binary | Upstream | License | Notice |
|--------|----------|---------|--------|
| `Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`, `System.Collections.Immutable.dll`, `System.Reflection.Metadata.dll` | [.NET Foundation](https://github.com/dotnet/roslyn) (Roslyn / .NET runtime) | MIT | `Assets/Plugins/Roslyn/LICENSE.txt` |

`Packages/com.wevaui/Runtime/Generators/Weva.Generators.dll` is built from this
repository's own source (`Tools/Weva.Generators/`) and is covered by the
project's MIT license.

## Package dependencies (fetched via Unity Package Manager — not redistributed)

The package declares Unity first-party dependencies in
`Packages/com.wevaui/package.json` (URP, Input System, uGUI, Burst). These are
downloaded by UPM under their own licenses (Unity Companion License / package
manifests) and are not redistributed in this repository.
