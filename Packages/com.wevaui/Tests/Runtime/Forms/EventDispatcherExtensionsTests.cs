using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    // TG20 — pins the sentinel-key contract between Input-System bridges and
    // InputController. EventDispatcherExtensions.DispatchTextInput funnels
    // character payloads through DispatchKeyDown using Key = "TextInput" with
    // the literal character carried in KeyboardEvent.Code. The InputController
    // (and any other consumer) distinguishes text input from control keys by
    // inspecting evt.Key.
    public class EventDispatcherExtensionsTests {
        sealed class HitFor : IHitTester {
            readonly Element only;
            public HitFor(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        static (Element input, EventDispatcher d) Setup(string html = "<input id=\"i\" value=\"\">") {
            var doc = HtmlParser.Parse(html);
            var input = doc.GetElementById("i");
            var d = new EventDispatcher(doc, new HitFor(input), new FakeUIClock());
            d.Focus(input);
            return (input, d);
        }

        // ---- 1. Single-char dispatch fires KeyDown with Key="TextInput" and the char in Code ----
        [Test]
        public void DispatchTextInput_single_char_fires_keydown_with_sentinel_key_and_payload_in_code() {
            var (input, d) = Setup();
            KeyboardEvent captured = null;
            d.AddEventListener(input, EventKind.KeyDown, e => captured = (KeyboardEvent)e);

            d.DispatchTextInput("a");

            Assert.That(captured, Is.Not.Null, "expected exactly one KeyDown event for DispatchTextInput(\"a\")");
            Assert.That(captured.Kind, Is.EqualTo(EventKind.KeyDown));
            Assert.That(captured.Key, Is.EqualTo("TextInput"), "DispatchTextInput must use the sentinel key 'TextInput'");
            Assert.That(captured.Code, Is.EqualTo("a"), "literal character payload must be carried in KeyboardEvent.Code");
            Assert.That(captured.Repeat, Is.False, "text-input dispatch is never marked as a key-repeat");
        }

        // ---- 2. Sentinel "TextInput" is distinct from real key names ----
        // The contract InputController consumes (`if (evt.Key == "TextInput") ...`) only
        // works if no DispatchKeyDown caller can legitimately produce Key == "TextInput"
        // for a real control key. Pin the public constant, and verify the helper predicates
        // ONLY match the sentinel and not common control / printable keys.
        [Test]
        public void TextInput_sentinel_is_distinct_from_real_key_names() {
            Assert.That(EventDispatcherExtensions.TextInputKey, Is.EqualTo("TextInput"));

            var realKeyNames = new[] { "a", "A", "Enter", "Tab", "Backspace", "ArrowLeft", "Escape", "Space", "1", "" };
            foreach (var k in realKeyNames) {
                var evt = new KeyboardEvent { Kind = EventKind.KeyDown, Key = k, Code = k };
                Assert.That(evt.IsTextInput(), Is.False, $"IsTextInput must be false for real key '{k}'");
                Assert.That(evt.TextInputPayload(), Is.Null, $"TextInputPayload must be null for real key '{k}'");
            }

            var sentinel = new KeyboardEvent { Kind = EventKind.KeyDown, Key = "TextInput", Code = "x" };
            Assert.That(sentinel.IsTextInput(), Is.True);
            Assert.That(sentinel.TextInputPayload(), Is.EqualTo("x"));
        }

        // ---- 3. Multi-char payload — pin the ACTUAL impl (single event, full payload in Code) ----
        // The implementation forwards the whole `text` string through one DispatchKeyDown,
        // so dispatching "abc" fires ONE KeyDown carrying "abc" in Code (not three per-char
        // dispatches). This matches IME-commit / Input-System onTextInput which already
        // delivers grouped strings.
        [Test]
        public void DispatchTextInput_multi_char_fires_single_event_with_full_payload() {
            var (input, d) = Setup();
            var events = new List<KeyboardEvent>();
            d.AddEventListener(input, EventKind.KeyDown, e => events.Add((KeyboardEvent)e));

            d.DispatchTextInput("abc");

            Assert.That(events.Count, Is.EqualTo(1), "DispatchTextInput delivers the payload as one KeyDown, not per-char");
            Assert.That(events[0].Key, Is.EqualTo("TextInput"));
            Assert.That(events[0].Code, Is.EqualTo("abc"));
            Assert.That(events[0].TextInputPayload(), Is.EqualTo("abc"));
        }

        // ---- 4. InputController-style routing — sentinel branches into text, real key branches to control handler ----
        // Mirrors the if/else InputController uses (see Runtime/Forms/InputController.cs:205):
        //     if (evt.IsTextInput()) Model.Insert(evt.TextInputPayload());
        //     else HandleControlKey(...);
        // A single handler must correctly split text-input vs. control-key dispatches that
        // both arrive as KeyDown events through the same listener.
        [Test]
        public void InputController_style_handler_routes_text_input_and_control_keys_correctly() {
            var (input, d) = Setup();
            var appended = new System.Text.StringBuilder();
            var controlKeys = new List<string>();
            d.AddEventListener(input, EventKind.KeyDown, e => {
                var ke = (KeyboardEvent)e;
                if (ke.Key == "TextInput") {
                    appended.Append(ke.Code);
                } else {
                    controlKeys.Add(ke.Key);
                }
            });

            d.DispatchTextInput("h");
            d.DispatchTextInput("i");
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, repeat: false);
            d.DispatchTextInput("!");
            d.DispatchKeyDown("Backspace", "Backspace", KeyModifiers.None, repeat: false);

            Assert.That(appended.ToString(), Is.EqualTo("hi!"),
                "text-input branch must accumulate the character payloads, ignoring control keys");
            Assert.That(controlKeys, Is.EqualTo(new[] { "Enter", "Backspace" }),
                "control-key branch must receive Enter/Backspace and NOT see the text-input dispatches");
        }

        // ---- 5. Bonus: Empty / null payload is a no-op (guard) — prevents the sentinel
        // leaking into KeyDown with an empty Code, which would mis-trigger an Insert("").
        [Test]
        public void DispatchTextInput_empty_or_null_payload_is_a_noop() {
            var (input, d) = Setup();
            int count = 0;
            d.AddEventListener(input, EventKind.KeyDown, _ => count++);

            d.DispatchTextInput("");
            d.DispatchTextInput(null);

            Assert.That(count, Is.Zero, "empty/null text payload must not fire a sentinel KeyDown");
        }

        // ---- 6. Modifiers from the bridge propagate through to the KeyboardEvent. ----
        // Input-System bridges pass the active modifier mask alongside the character so
        // consumers can decide whether to swallow (e.g. Ctrl+V) — verify the mods reach
        // the listener intact.
        [Test]
        public void DispatchTextInput_propagates_key_modifiers() {
            var (input, d) = Setup();
            KeyboardEvent captured = null;
            d.AddEventListener(input, EventKind.KeyDown, e => captured = (KeyboardEvent)e);

            d.DispatchTextInput("X", KeyModifiers.Shift | KeyModifiers.Ctrl);

            Assert.That(captured, Is.Not.Null);
            Assert.That(captured.Key, Is.EqualTo("TextInput"));
            Assert.That(captured.Code, Is.EqualTo("X"));
            Assert.That(captured.ShiftKey, Is.True);
            Assert.That(captured.CtrlKey, Is.True);
            Assert.That(captured.AltKey, Is.False);
            Assert.That(captured.MetaKey, Is.False);
        }
    }
}
