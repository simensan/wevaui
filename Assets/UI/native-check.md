# native-check.html: looking at the core's recent work in Unity

> **Historical render review, September 11.** The missing Unity font fallback
> and shaping described below were subsequently changed. Use the current
> [font guide](../../Packages/com.wevaui/Documentation~/text-and-fonts.md) for setup
> and the [review report](../../docs/verification/review-three-days-20260915.md) for later evidence.

`native-check.html` + `native-check.css` (this folder) exercise, one block each:
safe-area insets, `direction`/`unicode-bidi`, a `local()` font source, the
individual transform properties, the intrinsic sizing keywords, component-scoped
`<style>`, and `scroll-behavior: smooth`. Every block says what it should look
like.

## What was already looked at (2026-09-11, headless)

The page was drawn by the Unity Native offscreen path (the EditMode test
`Parity_RendersPagesForComparison` with `WEVA_NATIVE_PARITY` pointing at a copy
of the page; images in `.utmp/native-check*/unity.png`). Seen in those renders:

- Transforms, sizing keywords, scoped pills, the smooth-scroll list and the
  Arial `local()` line all render as described.
- **Hebrew does not render on Unity.** The bundled UI face and the
  NotoSansSymbols2 fallback carry no Hebrew, and there is no OS fallback yet, so
  the Hebrew words in block 2 are blank. The run ORDER is right (the Latin word
  sits at the right end of the rtl line, the override span reverses); the
  glyphs need a face that has them. Set `WevaNativeDocument.Fallbacks` to a font
  with Hebrew/Arabic coverage, or declare `@font-face { src: local("Arial") }`
  for the paragraph, to see them. Godot's theme font covers Hebrew and the
  same page renders the words there.
- Even with a Hebrew face, TextCore does not shape right-to-left inside a word:
  the letters of a Hebrew word come out in logical order on Unity (PORT_PLAN
  item 15 records this). TextServer on Godot gets it right.
- Weva's UA sheet sizes `html`/`body` to `100%` of the viewport (content-box);
  padding on them needs `box-sizing: border-box`, which the sample uses.

## Through the real pipeline (2026-09-11, headless Play mode)

`NativeGameViewCaptureTests` (Tests/Runtime/RenderGoldens, `[Explicit]`, run with
`-testPlatform PlayMode -testFilter NativeGameViewCaptureTests`) puts a
`WevaNativeDocument` with this page in front of a camera, draws it through the
project's URP renderer and writes `.utmp/native-gameview/*.png`, plus the C#
engine drawing the same page as a control. The first run came out black: the
renderer's active feature is `UIBatchedRendererFeature`, whose pass never drew
native sources -- only the inactive legacy `UIRendererFeature` did. Fixed the
same day (the batched pass draws native documents before its own batches);
the captures then match the offscreen ones, and a live `SetSafeAreaInsets(44,
0, 0, 8)` shows the padding in block 1.

## The live-scene pass (Game view), for you

1. Open `Assets/uitest.unity` (or any scene with the URP `UIRenderPass`), add
   an empty object with `WevaNativeDocument`, and assign `Html` =
   `native-check.html`, `Css` = `native-check.css`, `BasePath` =
   `Assets/UI`. Enter Play mode.
2. Block 1: tick `FollowScreenSafeArea` in the Inspector. On a desktop nothing
   changes (no insets). In the Device Simulator with a notched phone, the tinted
   padding appears above and left of the text. From a script,
   `GetComponent<WevaNativeDocument>().Document.SetSafeAreaInsets(44, 0, 0, 8)`
   pads it directly.
3. Block 2: the rtl line is right-aligned with `abc` at the right end; the
   override span reads `def abc`. Hebrew glyphs need a fallback face (above).
4. Block 3: the first line is Arial (wider, different `Q` and `a`); the second
   is the UI face.
5. Block 4: pink square shifted right by half its width, green diamond, blue
   double-width square.
6. Block 5: the first bar hugs its text and is centred; the second is the width
   of `narrowest`.
7. Block 6: two blue pills; `not a pill` after them is plain yellow text.
8. Block 7: wheel over the list moves it at once; calling
   `Document.SetElementScroll(list, 0, 100)` from a script eases over a quarter second
   (`Document.IsAnimating` reports true meanwhile).

If any block disagrees with its note, that is a finding: say which block and what
you saw.
