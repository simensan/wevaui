# Samples

[← Back to index](index.md)

## Start here: the Phase One Demo (shipped in the package)

The **Phase One Demo** is the canonical, end-to-end example and the one that
ships *inside* the package. Import it via **Package Manager → Weva → Samples**;
it lands under `Assets/Samples/Weva/<version>/Phase One Demo/`. It is a complete
scene — `WevaDocument` + controller + HTML/CSS + a reusable component — that
exercises controller binding, events, and hot reload. Read its `README.md` and
`Scripts/PhaseOneDemoController.cs` first.

This is the only sample set a **package consumer** gets. Everything in the
table below lives in the **repo's** `Assets/UI/` folder and is **not** part of
the distributed package — clone the repo if you want to browse them.

## Repo sample pages (`Assets/UI/`, repo checkout only)

The repo ships ~30 sample pages under `Assets/UI/` (each an `.html` + `.css`
pair). They double as the corpus for the layout-vs-Chrome audit and the
golden/perf calibration, and are game-UI themed — mostly a fictional RPG
("Halcyon"/"Ravenmoor") and a match-3 ("Sweet Cascade").

Many are real exercises of specific subsystems; the one-liners below note what
each primarily stresses. Per-sample layout deviation vs. headless Chrome is
tracked in the project's sample-audit.

## Reference / playground

| Sample | Exercises |
|---|---|
| `flex-playground.html` | Flexbox: nested columns, grow/shrink, cross-stretch reflow, auto margins. |
| `grid-playground.html` | Grid: templates, named areas, auto-placement, `align-content`, `min-height` floors. |
| `9slice-demo.html` | 9-slice `<img>` and CSS `border-image` from sprite borders. |
| `inputtest.html` | Form controls + focus/keyboard nav (paired with `inputtest.unity`). |
| `randhtml.html` | The dev demo the golden tests + perf benches calibrate against — HUD / quest log / chat / map widgets in one page. |
| `weva-landing.html` | Marketing-style landing page (gradient text, code blocks, flex layout). |

## HUD / in-game overlays

| Sample | Exercises |
|---|---|
| `hud.html` | Status bars (`%` widths), level ring, vitals — classic game HUD. |
| `combat-hud.html` | Centered ability bar, action-row positioning. |
| `match3.html` / `match3-endgame.html` | Match-3 board and level-complete screen; inline reward shells. |
| `story-bubble.html` | Dialogue bubble with `filter: drop-shadow` + `text-shadow`. |
| `dialogue.html` / `nook-dialogue.html` | NPC dialogue panels with portraits. |

## Screens / menus

| Sample | Exercises |
|---|---|
| `menu.html` | Main-menu list (line-height / typography heavy). |
| `load-game.html` | Save-slot grid with rounded/elliptical corners. |
| `level-select.html` | Level map with rotated connector lines (transforms on inline elements). |
| `settings.html` | Settings form: toggles, sliders, sections. |
| `inventory.html` | Inventory grid inside a `100vh` flex container (grid height containment). |
| `vendor.html` | Trader stall: item grid with emoji glyphs. |
| `quests.html` | Quest log list. |
| `stats.html` | Character sheet with an aspect-ratio gear grid. |
| `episode-stats.html` | Run-summary stats with flex cross-stretch. |
| `leaderboard.html` | Season leaderboard table/rows. |
| `map.html` | World map with a footer travel-cost row. |
| `todo.html` | Minimal todo list (good first read). |

## Dashboards

| Sample | Exercises |
|---|---|
| `stock-dashboard.html` | Dense flex→grid→flex chain (intrinsic-sizing stress). |
| `advanced-dashboard.html` | Multi-panel dashboard with `backdrop-filter` and height containment. |

## Notes

- Several samples render with a uniform vertical offset vs. Chrome — that is the
  **intentional default-face** divergence (the bundled Inter face's metrics
  differ slightly from Chrome's Arial; `font-drift` in the audit), not a layout
  bug. See [Text & Fonts](text-and-fonts.md).
- Samples whose layout still diverges structurally are the ranked backlog in
  the project's sample-audit (grid height containment, message-bubble
  shrink-to-fit, inline transforms, etc.).

---

Next: [Text & Fonts](text-and-fonts.md)
