using System.Linq;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Forms.Ime;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class InputControllerTests {
        sealed class TrivialHit : IHitTester {
            readonly Element only;
            public TrivialHit(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        static (Document doc, Element input, EventDispatcher d) Setup(string html) {
            var doc = HtmlParser.Parse(html);
            var input = doc.GetElementsByTagName("input").FirstOrDefault()
                        ?? doc.GetElementsByTagName("textarea").First();
            var d = new EventDispatcher(doc, new TrivialHit(input), new FakeUIClock());
            d.Focus(input);
            return (doc, input, d);
        }

        [Test]
        public void Typing_text_inserts_into_value_via_attribute() {
            var (_, input, d) = Setup("<input value=\"\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchTextInput("h");
            d.DispatchTextInput("i");
            Assert.That(input.GetAttribute("value"), Is.EqualTo("hi"));
            Assert.That(ctrl.Model.Text, Is.EqualTo("hi"));
        }

        [Test]
        public void Typing_marks_control_for_user_validation_pseudos() {
            var (_, input, d) = Setup("<input type=\"email\" required value=\"\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchTextInput("bad");

            Assert.That((d.StateProvider.GetState(input) & ElementState.UserInteracted) != 0, Is.True);
            Assert.That(SelectorMatcher.Matches(SelectorParser.Parse(":user-invalid"), input, d.StateProvider), Is.True);
        }

        [Test]
        public void Backspace_deletes_one_char() {
            var (_, input, d) = Setup("<input value=\"abc\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            // Caret is at end after model construction (no select-all on focus path here since already focused).
            ctrl.Model.SetCaret(3);
            d.DispatchKeyDown("Backspace", "Backspace", KeyModifiers.None, false);
            Assert.That(input.GetAttribute("value"), Is.EqualTo("ab"));
        }

        [Test]
        public void Enter_in_singleline_does_not_insert_newline() {
            var (_, input, d) = Setup("<input value=\"abc\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(3);
            int commits = 0;
            ctrl.ValueCommitted += () => commits++;
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("abc"));
            Assert.That(commits, Is.EqualTo(1));
        }

        [Test]
        public void Enter_in_textarea_inserts_newline() {
            var (_, ta, d) = Setup("<textarea>abc</textarea>");
            var ctrl = new InputController(ta, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(ctrl.Model.Text.Length);
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("abc\n"));
            Assert.That(ta.GetAttribute("value"), Is.EqualTo("abc\n"));
        }

        [Test]
        public void Focusing_textarea_preserves_child_text_default() {
            var doc = HtmlParser.Parse("<textarea id=\"t\">abc</textarea>");
            var ta = doc.GetElementById("t");
            var d = new EventDispatcher(doc, new TrivialHit(ta), new FakeUIClock());
            var ctrl = new InputController(ta, d);
            ctrl.Wire();

            d.Focus(ta);

            Assert.That(ctrl.Model.Text, Is.EqualTo("abc"));
            Assert.That(ta.GetAttribute("value"), Is.Null);
        }

        [Test]
        public void Arrow_left_right_move_caret() {
            var (_, input, d) = Setup("<input value=\"abc\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(2);
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, false);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(1));
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, false);
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, false);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(3));
        }

        [Test]
        public void Tab_advances_focus_default_unblocked() {
            var doc = HtmlParser.Parse("<input id=\"a\" value=\"\"><input id=\"b\" value=\"\">");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new TrivialHit(a), new FakeUIClock());
            d.Focus(a);
            var ctrl = new InputController(a, d);
            ctrl.Wire();
            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.None, false);
            Assert.That(d.FocusedElement, Is.EqualTo(b));
        }

        [Test]
        public void Disabled_input_ignores_text_input() {
            var (_, input, d) = Setup("<input value=\"\" disabled>");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchTextInput("x");
            Assert.That(input.GetAttribute("value"), Is.EqualTo(""));
        }

        [Test]
        public void ReadOnly_input_ignores_text_input_but_allows_caret_move() {
            var (_, input, d) = Setup("<input value=\"abc\" readonly>");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(3);
            d.DispatchTextInput("x");
            Assert.That(ctrl.Model.Text, Is.EqualTo("abc"));
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, false);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(2));
        }

        [Test]
        public void Checkbox_click_toggles_checked() {
            var (_, input, d) = Setup("<input type=\"checkbox\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(input.HasAttribute("checked"), Is.True);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(input.HasAttribute("checked"), Is.False);
        }

        [Test]
        public void Radio_click_unchecks_others_in_same_name_group() {
            var doc = HtmlParser.Parse("<form><input type=\"radio\" name=\"g\" id=\"r1\" checked><input type=\"radio\" name=\"g\" id=\"r2\"></form>");
            var r1 = doc.GetElementById("r1");
            var r2 = doc.GetElementById("r2");
            var d = new EventDispatcher(doc, new TrivialHit(r2), new FakeUIClock());
            d.Focus(r2);
            var ctrl2 = new InputController(r2, d);
            ctrl2.Wire();
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(r2.HasAttribute("checked"), Is.True);
            Assert.That(r1.HasAttribute("checked"), Is.False);
        }

        [Test]
        public void IME_committed_text_is_inserted() {
            var (_, input, d) = Setup("<input value=\"ab\">");
            var ime = new ImeSession();
            var ctrl = new InputController(input, d, ime);
            ctrl.Wire();
            ctrl.Model.SetCaret(2);
            ime.BeginComposition();
            ime.UpdateCompositionString("X");
            ime.CommitComposition("XY");
            Assert.That(ctrl.Model.Text, Is.EqualTo("abXY"));
            Assert.That(input.GetAttribute("value"), Is.EqualTo("abXY"));
        }

        [Test]
        public void Click_on_label_with_for_toggles_referenced_checkbox() {
            // Simulates the menu.html `card-form` interaction:
            //   <input type="checkbox" id="cb1">
            //   <label for="cb1">Subscribe</label>
            // Clicking the label should toggle the input's checked attribute.
            var doc = HtmlParser.Parse(
                "<input type=\"checkbox\" id=\"cb1\">" +
                "<label for=\"cb1\">Subscribe</label>");
            var cb = doc.GetElementById("cb1");
            var label = doc.GetElementsByTagName("label").First();
            // The hit-tester returns the label so the click goes to the label,
            // not the input.
            var d = new EventDispatcher(doc, new TrivialHit(label), new FakeUIClock());
            var ctrl = new InputController(cb, d);
            ctrl.Wire();
            var labelCtrl = new LabelController(label, d);
            labelCtrl.Wire();

            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);

            Assert.That(cb.HasAttribute("checked"), Is.True,
                "click on a <label for=...> should toggle the referenced input");
        }

        [Test]
        public void Number_input_rejects_non_numeric_text() {
            var (_, input, d) = Setup("<input type=\"number\" value=\"\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchTextInput("a");
            d.DispatchTextInput("1");
            d.DispatchTextInput("2");
            d.DispatchTextInput("x");
            Assert.That(ctrl.Model.Text, Is.EqualTo("12"));
        }

        [Test]
        public void Shift_arrow_extends_selection_and_typing_replaces_it() {
            // Regression: Shift+ArrowLeft must extend the selection (not just
            // move the caret), and a subsequent character insert must replace
            // the selected range — not append next to it.
            var (_, input, d) = Setup("<input value=\"abcdef\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(6);
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.Shift, false);
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.Shift, false);
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.Shift, false);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(3));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(6));
            Assert.That(ctrl.Model.Selection.IsCollapsed, Is.False);
            d.DispatchTextInput("X");
            Assert.That(ctrl.Model.Text, Is.EqualTo("abcX"));
            Assert.That(input.GetAttribute("value"), Is.EqualTo("abcX"));
        }

        [Test]
        public void Ctrl_arrow_jumps_word_and_ctrl_backspace_deletes_word() {
            // Regression: Ctrl+ArrowLeft jumps to previous word boundary and
            // Ctrl+Backspace deletes the preceding word.
            var (_, input, d) = Setup("<input value=\"hello world foo\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(15); // end
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(12),
                "Ctrl+ArrowLeft from end of 'hello world foo' lands at start of 'foo'");
            d.DispatchKeyDown("Backspace", "Backspace", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("hello foo"),
                "Ctrl+Backspace at start of 'foo' deletes the preceding 'world '");
            Assert.That(input.GetAttribute("value"), Is.EqualTo("hello foo"));
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(6));
        }

        [Test]
        public void MaxLength_attribute_caps_typed_input() {
            // Regression: typing past maxlength must be silently dropped.
            var (_, input, d) = Setup("<input value=\"\" maxlength=\"3\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchTextInput("a");
            d.DispatchTextInput("b");
            d.DispatchTextInput("c");
            d.DispatchTextInput("d");
            d.DispatchTextInput("e");
            Assert.That(ctrl.Model.Text, Is.EqualTo("abc"));
            Assert.That(input.GetAttribute("value"), Is.EqualTo("abc"));
        }

        // Audit FM7: author preventDefault() must block the default edit
        // action — the standard browser way to filter input. Pre-fix OnKey
        // performed the insert/delete without ever consulting
        // DefaultPrevented, contradicting the engine's own dispatcher
        // convention (Tab/spatial-nav gate on !DefaultPrevented).
        [Test]
        public void Author_preventDefault_blocks_typing_FM7() {
            var (doc, input, d) = Setup("<input value=\"ab\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(2);
            // Capture-phase author filter on the root: block every keystroke.
            var root = doc.GetElementsByTagName("html").First();
            d.AddEventListener(root, EventKind.KeyDown, e => e.PreventDefault(), useCapture: true);

            d.DispatchTextInput("X");
            Assert.That(ctrl.Model.Text, Is.EqualTo("ab"),
                "a capture-phase preventDefault must block the character insert (audit FM7)");
            Assert.That(input.GetAttribute("value"), Is.EqualTo("ab"));

            d.DispatchKeyDown("Backspace", "Backspace", KeyModifiers.None, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("ab"),
                "preventDefault must block the delete edit action too");
        }

        // Audit FM1: a <textarea>'s rendered content is its child TextNodes —
        // layout/paint never read the value attribute. Pre-fix, typing into a
        // focused textarea changed the attribute and PreventDefault'ed the
        // keystroke but NOTHING synced the children: the edit was swallowed
        // invisibly (the classic trap).
        [Test]
        public void Textarea_typing_updates_the_rendered_TextNode_FM1() {
            var (_, ta, d) = Setup("<textarea>hello</textarea>");
            var ctrl = new InputController(ta, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(5);
            d.DispatchTextInput("!");
            Assert.That(ctrl.Model.Text, Is.EqualTo("hello!"));
            Assert.That(RenderedText(ta), Is.EqualTo("hello!"),
                "the child TextNode — what layout/paint actually render — must reflect the edit (audit FM1)");
        }

        [Test]
        public void Empty_textarea_typing_creates_the_TextNode_FM1() {
            var (_, ta, d) = Setup("<textarea></textarea>");
            var ctrl = new InputController(ta, d);
            ctrl.Wire();
            d.DispatchTextInput("x");
            Assert.That(RenderedText(ta), Is.EqualTo("x"),
                "an initially-empty textarea must gain a TextNode on first edit");
        }

        [Test]
        public void Textarea_backspace_updates_the_rendered_TextNode_FM1() {
            var (_, ta, d) = Setup("<textarea>abc</textarea>");
            var ctrl = new InputController(ta, d);
            ctrl.Wire();
            ctrl.Model.SetCaret(3);
            d.DispatchKeyDown("Backspace", "Backspace", KeyModifiers.None, false);
            Assert.That(RenderedText(ta), Is.EqualTo("ab"));
        }

        static string RenderedText(Element e) {
            var sb = new System.Text.StringBuilder();
            void Walk(Node n) {
                foreach (var c in n.Children) {
                    if (c is TextNode t) sb.Append(t.Data);
                    else Walk(c);
                }
            }
            Walk(e);
            return sb.ToString();
        }

        // ── audit FM2: pointer caret placement + drag selection ────────────
        // Pre-fix InputController registered NO pointer listeners: every click
        // into a field select-all'd the value (so the next keystroke replaced
        // it), and drag/shift-click selection didn't exist at all — despite
        // every primitive (CaretGeometry, the model measurer, pointer capture)
        // existing and being unit-tested.

        // Fake box at X=10 with 2px padding → content-left = 12; 8px/char
        // measure makes caret slots land at 12 + 8·i.
        const double ContentLeft = 12.0;
        const double CharW = 8.0;

        static InputController WirePointer(Element input, EventDispatcher d) {
            var ctrl = new InputController(input, d);
            ctrl.Model.SetMeasureSubstring((t, s, c) => c * CharW);
            var box = new Weva.Layout.Boxes.BlockBox {
                Element = input, X = 10, Width = 124,
                PaddingLeft = 2, PaddingRight = 2,
            };
            ctrl.ElementToBox = _ => box;
            ctrl.Wire();
            return ctrl;
        }

        [Test]
        public void Click_places_a_collapsed_caret_not_select_all_FM2() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            // Click between chars 4 and 5 (closer to slot 4).
            d.DispatchPointerDown(ContentLeft + 4 * CharW + 1, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 4 * CharW + 1, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(4), "caret placed at the clicked slot");
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(4),
                "a click must place a COLLAPSED caret — pre-FM2 every click selected the whole value");
        }

        [Test]
        public void Drag_selects_the_swept_range_FM2() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            d.DispatchPointerDown(ContentLeft + 2 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchPointerMove(ContentLeft + 7 * CharW, 5, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 7 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(2));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(7), "drag must sweep a selection (audit FM2)");
        }

        [Test]
        public void Backward_drag_selects_with_backward_direction_FM2() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            d.DispatchPointerDown(ContentLeft + 7 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchPointerMove(ContentLeft + 2 * CharW, 5, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 2 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(2));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(7));
            Assert.That(ctrl.Model.Selection.Focus, Is.EqualTo(2), "focus follows the pointer");
        }

        [Test]
        public void Shift_click_extends_from_the_existing_caret_FM2() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            ctrl.Model.SetCaret(3);
            d.DispatchPointerDown(ContentLeft + 9 * CharW, 5, 0, KeyModifiers.Shift);
            d.DispatchPointerUp(ContentLeft + 9 * CharW, 5, 0, KeyModifiers.Shift);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(3));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(9), "shift-click extends (audit FM2)");
        }

        [Test]
        public void Keyboard_focus_still_selects_all_FM2() {
            // The suppression is pointer-scoped: programmatic/keyboard focus
            // keeps the select-all convenience.
            var (_, input, d) = Setup("<input value=\"abc\">");
            var ctrl = WirePointer(input, d);
            d.Focus(null);
            d.Focus(input);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(0));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(3), "non-pointer focus selects all");
        }

        // ── input/selection audit #4: click-streak selection modes ──────────
        // The dispatcher counts button-0 down streaks by time (≤500ms) +
        // proximity (≤4px) + same target, exposing DOM UIEvent.detail;
        // detail 2 selects the word unit and drags by words, detail ≥3
        // selects all (Chrome's paragraph mode == the whole single-line value).

        static void Click(EventDispatcher d, double x, double t) {
            d.Tick(t);
            d.DispatchPointerDown(x, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(x, 5, 0, KeyModifiers.None);
        }

        [Test]
        public void Double_click_selects_the_word_under_the_pointer() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            double x = ContentLeft + 2 * CharW;
            Click(d, x, 0.0);
            Click(d, x, 0.2);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(0));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(5), "double-click selects 'hello'");
        }

        [Test]
        public void Double_click_drag_extends_by_whole_words() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            double x = ContentLeft + 2 * CharW;
            Click(d, x, 0.0);
            d.Tick(0.2);
            d.DispatchPointerDown(x, 5, 0, KeyModifiers.None); // second down: detail 2
            d.DispatchPointerMove(ContentLeft + 8 * CharW, 5, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 8 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(0));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(11),
                "word-mode drag snaps the selection to whole words ('hello world')");
        }

        [Test]
        public void Double_click_backward_drag_snaps_words_with_backward_direction() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            double x = ContentLeft + 8 * CharW; // inside 'world'
            Click(d, x, 0.0);
            d.Tick(0.2);
            d.DispatchPointerDown(x, 5, 0, KeyModifiers.None);
            d.DispatchPointerMove(ContentLeft + 2 * CharW, 5, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 2 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(0));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(11));
            Assert.That(ctrl.Model.Selection.Direction, Is.EqualTo(SelectionDirection.Backward),
                "dragging left of the anchor word selects backward");
        }

        [Test]
        public void Triple_click_selects_all_and_drag_keeps_it() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            double x = ContentLeft + 2 * CharW;
            Click(d, x, 0.0);
            Click(d, x, 0.2);
            d.Tick(0.4);
            d.DispatchPointerDown(x, 5, 0, KeyModifiers.None); // third down: detail 3
            d.DispatchPointerMove(ContentLeft + 5 * CharW, 5, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 5 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(0));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(11), "triple-click selects all; drag keeps it");
        }

        [Test]
        public void Slow_second_click_is_a_single_click() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            double x = ContentLeft + 2 * CharW;
            Click(d, x, 0.0);
            Click(d, x, 0.8); // past the 500ms double-click window
            Assert.That(ctrl.Model.Selection.Start, Is.EqualTo(2));
            Assert.That(ctrl.Model.Selection.End, Is.EqualTo(2),
                "a slow second click places a collapsed caret, not a word selection");
        }

        [Test]
        public void Far_second_click_is_a_single_click() {
            var (_, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            Click(d, ContentLeft + 2 * CharW, 0.0);
            Click(d, ContentLeft + 9 * CharW, 0.1); // fast but far away
            Assert.That(ctrl.Model.Selection.End - ctrl.Model.Selection.Start, Is.EqualTo(0),
                "a second click beyond the proximity slop restarts the streak");
        }

        // ── input/selection audit #5: clipboard (Ctrl+C/X/V) ────────────────

        static (InputController ctrl, Weva.Forms.Bridge.InMemoryClipboardBridge clip, EventDispatcher d)
                SetupClipboard(string html) {
            var (_, input, d) = Setup(html);
            var ctrl = new InputController(input, d);
            ctrl.Clipboard = new Weva.Forms.Bridge.InMemoryClipboardBridge();
            ctrl.Wire();
            return (ctrl, (Weva.Forms.Bridge.InMemoryClipboardBridge)ctrl.Clipboard, d);
        }

        [Test]
        public void Ctrl_C_copies_the_selection() {
            var (ctrl, clip, d) = SetupClipboard("<input value=\"hello world\">");
            ctrl.Model.SetSelection(6, 11);
            d.DispatchKeyDown("c", "KeyC", KeyModifiers.Ctrl, false);
            Assert.That(clip.GetText(), Is.EqualTo("world"));
            Assert.That(ctrl.Model.Text, Is.EqualTo("hello world"), "copy must not edit");
        }

        [Test]
        public void Ctrl_X_cuts_the_selection() {
            var (ctrl, clip, d) = SetupClipboard("<input value=\"hello world\">");
            ctrl.Model.SetSelection(5, 11);
            d.DispatchKeyDown("x", "KeyX", KeyModifiers.Ctrl, false);
            Assert.That(clip.GetText(), Is.EqualTo(" world"));
            Assert.That(ctrl.Model.Text, Is.EqualTo("hello"));
        }

        [Test]
        public void Ctrl_V_pastes_at_the_caret_and_over_a_selection() {
            var (ctrl, clip, d) = SetupClipboard("<input value=\"ab\">");
            clip.SetText("XY");
            ctrl.Model.SetCaret(1);
            d.DispatchKeyDown("v", "KeyV", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("aXYb"));
            ctrl.Model.SetSelection(0, 4);
            d.DispatchKeyDown("v", "KeyV", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("XY"), "paste replaces the selection");
        }

        [Test]
        public void Paste_strips_newlines_in_single_line_inputs() {
            var (ctrl, clip, d) = SetupClipboard("<input value=\"\">");
            clip.SetText("one\r\ntwo\nthree");
            d.DispatchKeyDown("v", "KeyV", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("onetwothree"),
                "HTML value sanitization strips CR/LF for single-line inputs (Chrome)");
        }

        [Test]
        public void Password_fields_block_copy_and_cut() {
            var (ctrl, clip, d) = SetupClipboard("<input type=\"password\" value=\"secret\">");
            clip.SetText("sentinel");
            ctrl.Model.SelectAll();
            d.DispatchKeyDown("c", "KeyC", KeyModifiers.Ctrl, false);
            Assert.That(clip.GetText(), Is.EqualTo("sentinel"), "Chrome blocks copying from password fields");
            d.DispatchKeyDown("x", "KeyX", KeyModifiers.Ctrl, false);
            Assert.That(clip.GetText(), Is.EqualTo("sentinel"));
            Assert.That(ctrl.Model.Text, Is.EqualTo("secret"), "cut must not edit a password field");
        }

        // ── input/selection audit #7: persistent edit-scroll window ─────────
        // WirePointer geometry: availW = 124 − 2·2 = 120px, 8px/char → 15
        // visible slots. A 30-char value measures 240px (maxScroll = 122).

        const string ThirtyChars = "abcdefghijklmnopqrstuvwxyz0123"; // 30 chars

        [Test]
        public void Caret_left_moves_do_not_jump_the_window_back_to_zero() {
            var (_, input, d) = Setup($"<input value=\"{ThirtyChars}\">");
            var ctrl = WirePointer(input, d);
            // Click at slot 16 (past the fold) → window follows the caret.
            d.DispatchPointerDown(ContentLeft + 16 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 16 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.EditScrollX, Is.EqualTo(16 * CharW + 2 - 120).Within(0.01),
                "window follows the caret past the right edge");
            double windowBefore = ctrl.EditScrollX;
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, false);
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, false);
            Assert.That(ctrl.EditScrollX, Is.EqualTo(windowBefore).Within(0.01),
                "moving left INSIDE the window must not scroll (the stateless model " +
                "jumped the view back toward the string start on every left move)");
        }

        [Test]
        public void Caret_at_end_clamps_to_max_scroll_and_home_reveals_the_start() {
            var (_, input, d) = Setup($"<input value=\"{ThirtyChars}\">");
            var ctrl = WirePointer(input, d);
            d.DispatchPointerDown(ContentLeft + 5, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 5, 5, 0, KeyModifiers.None);
            d.DispatchKeyDown("End", "End", KeyModifiers.None, false);
            Assert.That(ctrl.EditScrollX, Is.EqualTo(30 * CharW + 2 - 120).Within(0.01),
                "End scrolls to show the value tail");
            d.DispatchKeyDown("Home", "Home", KeyModifiers.None, false);
            Assert.That(ctrl.EditScrollX, Is.EqualTo(0).Within(0.01), "Home reveals the start");
        }

        [Test]
        public void Drag_past_the_left_edge_extends_and_scrolls_back() {
            var (_, input, d) = Setup($"<input value=\"{ThirtyChars}\">");
            var ctrl = WirePointer(input, d);
            // Caret to the end (window at maxScroll), then drag left past the edge.
            d.DispatchPointerDown(ContentLeft + 16 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchKeyDown("End", "End", KeyModifiers.None, false);
            double windowAtEnd = ctrl.EditScrollX;
            d.DispatchPointerDown(ContentLeft + 100, 5, 0, KeyModifiers.None);
            int focusAfterDown = ctrl.Model.Selection.Focus;
            for (int i = 0; i < 4; i++) {
                d.DispatchPointerMove(ContentLeft - 5, 5, KeyModifiers.None);
            }
            d.DispatchPointerUp(ContentLeft - 5, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.Model.Selection.Focus, Is.LessThan(focusAfterDown),
                "each move past the left edge extends the selection one slot further back");
            Assert.That(ctrl.EditScrollX, Is.LessThan(windowAtEnd),
                "the window scrolls back to keep the extending selection visible " +
                "(the stateless model could never scroll left of the caret-follow)");
        }

        [Test]
        public void Blur_resets_the_edit_window_to_the_value_start() {
            var doc = HtmlParser.Parse($"<input id=\"a\" value=\"{ThirtyChars}\"><input id=\"b\" value=\"\">");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new TrivialHit(a), new FakeUIClock());
            d.Focus(a);
            var ctrl = WirePointer(a, d);
            d.DispatchPointerDown(ContentLeft + 20 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 20 * CharW, 5, 0, KeyModifiers.None);
            Assert.That(ctrl.EditScrollX, Is.GreaterThan(0));
            d.Focus(b); // blur a
            Assert.That(ctrl.EditScrollX, Is.EqualTo(0),
                "an unfocused overflowing field shows the value START (Chrome)");
        }

        [Test]
        public void Ctrl_Z_undoes_typing_and_syncs_the_attribute() {
            var (_, input, d) = Setup("<input value=\"\">");
            var ctrl = new InputController(input, d);
            ctrl.Wire();
            d.DispatchTextInput("h");
            d.DispatchTextInput("i");
            Assert.That(input.GetAttribute("value"), Is.EqualTo("hi"));
            d.DispatchKeyDown("z", "KeyZ", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo(""));
            Assert.That(input.GetAttribute("value"), Is.EqualTo(""),
                "undo must flow back into the value attribute");
            d.DispatchKeyDown("z", "KeyZ", KeyModifiers.Ctrl | KeyModifiers.Shift, false);
            Assert.That(input.GetAttribute("value"), Is.EqualTo("hi"), "Ctrl+Shift+Z redoes");
            d.DispatchKeyDown("z", "KeyZ", KeyModifiers.Ctrl, false);
            d.DispatchKeyDown("y", "KeyY", KeyModifiers.Ctrl, false);
            Assert.That(input.GetAttribute("value"), Is.EqualTo("hi"), "Ctrl+Y redoes");
        }

        // ── input/selection audit #9/#10/#11: default-action semantics ──────

        [Test]
        public void Consumed_pointer_up_synthesizes_no_click() {
            // Chrome: a scroll gesture never also clicks. ScrollEventHandler
            // PreventDefaults the up that ends an armed pan — the dispatcher
            // must honor it (pre-fix: pan a list, release over a button, the
            // button clicked).
            var doc = HtmlParser.Parse("<div id=\"btn\">press</div>");
            var btn = doc.GetElementById("btn");
            var d = new EventDispatcher(doc, new TrivialHit(btn), new FakeUIClock());
            var root = (Element)doc.Children.First(c => c is Element);
            int clicks = 0;
            d.AddEventListener(btn, EventKind.Click, _ => clicks++);
            d.AddEventListener(root, EventKind.PointerUp, e => e.PreventDefault(), useCapture: true);
            d.DispatchPointerDown(5, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(5, 5, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(0), "a consumed pointer-up must not synthesize a click");
        }

        [Test]
        public void Prevented_pointer_down_keeps_the_previous_focus() {
            // Chrome's focus-preserving-toolbar idiom: preventDefault() on
            // mousedown cancels the focus default action. Also what stops
            // scrollbar clicks (which PreventDefault the down) from stealing
            // focus + firing a blur-commit change under the scrollbar.
            var doc = HtmlParser.Parse("<input id=\"a\" value=\"\"><div id=\"b\">toolbar</div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new TrivialHit(b), new FakeUIClock());
            d.Focus(a);
            var root = (Element)doc.Children.First(c => c is Element);
            d.AddEventListener(root, EventKind.PointerDown, e => e.PreventDefault(), useCapture: true);
            d.DispatchPointerDown(5, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(5, 5, 0, KeyModifiers.None);
            Assert.That(d.FocusedElement, Is.SameAs(a),
                "preventDefault on pointer-down must keep the previous focus");
        }

        [Test]
        public void Input_pointer_events_bubble_to_ancestors() {
            // Chrome dispatches pointer events on text fields to ancestors
            // normally — selection is a default action, not a propagation
            // stop. Pre-fix the input StopPropagation'd down/move/up, so
            // author listeners on containers never saw pointer activity that
            // started in a field.
            var (doc, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            var parent = input.Parent as Element;
            Assert.That(parent, Is.Not.Null);
            int downs = 0, moves = 0, ups = 0;
            d.AddEventListener(parent, EventKind.PointerDown, _ => downs++);
            d.AddEventListener(parent, EventKind.PointerMove, _ => moves++);
            d.AddEventListener(parent, EventKind.PointerUp, _ => ups++);
            d.DispatchPointerDown(ContentLeft + 2 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchPointerMove(ContentLeft + 6 * CharW, 5, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 6 * CharW, 5, 0, KeyModifiers.None);
            Assert.That((downs, moves, ups), Is.EqualTo((1, 1, 1)),
                "selection gestures must still bubble to author listeners");
            Assert.That(ctrl.Model.Selection.End - ctrl.Model.Selection.Start, Is.GreaterThan(0),
                "…while the selection default action still runs");
        }

        [Test]
        public void Prevented_pointer_down_places_no_caret() {
            // FM7 for pointers: caret placement is the down's default action.
            var (doc, input, d) = Setup("<input value=\"hello world\">");
            var ctrl = WirePointer(input, d);
            ctrl.Model.SetSelection(0, 5); // pre-existing selection must survive
            var root = (Element)doc.Children.First(c => c is Element);
            d.AddEventListener(root, EventKind.PointerDown, e => e.PreventDefault(), useCapture: true);
            d.DispatchPointerDown(ContentLeft + 8 * CharW, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(ContentLeft + 8 * CharW, 5, 0, KeyModifiers.None);
            Assert.That((ctrl.Model.Selection.Start, ctrl.Model.Selection.End), Is.EqualTo((0, 5)),
                "a prevented pointer-down must not move the caret or selection");
        }

        // ── input/selection audit #6 (minimal): textarea click safety ───────

        [Test]
        public void Textarea_click_places_a_caret_instead_of_invisible_select_all() {
            var (_, ta, d) = Setup("<textarea>existing content</textarea>");
            var ctrl = new InputController(ta, d);
            ctrl.Wire();
            d.DispatchPointerDown(5, 5, 0, KeyModifiers.None);
            d.DispatchPointerUp(5, 5, 0, KeyModifiers.None);
            d.DispatchTextInput("!");
            // Pre-fix: the click armed OnFocus' select-all (never painted for
            // textareas), so the next keystroke silently REPLACED everything.
            Assert.That(ctrl.Model.Text, Is.EqualTo("existing content!"),
                "typing after a click must append at the caret, not replace the whole content");
        }

        [Test]
        public void ReadOnly_allows_copy_but_blocks_cut_and_paste() {
            var (ctrl, clip, d) = SetupClipboard("<input value=\"abc\" readonly>");
            ctrl.Model.SetSelection(0, 3);
            d.DispatchKeyDown("c", "KeyC", KeyModifiers.Ctrl, false);
            Assert.That(clip.GetText(), Is.EqualTo("abc"), "copy is allowed in readonly fields");
            d.DispatchKeyDown("x", "KeyX", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("abc"), "cut is blocked in readonly fields");
            clip.SetText("zzz");
            d.DispatchKeyDown("v", "KeyV", KeyModifiers.Ctrl, false);
            Assert.That(ctrl.Model.Text, Is.EqualTo("abc"), "paste is blocked in readonly fields");
        }
    }
}
