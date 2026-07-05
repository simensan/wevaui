# Figma Import Demo

`hero-card.figma.json` is a sample export (the JSON the Figma plugin produces for
a small "hero card" frame). Import it to see the bridge in action:

**Assets ▸ Weva ▸ Import Figma JSON…** → pick `hero-card.figma.json`.

It writes `hero-card.html` + `hero-card.css` next to the JSON. The frame
exercises:

- vertical Auto Layout → `display:flex; flex-direction:column; gap:12px; padding:20px`
- a **bound title** (layer `Title {{ HeroName }}`) → `<span>{{ HeroName }}</span>`
- an uppercased, letter-spaced subtitle
- a **play button** (layer `Play <button> @click=OnPlay #play`) →
  `<button id="play" on-click="OnPlay">`, full-width via Fill on the cross axis
  (`align-self:stretch`)

Wire it up by pointing a `UIDocument` at `hero-card.html` with a controller that
exposes `HeroName` (a `[UIBind]` field) and an `OnPlay()` method.

The generated output is guarded by `HeroCardDemoTests` so it can't silently
drift.
