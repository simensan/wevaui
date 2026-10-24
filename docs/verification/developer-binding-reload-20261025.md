# Developer document binding and reload

Nested keyed lists now retain surviving controls when their membership changes.
HTML reload expands the replacement with current binding data before matching
nodes, then transfers the replacement's binding expressions to retained nodes.
Edited values, focus and handles survive; new and removed rows follow the data.
Text-field change events on blur include the committed value.

The C ABI regression reloads nested tool/control templates three times, changes
their text bindings, adds a row, reorders rows and removes a row while retaining
the edited field's identity, value and focus. No ABI signature changed.

Validation on 2026-10-25: GCC and Clang builds completed without warnings;
both complete CTest runs passed 14/14 suites. Logs are retained in the task
worktree as `build-gcc.log`, `test-gcc.log`, `build-clang.log` and
`test-clang.log`. Agentic's desktop integration also exercises bound reloads.
Unity/Godot host suites and the full browser-oracle gate were not rerun for
this core binding change.
