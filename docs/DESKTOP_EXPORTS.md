# Desktop export verification

Verify the actual packaged addon on each target platform. The check creates a
fresh project, imports its artwork, exports debug/release/embedded-pack games,
then runs them after relocation without the source project.

The [September 15 review](verification/review-three-days-20260915.md) records
passing Linux exports and native Unicode checks with matching patched templates.
[Earlier Windows/Linux results](DESKTOP_EXPORTS_HISTORY.md) retain their own
build identities and scope.

## Reproduce

Install templates matching the exact editor build. For the complete native-text
checks, use the [qualified patched configuration](GODOT_TEXT_SHAPING.md#stock-godot-472-limitation)
with both debug and release templates. A patched editor alone does not fix
stock exported runtimes.

From the repository root, on each platform being shipped:

```sh
python3 hosts/godot/check_export.py --godot /path/to/godot \
    --addon /path/to/weva-preview.zip --native --keep
```

Use the console editor executable and `python` on Windows. `--keep` prints
the isolated fixture directory and preserves export/runtime logs. Add
`--render` to capture the example from the project and every native export,
then require identical PNG hashes. That mode needs a display with OpenGL 3;
its windows are positioned outside the desktop. The ordinary native checks
are headless.

`check.sh` includes native exports in its addon gate. Matching desktop
templates are required; missing templates fail that gate. Set
`WEVA_EXPORT_RENDER=1` to include the display-dependent pixel comparison.
Without `--native`, `check_export.py` retains its template-free project/PCK
check, but this does not prove native executable export.

## Scope

This proves the tested desktop configurations and representative addon
workflow. Other Godot versions, CPU architectures, export targets, renderers
and native build configurations need their own checks. The addon remains a
development preview while the interaction and conformance work in
[PRODUCT_READINESS.md](PRODUCT_READINESS.md) is unfinished.
