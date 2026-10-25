# Option backgrounds — 2026-10-25

Open select popups now paint authored option/optgroup background colors.
These elements have computed styles but no collapsed-select layout boxes;
painting only their text left light labels on the popup's white default.
Selection highlighting still paints above the authored row background.

Validation:

- GCC and Clang final builds: zero warnings; all 14 CTest suites pass.
  The added draw-output checks cover option/optgroup backgrounds and a live
  style change while open. An initial assertion confused the open-select
  boolean result with an error code; the failed logs are retained.
- Chrome hand layout oracle: 64/64 pass at 800×600, 1.5px ceiling, no gaps.
- Chrome 153.0.8010.54 reference confirms the authored dark option background
  and light text; the listbox render was inspected.
- Convoy/Agentic Vulkan at 1280×800: opened Foliage Lab's category dropdown,
  inspected both readable options and selected Bushes. The controls and
  3D preview changed together. Captured the corrected popup and bush preview.
- Agentic's final `agentic_weva` gate and doctests pass with this core and
  its new synthetic font variants. No Unity/Godot host or macOS claim.

Logs: `dropdown-build-{gcc,clang}-final.log`,
`dropdown-test-{gcc,clang}-final.log`, `dropdown-oracle.log`.
Chrome and live images: Convoy's `builds/test-evidence/weva-developer-ui/`
(`dropdown-chrome.png`, `final-dropdown-text.png`, `final-foliage-bush.png`).
The initial builds' filesystem clock warnings are retained separately; the
final builds are clean.
