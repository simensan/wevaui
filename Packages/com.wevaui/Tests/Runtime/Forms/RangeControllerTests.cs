using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    // TG15 — RangeController test coverage. Pins value-math edge cases
    // (step quantization rounding, neg-step rejection, NaN guarding),
    // keyboard interactions (arrows / page / home / end), and event
    // dispatch semantics (input continuous, change on commit).
    public class RangeControllerTests {
        sealed class HitFor : IHitTester {
            readonly Element only;
            public HitFor(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        // Hit tester whose target can change between dispatches (open the select,
        // then click an option in the popup).
        sealed class MutableHit : IHitTester {
            public Element Target;
            public MutableHit(Element e) { Target = e; }
            public Element HitTest(double x, double y) => Target;
        }

        static (Document doc, Element input, EventDispatcher d) Setup(string html) {
            var doc = HtmlParser.Parse(html);
            var input = doc.GetElementsByTagName("input").First();
            var d = new EventDispatcher(doc, new HitFor(input), new FakeUIClock());
            d.Focus(input);
            return (doc, input, d);
        }

        static RangeController WireRange(Element input, EventDispatcher d, Box box = null) {
            var ctrl = new RangeController(input, d, _ => box);
            ctrl.Wire();
            return ctrl;
        }

        static BlockBox MakeTrackBox(Element e, double x, double width) {
            var b = new BlockBox();
            b.Element = e;
            b.Style = new ComputedStyle(e);
            b.X = x;
            b.Y = 0;
            b.Width = width;
            b.Height = 20;
            return b;
        }

        // ---------- Value math: quantization ----------

        [Test]
        public void Value_27_quantizes_to_30_round_half_up() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"27\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(30).Within(1e-9));
        }

        [Test]
        public void Value_25_quantizes_to_30_midpoint_biases_up() {
            // Spec: round-half-up at midpoint. 25 is the midpoint between 20 and 30
            // on a min=0 step=10 grid; should resolve to 30.
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"25\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(30).Within(1e-9));
        }

        [Test]
        public void Value_24_quantizes_down_to_20() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"24\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(20).Within(1e-9));
        }

        // ---------- Clamp ----------

        [Test]
        public void Out_of_range_value_above_max_clamps_to_max() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"200\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(100).Within(1e-9));
        }

        [Test]
        public void Out_of_range_value_below_min_clamps_to_min() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"-50\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(0).Within(1e-9));
        }

        // ---------- Step edge cases ----------

        [Test]
        public void Step_zero_is_rejected_and_treated_as_one() {
            // Spec: step=0 is invalid; engine should treat it as step=1
            // (so a fractional source value should quantize to the nearest integer).
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"0\" value=\"42.7\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(43).Within(1e-9));
        }

        [Test]
        public void Negative_step_is_rejected_and_treated_as_one() {
            // Spec: negative step is invalid; engine should treat it as step=1.
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"-5\" value=\"42.7\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(43).Within(1e-9));
        }

        [Test]
        public void Step_any_allows_arbitrary_values_no_quantization() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"any\" value=\"42.7\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(42.7).Within(1e-9));
        }

        [Test]
        public void NaN_value_defaults_to_midpoint_of_min_and_max() {
            // Spec: a value attribute that parses to NaN must fall back to
            // the (min + max) / 2 default.
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" value=\"NaN\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void Empty_value_attribute_defaults_to_midpoint() {
            // Sanity guard for the documented "value is half-way between
            // min and max" default (no value attribute).
            var (_, input, d) = Setup("<input type=\"range\" min=\"10\" max=\"30\">");
            var ctrl = WireRange(input, d);
            Assert.That(ctrl.Value, Is.EqualTo(20).Within(1e-9));
        }

        // ---------- Keyboard ----------

        [Test]
        public void ArrowUp_increments_by_step() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"30\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("ArrowUp", "ArrowUp", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void Registry_auto_wires_checkbox_so_click_toggles_it() {
            // Audit regression: FormControlsRegistry must wire checkbox/radio so they
            // toggle on click. IsTextInput excluded them, so checkboxes were inert in
            // live documents (no controller ever performed the toggle).
            var doc = HtmlParser.Parse("<input type=\"checkbox\" id=\"cb1\">");
            var cb = doc.GetElementById("cb1");
            var d = new EventDispatcher(doc, new HitFor(cb), new FakeUIClock());
            using var registry = new FormControlsRegistry(doc, d);

            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);

            Assert.That(cb.HasAttribute("checked"), Is.True,
                "registry-wired checkbox toggles on click");
        }

        [Test]
        public void Registry_auto_wires_label_so_click_forwards_to_checkbox() {
            // Audit regression: the registry must auto-wire BOTH the LabelController
            // (forwards the click to the target) AND the checkbox's InputController
            // (performs the toggle). Previously neither was wired — clicking a <label>
            // did nothing (silent a11y gap).
            var doc = HtmlParser.Parse(
                "<input type=\"checkbox\" id=\"cb1\">" +
                "<label for=\"cb1\">Subscribe</label>");
            var cb = doc.GetElementById("cb1");
            var label = doc.GetElementsByTagName("label").First();
            var d = new EventDispatcher(doc, new HitFor(label), new FakeUIClock());
            using var registry = new FormControlsRegistry(doc, d);

            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);

            Assert.That(cb.HasAttribute("checked"), Is.True,
                "registry auto-wires label-forward + checkbox-toggle so a label click checks the box");
        }

        [Test]
        public void Registry_auto_wires_select_so_click_opens_dropdown_and_picks() {
            // Audit fix: <select> was inert — it rendered the closed label but
            // clicking never opened the options. The registry must auto-wire a
            // SelectController that opens a popup and writes the pick back.
            var doc = HtmlParser.Parse(
                "<select id=\"s1\"><option>Alpha</option><option>Beta</option><option>Gamma</option></select>");
            var sel = doc.GetElementById("s1");
            var hit = new MutableHit(sel);
            var d = new EventDispatcher(doc, hit, new FakeUIClock());
            using var registry = new FormControlsRegistry(doc, d, null, _ => MakeTrackBox(sel, 0, 120));

            // Click the select → a popup listing all options appears.
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            var items = doc.GetElementsByClassName("ui-menu-item").ToList();
            Assert.That(items.Count, Is.EqualTo(3), "dropdown lists every option");

            // Click the 2nd option (Beta) → it becomes the selected option.
            hit.Target = items[1];
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);

            var selected = new SelectElement(sel).SelectedOption;
            Assert.That(selected, Is.Not.Null);
            Assert.That(selected.Label, Is.EqualTo("Beta"),
                "picking an option from the dropdown selects it");
        }

        [Test]
        public void Registry_auto_wires_details_so_summary_click_toggles_open() {
            // Audit fix: <details>/<summary> had UA styling but no toggle — clicking
            // the summary did nothing. The registry must auto-wire a DetailsController.
            var doc = HtmlParser.Parse("<details><summary>More</summary><p>Body</p></details>");
            var details = doc.GetElementsByTagName("details").First();
            var summary = doc.GetElementsByTagName("summary").First();
            var d = new EventDispatcher(doc, new HitFor(summary), new FakeUIClock());
            using var registry = new FormControlsRegistry(doc, d);

            Assert.That(details.HasAttribute("open"), Is.False, "starts closed");
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(details.HasAttribute("open"), Is.True, "summary click opens the disclosure");
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(details.HasAttribute("open"), Is.False, "second click closes it");
        }

        [Test]
        public void Registry_auto_wires_range_input_so_it_is_interactive() {
            // The FormControlsRegistry — NOT an explicit WireRange — must create
            // and Wire() a RangeController for a range input, so range sliders are
            // interactive out of the box (regression: RangeController existed but
            // was never instantiated, leaving sliders inert).
            var (doc, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"30\">");
            using var registry = new FormControlsRegistry(doc, d, null, _ => MakeTrackBox(input, 0, 200));
            d.DispatchKeyDown("ArrowUp", "ArrowUp", KeyModifiers.None, false);
            Assert.That(input.GetAttribute("value"), Is.EqualTo("40"),
                "registry-wired RangeController steps the value (30 → 40) on ArrowUp");
        }

        [Test]
        public void ArrowRight_increments_by_step() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"30\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void ArrowDown_decrements_by_step() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"30\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void ArrowLeft_decrements_by_step() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"30\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(20).Within(1e-9));
        }

        [Test]
        public void PageUp_moves_by_ten_times_step() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"1000\" step=\"5\" value=\"100\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("PageUp", "PageUp", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(150).Within(1e-9));
        }

        [Test]
        public void PageDown_moves_by_ten_times_step() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"1000\" step=\"5\" value=\"100\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("PageDown", "PageDown", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void Home_snaps_value_to_min() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"10\" max=\"100\" step=\"1\" value=\"55\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("Home", "Home", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void End_snaps_value_to_max() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"10\" max=\"100\" step=\"1\" value=\"55\">");
            var ctrl = WireRange(input, d);
            d.DispatchKeyDown("End", "End", KeyModifiers.None, false);
            Assert.That(ctrl.Value, Is.EqualTo(100).Within(1e-9));
        }

        // ---------- Events ----------

        [Test]
        public void Keyboard_step_fires_input_and_change() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"10\" value=\"30\">");
            var ctrl = WireRange(input, d);
            int inputs = 0, changes = 0;
            d.AddEventListener(input, EventKind.Input, _ => inputs++);
            d.AddEventListener(input, EventKind.Change, _ => changes++);

            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, false);

            Assert.That(inputs, Is.EqualTo(1), "input event should fire on value change");
            Assert.That(changes, Is.EqualTo(1), "change event should fire on commit");
            Assert.That(ctrl.Value, Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void Pointer_drag_fires_input_per_move_and_change_only_on_pointer_up() {
            var (_, input, d) = Setup("<input type=\"range\" min=\"0\" max=\"100\" step=\"any\" value=\"0\">");
            // Track box: 100px wide starting at x=0. Pointer at x=N -> value=N.
            var box = MakeTrackBox(input, 0, 100);
            var ctrl = WireRange(input, d, box);

            int inputs = 0, changes = 0;
            d.AddEventListener(input, EventKind.Input, _ => inputs++);
            d.AddEventListener(input, EventKind.Change, _ => changes++);

            // PointerDown sets value to click position (fires input).
            d.DispatchPointerDown(25, 10, 0, KeyModifiers.None);
            Assert.That(inputs, Is.EqualTo(1), "PointerDown should fire input once for the initial set");
            Assert.That(changes, Is.EqualTo(0), "change must NOT fire during drag");

            // Two intermediate moves: each fires input (value actually changes).
            d.DispatchPointerMove(50, 10, KeyModifiers.None);
            d.DispatchPointerMove(75, 10, KeyModifiers.None);
            Assert.That(inputs, Is.EqualTo(3), "each PointerMove that changes value fires input");
            Assert.That(changes, Is.EqualTo(0), "change must NOT fire during drag");

            // PointerUp commits — fires change exactly once (value moved from 0 to 75).
            d.DispatchPointerUp(75, 10, 0, KeyModifiers.None);
            Assert.That(changes, Is.EqualTo(1), "change fires once on PointerUp commit");
            Assert.That(ctrl.Value, Is.EqualTo(75).Within(1e-9));
        }
    }
}
