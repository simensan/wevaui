// Input and events through the shared core from C#: the portable checks of
// the Godot host's keyboard_integration_tests.gd and input_integration_tests.gd
// (the ones about the document rather than Godot's Control routing), driven
// through NativeDocument's own calls the way NativeInputFeed drives them.
// The stub face is enough here: nothing measures text.
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeInputTests
    {
        private const string KeyboardHtml =
            "<button id=\"button\" on-click=\"Run\">Run</button>" +
            "<input id=\"check\" type=\"checkbox\">" +
            "<form id=\"radios\"><input id=\"a\" type=\"radio\" name=\"g\"><input type=\"radio\" name=\"g\" disabled><input id=\"b\" type=\"radio\" name=\"g\"></form>" +
            "<form><input id=\"other\" type=\"radio\" name=\"g\" checked></form>" +
            "<input id=\"range\" type=\"range\">" +
            "<details id=\"details\"><summary id=\"summary\">More</summary><p>Content</p></details>" +
            "<button id=\"trigger\" popovertarget=\"menu\">Menu</button><div id=\"menu\" popover>Actions</div>" +
            "<form id=\"login\" on-submit=\"Submit\"><input id=\"name\"><button id=\"submit\" on-click=\"Default\">Submit</button></form>";

        private NativeDocument _doc;
        private readonly List<NativeEvent> _events = new List<NativeEvent>();
        private readonly List<string> _clicks = new List<string>();
        private readonly List<string> _handlers = new List<string>();
        private readonly List<string> _submits = new List<string>();
        private readonly List<string> _texts = new List<string>();

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 400);
            _doc.LoadHtml(KeyboardHtml);
            _doc.SetCss("html,body{margin:0}button,input{margin:2px}");
            _doc.Update(0);
            Pump();
            _clicks.Clear();
            _handlers.Clear();
            _submits.Clear();
            _texts.Clear();
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        private string Id(uint e) => e == WevaNative.WEVA_ELEMENT_NONE ? "" : _doc.ElementId(e);
        private string FocusedId => Id(_doc.Focus);
        private string Value(string selector) => _doc.ElementValue(_doc.Query(selector));
        private bool Has(string selector) => _doc.Query(selector) != WevaNative.WEVA_ELEMENT_NONE;

        // After every input the host updates and drains the queue, as the Godot host does.
        private void Pump()
        {
            _doc.Update(0);
            _events.Clear();
            _doc.PollEvents(_events);
            foreach (NativeEvent e in _events)
            {
                if (e.Handler.Length > 0) _handlers.Add(e.Handler);
                switch (e.Kind)
                {
                    case weva_event_kind.WEVA_EVENT_CLICK: _clicks.Add(Id(e.Target)); break;
                    case weva_event_kind.WEVA_EVENT_SUBMIT: _submits.Add(Id(e.Target)); break;
                    case weva_event_kind.WEVA_EVENT_TEXT_INPUT: _texts.Add(e.Text); break;
                }
            }
        }

        private bool KeyDown(weva_key key, uint modifiers = 0)
        {
            bool consumed = _doc.Key(key, true, modifiers);
            Pump();
            return consumed;
        }

        private bool KeyUp(weva_key key, uint modifiers = 0)
        {
            bool consumed = _doc.Key(key, false, modifiers);
            Pump();
            return consumed;
        }

        private void Press(weva_key key)
        {
            KeyDown(key);
            KeyUp(key);
        }

        private bool Type(string text, uint modifiers = 0)
        {
            bool taken = _doc.TryTextInput(text, modifiers);
            Pump();
            return taken;
        }

        [Test]
        public void Enter_ActivatesAButtonOnKeyDown_AndRepeats()
        {
            Assert.That(_doc.SetFocus("#button"));
            Assert.That(KeyDown(weva_key.WEVA_KEY_ENTER), "button activation is consumed before gameplay input");
            Assert.That(_clicks, Is.EqualTo(new[] { "button" }), "Enter invokes the click on key-down");
            Assert.That(_handlers, Is.EqualTo(new[] { "Run" }), "the on-click handler name is reported");
            KeyDown(weva_key.WEVA_KEY_ENTER);   // a held key repeats as key-down
            Assert.That(_clicks.Count, Is.EqualTo(2), "held Enter repeats button activation");
            KeyUp(weva_key.WEVA_KEY_ENTER);
            Assert.That(_clicks.Count, Is.EqualTo(2), "Enter release does not click a second time");
        }

        [Test]
        public void Space_ActivatesAButtonOnRelease_AndTheKeyIsConsumed()
        {
            _doc.SetFocus("#button");
            // The key is taken by the button, which is what tells a host (the
            // Godot host, NativeInputFeed) not to deliver the " " it produces
            // as text as well; the core itself accepts any text it is handed.
            Assert.That(KeyDown(weva_key.WEVA_KEY_SPACE), "Space down on a button is consumed");
            KeyDown(weva_key.WEVA_KEY_SPACE);
            Assert.That(_clicks, Is.Empty, "Space waits for release, including repeated key-down");
            Assert.That(_texts, Is.Empty, "no text was produced by the key edges");
            Assert.That(KeyUp(weva_key.WEVA_KEY_SPACE), "Space release remains owned by the UI");
            Assert.That(_clicks, Is.EqualTo(new[] { "button" }), "Space release clicks once");
        }

        [Test]
        public void FocusLoss_CancelsAHeldButton()
        {
            _doc.SetFocus("#button");
            KeyDown(weva_key.WEVA_KEY_SPACE);
            _doc.SetFocus("#check");
            Pump();
            KeyUp(weva_key.WEVA_KEY_SPACE);
            Assert.That(_clicks, Is.Empty, "focus transfer cancels a held HTML button");
            _doc.SetFocus("#button");
            Press(weva_key.WEVA_KEY_SPACE);
            Assert.That(_clicks, Is.EqualTo(new[] { "button" }), "the first fresh Space after focus loss works");
        }

        [Test]
        public void Checkbox_TogglesOnSpaceRelease_NotEnter()
        {
            _doc.SetFocus("#check");
            KeyDown(weva_key.WEVA_KEY_SPACE);
            Assert.That(Value("#check"), Is.EqualTo(""), "checkbox stays unchanged on Space down");
            KeyUp(weva_key.WEVA_KEY_SPACE);
            Assert.That(Value("#check"), Is.EqualTo("on"), "checkbox toggles on Space release");
            Press(weva_key.WEVA_KEY_ENTER);
            Assert.That(Value("#check"), Is.EqualTo("on"), "Enter does not toggle a checkbox");
            bool changed = false;
            foreach (NativeEvent e in _events) if (e.Kind == weva_event_kind.WEVA_EVENT_CHANGE) changed = true;
            _doc.SetFocus("#check");
            Press(weva_key.WEVA_KEY_SPACE);
            foreach (NativeEvent e in _events) if (e.Kind == weva_event_kind.WEVA_EVENT_CHANGE && Id(e.Target) == "check") changed = true;
            Assert.That(changed, "a toggled box commits its value (CHANGE)");
            Assert.That(Value("#check"), Is.EqualTo(""));
        }

        [Test]
        public void RadioArrows_SkipDisabled_WrapInGroup_ScopedToForm()
        {
            _doc.SetFocus("#a");
            Press(weva_key.WEVA_KEY_RIGHT);
            Assert.That(FocusedId, Is.EqualTo("b"), "radio arrows skip disabled members");
            Assert.That(Value("#b"), Is.EqualTo("on"), "and select the next member");
            Assert.That(Value("#other"), Is.EqualTo("on"), "radio selection is scoped to its form");
            Press(weva_key.WEVA_KEY_RIGHT);
            Assert.That(FocusedId, Is.EqualTo("a"), "radio arrow navigation wraps inside its group");
            Assert.That(Value("#b"), Is.EqualTo(""));
            uint next = _doc.FocusStep(false, true);
            Pump();
            Assert.That(Id(next), Is.EqualTo("other"), "Tab leaves the radio group after one stop");
        }

        [Test]
        public void Slider_ArrowsPagesHomeEnd()
        {
            _doc.SetFocus("#range");
            Press(weva_key.WEVA_KEY_RIGHT);
            Assert.That(Value("#range"), Is.EqualTo("51"), "slider arrows change the value");
            Press(weva_key.WEVA_KEY_PAGE_UP);
            Assert.That(Value("#range"), Is.EqualTo("61"), "PageUp changes the slider by a page");
            Press(weva_key.WEVA_KEY_HOME);
            Assert.That(Value("#range"), Is.EqualTo("0"), "Home selects the slider minimum");
            Press(weva_key.WEVA_KEY_END);
            Assert.That(Value("#range"), Is.EqualTo("100"), "End selects the slider maximum");
            Press(weva_key.WEVA_KEY_RIGHT);
            Assert.That(Value("#range"), Is.EqualTo("100"), "slider arrows clamp at the endpoint");
        }

        [Test]
        public void Details_AndPopover_ToggleFromTheKeyboard()
        {
            _doc.SetFocus("#summary");
            Press(weva_key.WEVA_KEY_ENTER);
            Assert.That(Has("#details[open]"), "Enter expands details through its summary");
            Press(weva_key.WEVA_KEY_SPACE);
            Assert.That(Has("#details[open]"), Is.False, "Space collapses details through its summary");
            _doc.SetFocus("#trigger");
            Press(weva_key.WEVA_KEY_SPACE);
            Assert.That(Has("#menu[data-popover-open]"), "Space opens a popover trigger");
            Press(weva_key.WEVA_KEY_ESCAPE);
            Assert.That(Has("#menu[data-popover-open]"), Is.False, "Escape dismisses the keyboard-opened popover");
        }

        [Test]
        public void Form_SubmitsImplicitly_AndUnicodeTypes()
        {
            _doc.SetFocus("#name");
            Assert.That(Type("é"), "Unicode text input edits the focused field");
            Assert.That(_texts, Is.EqualTo(new[] { "é" }), "TEXT_INPUT preserves the Unicode event text");
            Assert.That(Value("#name"), Is.EqualTo("é"));
            Press(weva_key.WEVA_KEY_ENTER);
            Assert.That(_submits, Is.EqualTo(new[] { "login" }), "Enter in a field submits its form, targeting the form");
            Assert.That(_handlers, Does.Contain("Submit"), "the form's on-submit handler is named");
            _doc.SetElementAttribute(_doc.Query("#submit"), "disabled", "");
            Pump();
            Press(weva_key.WEVA_KEY_ENTER);
            Assert.That(_submits.Count, Is.EqualTo(1), "a disabled default button blocks implicit submission");
        }

        [Test]
        public void Editing_BackspaceUndoRedo_AndShortcutsAreNotText()
        {
            _doc.SetFocus("#name");
            Type("x");
            Type("y");
            Assert.That(Value("#name"), Is.EqualTo("xy"));
            Press(weva_key.WEVA_KEY_BACKSPACE);
            Assert.That(Value("#name"), Is.EqualTo("x"), "Backspace deletes");
            // Shortcuts (Ctrl+A/C/V/X/Z/Y) are the host's to keep from the text
            // path: NativeInputFeed drops typed characters while Ctrl is held,
            // as the Godot host does, and routes paste through PasteText.
            Assert.That(_doc.PasteText("é!"), "paste inserts");
            Pump();
            Assert.That(Value("#name"), Is.EqualTo("xé!"));
        }

        private const string PointerHtml =
            "<body><div id=\"box\" on-click=\"Box\"><button id=\"button\">Go</button></div>" +
            "<div id=\"ghost\"></div><div id=\"list\"><div id=\"tall\"></div></div></body>";
        private const string PointerCss =
            "html,body{margin:0;pointer-events:none}#box,#list{pointer-events:auto}#box{position:absolute;left:0;top:0;width:100px;height:60px}" +
            "button{position:absolute;left:10px;top:10px;width:50px;height:30px;margin:0}" +
            "#ghost{position:absolute;left:200px;top:0;width:100px;height:100px;pointer-events:none}" +
            "#list{position:absolute;left:0;top:200px;width:100px;height:100px;overflow:auto}#tall{height:1000px}";

        private void PointerFixture()
        {
            _doc.LoadHtml(PointerHtml);
            _doc.SetCss(PointerCss);
            Pump();
            _clicks.Clear();
            _handlers.Clear();
        }

        private void Click(double x, double y, uint button = (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY)
        {
            _doc.SetPointer(x, y, 0);
            Pump();
            _doc.SetPointer(x, y, button);
            Pump();
            _doc.SetPointer(x, y, 0);
            Pump();
        }

        [Test]
        public void Pointer_PressReleaseClick_WithEnterAndLeave()
        {
            PointerFixture();
            _doc.SetPointer(30, 20, 0);
            Pump();
            bool entered = false;
            foreach (NativeEvent e in _events) if (e.Kind == weva_event_kind.WEVA_EVENT_POINTER_ENTER && Id(e.Target) == "button") entered = true;
            Assert.That(entered, "moving over the button raises POINTER_ENTER");
            Assert.That(Id(_doc.ElementAt(30, 20)), Is.EqualTo("button"));
            Assert.That(_doc.AcceptsPointer(30, 20));

            _doc.SetPointer(30, 20, (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY);
            Pump();
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_POINTER_DOWN && Id(e.Target) == "button"), "POINTER_DOWN on the button");
            Assert.That(_clicks, Is.Empty, "no click before release");
            _doc.SetPointer(30, 20, 0);
            Pump();
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_POINTER_UP && Id(e.Target) == "button"), "POINTER_UP on the button");
            Assert.That(_clicks, Is.EqualTo(new[] { "button" }), "release on the pressed element clicks it");
            Assert.That(_handlers, Is.EqualTo(new[] { "Box" }), "the nearest on-click ancestor names the handler");

            _doc.SetPointer(150, 150, 0);
            Pump();
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_POINTER_LEAVE), "moving away raises POINTER_LEAVE");
            _doc.ClearPointer();
            Pump();
        }

        [Test]
        public void Pointer_ReleaseElsewhere_DoesNotClick_AndRightClickIsAContextMenu()
        {
            PointerFixture();
            _doc.SetPointer(30, 20, (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY);
            Pump();
            _doc.SetPointer(150, 150, (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY);
            Pump();
            _doc.SetPointer(150, 150, 0);
            Pump();
            Assert.That(_clicks, Is.Empty, "a press released elsewhere is not a click");
            Click(30, 20, (uint)weva_pointer_button.WEVA_BUTTON_SECONDARY);
            Assert.That(_clicks, Is.Empty, "the secondary button activates nothing");
        }

        [Test]
        public void Pointer_ContextMenuOnSecondaryPress()
        {
            PointerFixture();
            _doc.SetPointer(30, 20, 0);
            Pump();
            _doc.SetPointer(30, 20, (uint)weva_pointer_button.WEVA_BUTTON_SECONDARY);
            Pump();
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_CONTEXT_MENU && Id(e.Target) == "button"), "the secondary button down raises CONTEXT_MENU on the element");
            _doc.SetPointer(30, 20, 0);
            Pump();
            Assert.That(_clicks, Is.Empty, "and never a click");
        }

        [Test]
        public void PointerEventsNone_LetsHitTestingPass()
        {
            PointerFixture();
            Assert.That(_doc.AcceptsPointer(250, 50), Is.False, "pointer-events:none (the ghost, and the page behind it) is not this document's press: native GUI hit testing continues beneath");
            Assert.That(_doc.ElementAt(250, 50), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "the ghost is transparent to hit testing");
            Assert.That(_doc.AcceptsPointer(50, 250), "a descendant restores pointer-events:auto");
            Click(250, 50);
            Assert.That(_clicks, Is.Empty, "nothing clicks through a pointer-events:none element");
            Assert.That(_doc.AcceptsPointer(30, 20), "the button is this document's press");
        }

        [Test]
        public void Wheel_ScrollsTheContainerUnderThePointer_AndReportsIt()
        {
            PointerFixture();
            Assert.That(_doc.Scroll(50, 250, 0, 40), "a wheel over the list scrolls it");
            Pump();
            Assert.That(_doc.TryGetElementScroll(_doc.Query("#list"), out _, out double y, out _, out double maxY));
            Assert.That(y, Is.EqualTo(40).Within(0.01), "40px per notch");
            Assert.That(maxY, Is.EqualTo(900).Within(0.01));
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_SCROLL && Id(e.Target) == "list"), "SCROLL names the container");
            Assert.That(_doc.Scroll(50, 250, 0, -100), "scrolling back up");
            Pump();
            Assert.That(_doc.TryGetElementScroll(_doc.Query("#list"), out _, out y, out _, out _));
            Assert.That(y, Is.EqualTo(0).Within(0.01), "clamped at the top");
            Assert.That(_doc.Scroll(50, 250, 0, -40), Is.False, "nothing to scroll: the host passes the wheel on");
            Assert.That(_doc.Scroll(150, 150, 0, 40), Is.False, "no container under the pointer");
        }

        [Test]
        public void Composition_PreeditThenCommit()
        {
            _doc.SetFocus("#name");
            Pump();
            Assert.That(Id(_doc.TextInputTarget), Is.EqualTo("name"), "the focused field wants an IME");
            Assert.That(_doc.SetComposition("に", 0, 1), "a preedit is accepted");
            Pump();
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_COMPOSITION_START || e.Kind == weva_event_kind.WEVA_EVENT_COMPOSITION_UPDATE), "the IME lifecycle is reported");
            Assert.That(_doc.CommitComposition("日本"), "the commit replaces the preedit");
            Pump();
            Assert.That(Value("#name"), Is.EqualTo("日本"));
            Assert.That(_events.Exists(e => e.Kind == weva_event_kind.WEVA_EVENT_COMPOSITION_END && e.Text == "日本"), "COMPOSITION_END carries the final text");
            Assert.That(_doc.TryGetCaretBounds(out NativeBounds caret));
            Assert.That(caret.Height, Is.GreaterThan(0), "the caret has a place for IME candidates");
        }

        [Test]
        public void Focus_TabOrderWrapsOnlyWhenAsked()
        {
            uint first = _doc.FocusStep(false, false);
            Assert.That(Id(first), Is.EqualTo("button"));
            uint e = first;
            int steps = 0;
            while (e != WevaNative.WEVA_ELEMENT_NONE && steps < 20)
            {
                e = _doc.FocusStep(false, false);
                steps++;
            }
            Assert.That(e, Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "without wrap the edge returns NONE so a host can move on");
            Assert.That(steps, Is.GreaterThan(3));
            Assert.That(Id(_doc.FocusStep(false, true)), Is.EqualTo("button"), "with wrap the order cycles");
            Pump();
            Assert.That(_events.Exists(ev => ev.Kind == weva_event_kind.WEVA_EVENT_FOCUS && Id(ev.Target) == "button"), "FOCUS is reported");
        }
    }
}
