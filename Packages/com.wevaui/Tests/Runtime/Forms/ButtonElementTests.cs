using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class ButtonElementTests {
        sealed class FixedHit : IHitTester {
            readonly Element only;
            public FixedHit(Element e) { only = e; }
            public Element HitTest(double x, double y) => only;
        }

        static Element NewButton() => new Element("button");
        static Document Html(string s) => HtmlParser.Parse(s);

        [Test]
        public void Wrapping_non_button_throws() {
            Assert.Throws<System.ArgumentException>(() => new ButtonElement(new Element("div")));
        }

        [Test]
        public void Type_defaults_to_submit_when_attribute_missing() {
            var b = new ButtonElement(NewButton());
            Assert.That(b.Type, Is.EqualTo("submit"));
            Assert.That(b.IsSubmit, Is.True);
            Assert.That(b.IsReset, Is.False);
        }

        [Test]
        public void Type_explicit_button_is_preserved() {
            var e = NewButton();
            e.SetAttribute("type", "button");
            var b = new ButtonElement(e);
            Assert.That(b.Type, Is.EqualTo("button"));
            Assert.That(b.IsSubmit, Is.False);
            Assert.That(b.IsReset, Is.False);
        }

        [Test]
        public void Type_explicit_reset_is_preserved() {
            var e = NewButton();
            e.SetAttribute("type", "reset");
            var b = new ButtonElement(e);
            Assert.That(b.Type, Is.EqualTo("reset"));
            Assert.That(b.IsReset, Is.True);
            Assert.That(b.IsSubmit, Is.False);
        }

        [Test]
        public void Type_round_trips_via_attribute() {
            var e = NewButton();
            var b = new ButtonElement(e);
            b.Type = "button";
            Assert.That(e.GetAttribute("type"), Is.EqualTo("button"));
            Assert.That(b.Type, Is.EqualTo("button"));
        }

        [Test]
        public void Type_null_set_falls_back_to_submit_default() {
            var b = new ButtonElement(NewButton());
            b.Type = null;
            // SetAttribute is invoked with "submit" per the setter contract.
            Assert.That(b.Type, Is.EqualTo("submit"));
        }

        [Test]
        public void Disabled_toggles_attribute_presence() {
            var e = NewButton();
            var b = new ButtonElement(e);
            Assert.That(b.Disabled, Is.False);
            b.Disabled = true;
            Assert.That(e.HasAttribute("disabled"), Is.True);
            b.Disabled = false;
            Assert.That(e.HasAttribute("disabled"), Is.False);
        }

        [Test]
        public void Disabled_reflects_existing_attribute() {
            var e = NewButton();
            e.SetAttribute("disabled", "");
            var b = new ButtonElement(e);
            Assert.That(b.Disabled, Is.True);
        }

        [Test]
        public void Name_round_trips_through_GetAttribute() {
            var e = NewButton();
            var b = new ButtonElement(e);
            b.Name = "submitBtn";
            Assert.That(e.GetAttribute("name"), Is.EqualTo("submitBtn"));
            Assert.That(b.Name, Is.EqualTo("submitBtn"));
        }

        [Test]
        public void Value_round_trips_through_GetAttribute() {
            var e = NewButton();
            var b = new ButtonElement(e);
            b.Value = "payload";
            Assert.That(e.GetAttribute("value"), Is.EqualTo("payload"));
            Assert.That(b.Value, Is.EqualTo("payload"));
        }

        [Test]
        public void Name_and_Value_default_to_empty_string_when_absent() {
            var b = new ButtonElement(NewButton());
            Assert.That(b.Name, Is.EqualTo(""));
            Assert.That(b.Value, Is.EqualTo(""));
        }

        [Test]
        public void FindEnclosingForm_returns_ancestor_form() {
            var doc = Html("<form id=\"f1\"><div><button id=\"b\">go</button></div></form>");
            var btn = doc.GetElementById("b");
            var form = doc.GetElementById("f1");
            var b = new ButtonElement(btn);
            Assert.That(b.FindEnclosingForm(), Is.SameAs(form));
        }

        [Test]
        public void FindEnclosingForm_returns_null_when_no_form_ancestor() {
            var doc = Html("<div><button id=\"b\">go</button></div>");
            var btn = doc.GetElementById("b");
            var b = new ButtonElement(btn);
            Assert.That(b.FindEnclosingForm(), Is.Null);
        }

        [Test]
        public void FindEnclosingForm_resolves_form_attribute_outside_subtree() {
            // HTML Living Standard §4.10.18.6: <button form="f1"> resolves to
            // <form id="f1"> by id even when the button is NOT a descendant of
            // the form.
            var doc = Html("<form id=\"f1\"></form><div><button id=\"b\" form=\"f1\">go</button></div>");
            var btn = doc.GetElementById("b");
            var form = doc.GetElementById("f1");
            var b = new ButtonElement(btn);
            Assert.That(b.FindEnclosingForm(), Is.SameAs(form));
        }

        [Test]
        public void FindEnclosingForm_with_missing_form_id_returns_null_no_fallback() {
            // Per spec, an explicit form=<id> overrides ancestor association.
            // If the id doesn't resolve, the element has NO form owner — there
            // is no fallback to the ancestor walk even if a <form> ancestor
            // exists.
            var doc = Html("<form id=\"f1\"><button id=\"b\" form=\"missing\">go</button></form>");
            var btn = doc.GetElementById("b");
            var b = new ButtonElement(btn);
            Assert.That(b.FindEnclosingForm(), Is.Null);
        }

        [Test]
        public void FindEnclosingForm_form_attribute_pointing_at_non_form_returns_null() {
            // If the id resolves but the target is not a <form>, the element
            // has no form owner (spec: "the first such element in tree order
            // that is a form element").
            var doc = Html("<div id=\"x\"></div><button id=\"b\" form=\"x\">go</button>");
            var btn = doc.GetElementById("b");
            var b = new ButtonElement(btn);
            Assert.That(b.FindEnclosingForm(), Is.Null);
        }

        [Test]
        public void FindEnclosingForm_without_form_attribute_uses_ancestor_walk() {
            // Regression pin: when no form= attribute is present, the existing
            // ancestor-walk behaviour is preserved.
            var doc = Html("<form id=\"f1\"><div><button id=\"b\">go</button></div></form>");
            var btn = doc.GetElementById("b");
            var form = doc.GetElementById("f1");
            var b = new ButtonElement(btn);
            Assert.That(b.FindEnclosingForm(), Is.SameAs(form));
        }

        [Test]
        public void Click_on_disabled_button_does_not_fire_click_handler() {
            // HTML spec §4.10.18.5: disabled form controls do not fire click
            // events. EventDispatcher gates dispatch on the disableable
            // form-control set (button/fieldset/input/optgroup/option/select/
            // textarea) when `disabled` is set on the target or any
            // ancestor of the click target.
            var doc = Html("<button id=\"b\" disabled>go</button>");
            var btn = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new FixedHit(btn), new FakeUIClock());
            int clicks = 0;
            d.AddEventListener(btn, EventKind.Click, _ => clicks++);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(0));
            // The Disabled property still reports true.
            Assert.That(new ButtonElement(btn).Disabled, Is.True);
        }

        [Test]
        public void Click_on_enabled_button_still_fires_click_handler_regression() {
            // Regression pin: the disabled-suppression must not affect plain
            // (non-disabled) form controls. Without this guard, a typo in the
            // attribute-presence check could silently nullify every click.
            var doc = Html("<button id=\"b\">go</button>");
            var btn = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new FixedHit(btn), new FakeUIClock());
            int clicks = 0;
            d.AddEventListener(btn, EventKind.Click, _ => clicks++);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void Click_on_anchor_with_disabled_attribute_still_fires_per_spec() {
            // `<a disabled>` is NOT in HTML's disableable form-control set —
            // `disabled` isn't a valid attribute on anchors. The presence of
            // the attribute must NOT suppress the click. This pins the
            // narrowness of the predicate against the over-broad
            // `HasAttribute("disabled")` shortcut.
            var doc = Html("<a id=\"a\" href=\"#x\" disabled>link</a>");
            var a = doc.GetElementById("a");
            var d = new EventDispatcher(doc, new FixedHit(a), new FakeUIClock());
            int clicks = 0;
            d.AddEventListener(a, EventKind.Click, _ => clicks++);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void Setting_disabled_mid_session_takes_effect_on_next_click() {
            // The predicate reads the live attribute on each dispatch, so
            // toggling `disabled` at runtime (e.g. a "loading" state on a
            // submit button) suppresses subsequent clicks immediately —
            // no listener re-registration, no cached state.
            var doc = Html("<button id=\"b\">go</button>");
            var btn = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new FixedHit(btn), new FakeUIClock());
            int clicks = 0;
            d.AddEventListener(btn, EventKind.Click, _ => clicks++);

            // First click: enabled - fires.
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(1));

            // Programmatically disable mid-session.
            new ButtonElement(btn).Disabled = true;

            // Second click: disabled - suppressed.
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(1));

            // Re-enable: clicks resume.
            new ButtonElement(btn).Disabled = false;
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(2));
        }

        [Test]
        public void Click_on_child_of_disabled_button_is_also_suppressed() {
            // Browsers cancel the activation even when the pointer hit a
            // descendant of the disabled control (e.g. an icon `<span>` inside
            // a `<button disabled>`). The ancestor-walk in the predicate
            // pins this — and matches how `RunClickDefaultAction` already
            // walks ancestors for `<a href>`.
            var doc = Html("<button id=\"b\" disabled><span id=\"icon\">x</span></button>");
            var icon = doc.GetElementById("icon");
            var btn = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new FixedHit(icon), new FakeUIClock());
            int clicks = 0;
            d.AddEventListener(btn, EventKind.Click, _ => clicks++);
            d.AddEventListener(icon, EventKind.Click, _ => clicks++);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(0));
        }

        [Test]
        public void PointerDown_and_PointerUp_on_disabled_button_still_fire() {
            // Per the gating rule: ONLY the high-level activation events
            // (click, submit) are suppressed on disabled form controls.
            // Low-level pointer events still flow so :hover/:active state
            // machines, tooltips, and drag-affordance UIs keep working.
            var doc = Html("<button id=\"b\" disabled>go</button>");
            var btn = doc.GetElementById("b");
            var d = new EventDispatcher(doc, new FixedHit(btn), new FakeUIClock());
            int downs = 0, ups = 0;
            d.AddEventListener(btn, EventKind.PointerDown, _ => downs++);
            d.AddEventListener(btn, EventKind.PointerUp, _ => ups++);
            d.DispatchPointerDown(0, 0, 0, KeyModifiers.None);
            d.DispatchPointerUp(0, 0, 0, KeyModifiers.None);
            Assert.That(downs, Is.EqualTo(1));
            Assert.That(ups, Is.EqualTo(1));
        }
    }
}
