# Texture replacement lifetime audit — 2026-10-31

Repeated resize and HTML reload cycles retain bounded texture/material counts
and display the replacement pixels in both Unity and Godot. This pass adds
regressions; it changes no production behavior and makes no speedup claim.

## Coverage

Both tests exercise 24 distinct constant-color gradient textures, alternating
the document viewport width and reloading HTML every fourth iteration. Each
sequence runs with normal blending and multiply blending. Resizing explicitly
invalidates the core's raster texture cache, so this exercises retirement and
replacement rather than just revisiting already-cached images.

Unity checks the rendered center pixel, exactly one retained texture, a bounded
material count, settled upload reuse, and zero renderer texture/material entries
after disposal. Multiply intentionally owns two materials: a normal base plus
its blend variant. The initial probe incorrectly expected one; its failed XML
and log remain alongside the corrected run.

Godot checks one published core texture per iteration and, with rendering
enabled, the replacement pixel. Its texture map mirrors the published list by
swapping out retired references; blend materials are shared by blend mode, not
allocated per texture. The new scene is included in the headless host runner.
Godot's assertions do not expose private host resource counts. Neither test
proves long-duration native/GPU memory stability or covers arbitrary image-cache
growth, asynchronous destruction in a Unity player, or full inventory mutation.

## Verification

- Unity 6000.4.1f1 native renderer EditMode class: **14 passed, 1 existing
  environment-gated inconclusive**, zero failures. Both new cases pass.
- Godot 4.7 Windows, Vulkan Forward Mobile, RTX 5080: **96 checks, zero failures**.
- Godot Windows headless: **48 checks, zero failures**.
- Python compilation of the host runner and `git diff --check` pass.

Evidence under `.utmp/render-audit-20261030/`:
`texture-lifetime-v2.xml`, `texture-lifetime-v2.log`,
`godot-texture-lifetime.log`, `godot-texture-lifetime-headless.log`.
The initial expectation failure is in `texture-lifetime.xml` and
`texture-lifetime.log`. The adjacent JSON records accepted evidence hashes.

No host rebuild was needed because only tests and runner registration changed.
The complete local gate, full Unity suite, and player rendering suite were not
rerun; their earlier counts are not asserted as results for this audit.

Next performance target: measure the complete inventory selection mutation
sequence (details, icon, actions and slot classes), rather than extrapolating
from the existing quantity/highlight-only Unity timing workload.
