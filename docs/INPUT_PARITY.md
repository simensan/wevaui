# Input parity: Godot host vs Unity host

The two hosts translate their engine's input into the same C ABI calls
(`weva_document_set_pointer`, `_key`, `_text_input`, `_scroll`,
`_set_composition`, `_focus_move`, …); everything the two hosts must agree
on beyond the translation lives in the core, tested once. This table is the
Phase 4.2 gate: every row names the decision, where it is made, and the test
that pins it on each host. Rows marked *core* are decided by `libweva` and
covered by `libweva/tests`; the host tests there show the host opted in.

| Behaviour | Decided | Godot host (`hosts/godot`) | Unity host (`Packages/com.wevaui`) |
|---|---|---|---|
| Wheel notch = 100 CSS px (Chrome's `WHEEL_DELTA`) | host, both 100 since 2026-09-13 | `weva_node.cpp` `step = 100 * factor`; `input_integration_tests.gd` "one wheel notch scrolls Chrome's 100px" | `NativeInputFeed.WheelLine = 100`; `NativeInputFeedTests.WheelNotchScrollsWhatChromeScrolls` |
| Wheel scrolls the container under the pointer | core | `input_integration_tests.gd` wheel case | `NativeInputTests.Wheel_ScrollsTheContainerUnderThePointer_AndReportsIt` |
| Held key auto-repeats (0.5 s delay, then interval) | core, ABI 39, opt-in; Godot uses the OS echo instead | OS `echo` events; `input_integration_tests.gd` "held Backspace repeats" | `SetKeyRepeat(0.5, 0.03)`; `NativeInputFeedTests.HeldKeyRepeatsThroughTheCoreOnTheInputClock`; `test_c_abi_input_repeat.cpp` |
| Double-click selects the word (0.5 s, 4 px) | core, ABI 39, opt-in; Godot uses the OS double-click flag | `weva_node.cpp` `is_double_click()` → `select_word_at`; `render_tests.gd` word selection | `SetDoubleClick(0.5, 4)`; `NativeInputFeedTests.DoubleClickSelectsTheWordUnderIt`; `test_c_abi_input_repeat.cpp` |
| Pointer leaving the surface clears hover | host | `weva_node.cpp` mouse-exit → `clear_pointer`; `hover_tests.gd` | `NativeInputFeed` on surface exit; `NativeInputFeedTests.PointerLeavingTheSurfaceClearsHover` |
| Losing application focus clears hover, takes no keys | host | `weva_node.cpp` focus-out; `input_integration_tests.gd` "freeing a focused scene releases native focus" | `NativeInputFeed.HasFocus`; `NativeInputFeedTests.LosingApplicationFocusClearsHoverAndTakesNoKeys` |
| Copy/paste/undo chord is Cmd on macOS, Ctrl elsewhere | host | `weva_node.cpp` `is_command_or_control_pressed()` | `NativeInputFeed.CommandIsMeta`; `NativeInputFeedTests.CommandChordFollowsThePlatform` |
| Shortcut chords are not typed as text (Ctrl+A, Ctrl+Z) | core | `input_integration_tests.gd` "Ctrl+A is not inserted as text", "Ctrl+Z undoes typing without inserting z" | `NativeInputTests.Editing_BackspaceUndoRedo_AndShortcutsAreNotText` |
| Unicode text input | core | `input_integration_tests.gd` "Unicode text crosses the native event path" | `NativeInputTests.Form_SubmitsImplicitly_AndUnicodeTypes` |
| Keyboard can be kept for the game while the pointer works | host | `noninteractive` / `input_integration_tests.gd` "noninteractive documents let input reach the UI beneath" | `NativeInputFeed.AcceptsKeyboard`; `NativeInputFeedTests.AcceptsKeyboardGatesKeysAndTextButNotThePointer` |
| Consumed input is reported so gameplay can ignore it | host | `set_input_as_handled`; `input_integration_tests.gd` "accepted typing stays out of game input handlers" | `NativeInputFeed.Consumed` / `WevaDocument.InputConsumed`; every `NativeInputFeedTests` case asserts it |
| Tab order wraps inside the document unless the host lets it out | core (`focus_step(wrap)`) + host | `input_integration_tests.gd` "Tab continues into the next document", "Shift+Tab enters a document at its last control" | `NativeInputFeed.WrapTab` / `TabbedOut`; `NativeInputTests.Focus_TabOrderWrapsOnlyWhenAsked` |
| Touch: tap clicks, drag past a threshold pans | host | `weva_node.cpp` `InputEventScreenDrag` (a tap is Godot's emulated mouse click); `input_integration_tests.gd` "a finger dragging up 60px pans the list down 60px" | `NativeInputFeed.TouchPanThreshold = 8`; `NativeInputFeedTests.TouchTapClicksAndTouchDragPans` |
| Gamepad: d-pad / left stick move focus by geometry | core (`focus_move`) + host mapping | `gamepad_navigation_tests.gd` "D-pad right moves focus to the button beside it", "… down moves to the button below, not the next in source order", "the left stick moves focus through ui_right" | `NativeInputFeed.Directions`; `NativeInputFeedTests.GamepadMovesFocusByGeometryAndRepeatsWhileHeld` |
| Gamepad: a direction a control owns is its key first (slider, caret, select) | core | `gamepad_navigation_tests.gd` "right on a slider steps its value", "right in a text field moves the caret, not the focus", "down on a closed select changes its option" | same test (`Left`/`Right` land on the slider) |
| Gamepad: held direction repeats (0.4 s, then 0.1 s) | host | `gamepad_navigation_tests.gd` "no repeat before the delay", "holding the pad repeats after the delay", "release stops the repeat" | `NavigationRepeatDelay/Interval`; `NativeInputFeedTests.GamepadMovesFocusByGeometryAndRepeatsWhileHeld` |
| Gamepad: South accepts (Enter), East cancels (Escape), shoulders step the tab order | host | `gamepad_navigation_tests.gd` "accept clicks the focused button", "accept toggles a checkbox", "a cancel that closed something is consumed" | `NativeInputFeedTests.GamepadSouthActivatesAndShouldersStepTheTabOrder` |
| Gamepad: accept on a text field asks the host for a keyboard | host, opt-in | `gamepad_navigation_tests.gd` "accept in a text field asks for text entry instead of submitting", "with text entry off, accept in a form field submits like Enter" | `NativeInputFeed.GamepadTextEntry` / `TextEntryRequested`; `NativeInputFeedTests.GamepadAcceptOnATextFieldAsksTheHostForAKeyboard` |
| Gamepad: a pad press does not wake an unfocused document unless asked | host | `gamepad_wake`; `gamepad_navigation_tests.gd` "without gamepad_wake a pad press leaves an unfocused document alone" | not offered: the Unity feed always routes the pad to the document while `AutoInput` is on (a game gates it with `AutoInput`) |
| IME: preedit shows, commit delivers the text once, never also typed | core + host | `ime_integration_tests.gd` "commit replaces the preedit once", "committed text is delivered once" | `NativeInputFeed.OnImeComposition`; `NativeInputFeedTests.ImeCompositionShowsThenCommitsWithoutTypingTwice`; `NativeInputTests.Composition_PreeditThenCommit` |
| IME: the candidate window sits at the caret, only over a text control | host | `ime_integration_tests.gd` "editable input exposes its caret rectangle", "candidate coordinates include the document, CanvasLayer and viewport container" | `NativeInputFeed.SyncIme` (`Keyboard.SetIMECursorPosition`); the feed test asserts IME enabled only with a text target |
| Enter activates a button on key down and repeats; Space on release | core | `dialog_cancel_tests.gd`, `form_state_tests.gd` | `NativeInputTests.Enter_ActivatesAButtonOnKeyDown_AndRepeats`, `Space_ActivatesAButtonOnRelease_AndTheKeyIsConsumed` |
| Secondary button raises `contextmenu` | core | `input_integration_tests.gd` (dropdown/popover routing) | `NativeInputTests.Pointer_ContextMenuOnSecondaryPress` |
| `pointer-events: none` lets hit testing pass | core | `input_integration_tests.gd` "CSS pointer-events:none lets native GUI hit testing continue beneath" | `NativeInputTests.PointerEventsNone_LetsHitTestingPass` |
| Only the top overlapping document takes the pointer where it accepts it -- everywhere by default, as in a browser; a HUD lets clicks through with `html, body { pointer-events: none }` and `auto` on its controls | host | `input_integration_tests.gd` "only the top overlapping document activates", "noninteractive documents let input reach the UI beneath" | `NativeInputFeed.CoveredAt` over every live feed by `Order` (= `SortingOrder`, then creation order), mouse and touch; `NativeInputFeedTests.OnlyTheTopDocumentTakesThePointer_WhereItAcceptsIt` |
| Timed gestures run on unscaled time so a paused game's menu works | host | monotonic clock in `weva_node.cpp` | `WevaDocument.Update` feeds `Time.unscaledDeltaTime` as the input clock |

Every row is pinned on both hosts (the last two were pinned 2026-09-14:
Godot's touch pan by a scene check, Unity's multi-document pointer
arbitration by implementing it -- a feed had fed the pointer to its document
regardless of what was painted over it -- and testing it).
