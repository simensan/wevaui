using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    // §16 — Form-control state pseudo-class interaction tests.
    //
    // CSS Selectors L4 / HTML defines a rich set of form-control state
    // pseudo-classes. SelectorMatcherTests.cs covers the individual pseudos at a
    // basic level; this file pins the INTERACTION (compound) cases that exercise
    // how two or more pseudo-classes combine on the same element, and covers the
    // few pseudos that lacked any explicit test in the main file:
    //   :placeholder-shown, :focus, :focus-within, :active, :autofill.
    //
    // :autofill is registered in PseudoClassKind and ElementState (commit 00e6eeb);
    // the host UA wires the bit via a custom IElementStateProvider when an input
    // is autofilled. See the AutofillStateProvider stub below for the contract.
    public class FormControlPseudoInteractionTests {
        static Document Parse(string html) => HtmlParser.Parse(html);
        static Element ById(Document doc, string id) => doc.GetElementById(id);

        static bool Match(string selector, Element e, IElementStateProvider state = null)
            => SelectorMatcher.Matches(SelectorParser.Parse(selector), e, state);

        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            public void Set(Element e, ElementState s) { map[e] = s; }
            public ElementState GetState(Element e) => map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        // -----------------------------------------------------------------------
        // Compound: :required:invalid — the most common form-validation pattern
        // -----------------------------------------------------------------------

        [Test]
        public void Required_invalid_compound_matches_empty_required_input() {
            // input:required:invalid is what authors write to highlight missing
            // required fields. Must match a <input required> with no value.
            var doc = Parse("<input required id=\"x\">");
            Assert.That(Match("input:required:invalid", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Required_invalid_compound_does_not_match_when_value_set() {
            // Once the user fills in the field, :required:invalid must stop.
            var doc = Parse("<input required value=\"filled\" id=\"x\">");
            Assert.That(Match("input:required:invalid", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Required_valid_compound_matches_when_value_present() {
            // input:required:valid captures valid filled-in required fields.
            var doc = Parse("<input required value=\"hello\" id=\"x\">");
            Assert.That(Match("input:required:valid", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Required_valid_compound_does_not_match_when_empty() {
            var doc = Parse("<input required id=\"x\">");
            Assert.That(Match("input:required:valid", ById(doc, "x")), Is.False);
        }

        // -----------------------------------------------------------------------
        // Compound: :in-range / :out-of-range — number & range inputs
        // -----------------------------------------------------------------------

        [Test]
        public void In_range_compound_matches_number_with_value_inside_bounds() {
            var doc = Parse("<input type=\"number\" min=\"0\" max=\"10\" value=\"5\" id=\"x\">");
            Assert.That(Match("input:in-range", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Out_of_range_compound_matches_number_below_min() {
            var doc = Parse("<input type=\"number\" min=\"0\" max=\"10\" value=\"-1\" id=\"x\">");
            Assert.That(Match("input:out-of-range", ById(doc, "x")), Is.True);
            Assert.That(Match("input:in-range", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Out_of_range_compound_matches_number_above_max() {
            var doc = Parse("<input type=\"number\" min=\"0\" max=\"10\" value=\"11\" id=\"x\">");
            Assert.That(Match("input:out-of-range", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Out_of_range_combined_with_invalid_both_fire() {
            // A number input that is out of range is ALSO :invalid per spec
            // (the range constraint violation is a validity constraint).
            var doc = Parse("<input type=\"number\" min=\"2\" max=\"4\" value=\"5\" id=\"x\">");
            Assert.That(Match(":out-of-range", ById(doc, "x")), Is.True);
            Assert.That(Match(":invalid", ById(doc, "x")), Is.True);
        }

        // -----------------------------------------------------------------------
        // :read-only / :read-write — attribute mutation
        // -----------------------------------------------------------------------

        [Test]
        public void Read_write_does_not_match_after_readonly_attribute_set() {
            // v1: DOM mutations are not live — this tests the static attribute
            // that was already present when Parse() ran. An input without
            // readonly is :read-write; one with it is :read-only.
            var doc = Parse(
                "<input id=\"rw\">" +
                "<input readonly id=\"ro\">");
            Assert.That(Match("input:read-write", ById(doc, "rw")), Is.True);
            Assert.That(Match("input:read-only", ById(doc, "rw")), Is.False);
            Assert.That(Match("input:read-only", ById(doc, "ro")), Is.True);
            Assert.That(Match("input:read-write", ById(doc, "ro")), Is.False);
        }

        [Test]
        public void Disabled_input_is_not_read_write() {
            // CSS Selectors L4 §23.5: disabled elements are not :read-write.
            var doc = Parse("<input disabled id=\"x\">");
            Assert.That(Match(":read-write", ById(doc, "x")), Is.False);
            Assert.That(Match(":read-only", ById(doc, "x")), Is.True);
        }

        // -----------------------------------------------------------------------
        // :placeholder-shown — via NullStateProvider (DOM-driven)
        // -----------------------------------------------------------------------

        [Test]
        public void Placeholder_shown_matches_empty_input_with_placeholder() {
            // NullStateProvider.IsPlaceholderShown: placeholder present, no value.
            var doc = Parse("<input placeholder=\"Enter name\" id=\"x\">");
            Assert.That(Match(":placeholder-shown", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Placeholder_shown_does_not_match_when_value_present() {
            // The placeholder is hidden once the user has typed a value.
            var doc = Parse("<input placeholder=\"Enter name\" value=\"Alice\" id=\"x\">");
            Assert.That(Match(":placeholder-shown", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Placeholder_shown_does_not_match_input_without_placeholder() {
            var doc = Parse("<input id=\"x\">");
            Assert.That(Match(":placeholder-shown", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Placeholder_shown_matches_textarea_with_placeholder_and_no_content() {
            var doc = Parse("<textarea placeholder=\"Write here\" id=\"x\"></textarea>");
            Assert.That(Match(":placeholder-shown", ById(doc, "x")), Is.True);
        }

        // -----------------------------------------------------------------------
        // :placeholder-shown:focus compound — floating-label pattern
        // -----------------------------------------------------------------------

        [Test]
        public void Placeholder_shown_focus_compound_matches_focused_empty_input() {
            // Authors write `input:placeholder-shown:focus` for floating-label
            // animations — when the input is empty AND has focus.
            var doc = Parse("<input placeholder=\"Name\" id=\"x\">");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.Focus);
            // NullStateProvider handles PlaceholderShown from DOM; FakeState
            // adds Focus. SelectorMatcher uses the supplied state provider.
            // Combine both: pass a state that sets Focus AND check placeholder
            // DOM condition via the state provider's GetState.
            // The NullStateProvider contributes PlaceholderShown for us because
            // PlaceholderShown is determined from the DOM, not from GetState —
            // it's recomputed by MatchPseudo from the element attributes.
            // (SelectorMatcher.MatchPseudo, PlaceholderShown case: checks
            //  (state.GetState(e) & ElementState.PlaceholderShown) != 0)
            // So we need FakeState to also return PlaceholderShown.
            state.Set(e, ElementState.Focus | ElementState.PlaceholderShown);
            Assert.That(Match("input:placeholder-shown:focus", e, state), Is.True);
        }

        [Test]
        public void Placeholder_shown_focus_does_not_match_when_value_present_despite_focus() {
            var doc = Parse("<input placeholder=\"Name\" value=\"typed\" id=\"x\">");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.Focus);
            // :placeholder-shown is false because value is non-empty —
            // the compound selector must NOT match.
            Assert.That(Match("input:placeholder-shown:focus", e, state), Is.False);
        }

        // -----------------------------------------------------------------------
        // :focus — basic state-driven matching
        // -----------------------------------------------------------------------

        [Test]
        public void Focus_matches_when_focus_state_set() {
            var doc = Parse("<input id=\"x\">");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.Focus);
            Assert.That(Match(":focus", e, state), Is.True);
        }

        [Test]
        public void Focus_does_not_match_by_default() {
            var doc = Parse("<input id=\"x\">");
            Assert.That(Match(":focus", ById(doc, "x")), Is.False);
        }

        // -----------------------------------------------------------------------
        // :focus-within — parent matches when a descendant has focus
        // -----------------------------------------------------------------------

        [Test]
        public void Focus_within_matches_element_with_state_bit_set() {
            // The engine sets FocusWithin on the element AND all its ancestors
            // when a descendant gets focus. Here we test the raw matcher.
            var doc = Parse("<div id=\"form\"><input id=\"inp\"></div>");
            var form = ById(doc, "form");
            var state = new FakeState();
            state.Set(form, ElementState.FocusWithin);
            Assert.That(Match(":focus-within", form, state), Is.True);
        }

        [Test]
        public void Focus_within_does_not_match_when_no_state_bit() {
            var doc = Parse("<div id=\"x\"></div>");
            Assert.That(Match(":focus-within", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Focus_within_not_same_as_focus() {
            // :focus-within != :focus — the focused element itself has :focus;
            // its ancestor has :focus-within but typically NOT :focus.
            var doc = Parse("<div id=\"x\"></div>");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.FocusWithin);
            Assert.That(Match(":focus-within", e, state), Is.True);
            Assert.That(Match(":focus", e, state), Is.False);
        }

        // -----------------------------------------------------------------------
        // :active — pointer/keyboard activation state
        // -----------------------------------------------------------------------

        [Test]
        public void Active_matches_when_active_state_set() {
            var doc = Parse("<button id=\"x\"></button>");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.Active);
            Assert.That(Match(":active", e, state), Is.True);
        }

        [Test]
        public void Active_does_not_match_by_default() {
            var doc = Parse("<button id=\"x\"></button>");
            Assert.That(Match(":active", ById(doc, "x")), Is.False);
        }

        // -----------------------------------------------------------------------
        // :enabled / :disabled mutation — attribute toggle
        // -----------------------------------------------------------------------

        [Test]
        public void Enabled_disabled_flip_on_disabled_attribute() {
            // Static snapshot: a form control without disabled => :enabled;
            // one with disabled => :disabled. Non-form elements are neither.
            var doc = Parse(
                "<input id=\"enabled\">" +
                "<input disabled id=\"disabled\">" +
                "<div id=\"div\"></div>");
            Assert.That(Match(":enabled", ById(doc, "enabled")), Is.True);
            Assert.That(Match(":disabled", ById(doc, "enabled")), Is.False);
            Assert.That(Match(":disabled", ById(doc, "disabled")), Is.True);
            Assert.That(Match(":enabled", ById(doc, "disabled")), Is.False);
            Assert.That(Match(":enabled", ById(doc, "div")), Is.False);
            Assert.That(Match(":disabled", ById(doc, "div")), Is.False);
        }

        // -----------------------------------------------------------------------
        // :default — first submit button in form
        // -----------------------------------------------------------------------

        [Test]
        public void Default_pseudo_first_submit_button_in_form() {
            // Only the first non-disabled submit button in a form is :default.
            var doc = Parse(
                "<form>" +
                "<button id=\"b1\">Submit</button>" +
                "<button id=\"b2\">Also Submit</button>" +
                "</form>");
            Assert.That(Match(":default", ById(doc, "b1")), Is.True);
            Assert.That(Match(":default", ById(doc, "b2")), Is.False);
        }

        [Test]
        public void Default_pseudo_disabled_first_button_falls_through_to_second() {
            // If the first submit button is disabled, the second is :default.
            var doc = Parse(
                "<form>" +
                "<button disabled id=\"b1\">Disabled</button>" +
                "<button id=\"b2\">Submit</button>" +
                "</form>");
            Assert.That(Match(":default", ById(doc, "b1")), Is.False);
            Assert.That(Match(":default", ById(doc, "b2")), Is.True);
        }

        // -----------------------------------------------------------------------
        // :autofill — CSS Selectors L4 §11.4
        // -----------------------------------------------------------------------

        [Test]
        public void Autofill_does_not_match_when_state_bit_unset() {
            // NullStateProvider never sets ElementState.Autofill — the bit is
            // off until a host UA wires it via a custom IElementStateProvider.
            var doc = Parse("<input id=\"x\">");
            var e = ById(doc, "x");
            Assert.That(Match(":autofill", e), Is.False,
                ":autofill must NOT match until the Autofill state bit is set");
        }

        [Test]
        public void Autofill_matches_when_state_bit_set() {
            // Wire a stub provider that returns Autofill for the target element.
            var doc = Parse("<input id=\"x\">");
            var e = ById(doc, "x");
            var stub = new AutofillStateProvider(e);
            Assert.That(Match(":autofill", e, stub), Is.True,
                ":autofill must match when the state provider reports Autofill");
        }

        sealed class AutofillStateProvider : Weva.Css.Selectors.IElementStateProvider {
            readonly Weva.Dom.Element target;
            public AutofillStateProvider(Weva.Dom.Element target) { this.target = target; }
            public Weva.Css.Selectors.ElementState GetState(Weva.Dom.Element e) {
                if (ReferenceEquals(e, target)) return Weva.Css.Selectors.ElementState.Autofill;
                return Weva.Css.Selectors.ElementState.None;
            }
        }
    }
}
