using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class PopoverTests {
        static Element NewPop(string id = "p", string mode = null) {
            var e = new Element("div");
            e.SetAttribute("id", id);
            if (mode != null) e.SetAttribute("popover", mode);
            else e.SetAttribute("popover", "");
            return e;
        }

        [Test]
        public void Show_sets_data_popover_open_attribute() {
            var p = NewPop();
            var stack = new PopoverStack();
            stack.Show(p);
            Assert.That(p.HasAttribute(Popover.OpenAttr), Is.True);
            Assert.That(stack.Top, Is.SameAs(p));
        }

        [Test]
        public void Hide_removes_attribute_and_pops() {
            var p = NewPop();
            var stack = new PopoverStack();
            stack.Show(p);
            stack.Hide(p);
            Assert.That(p.HasAttribute(Popover.OpenAttr), Is.False);
            Assert.That(stack.Top, Is.Null);
        }

        [Test]
        public void Toggle_alternates_visibility() {
            var p = NewPop();
            var stack = new PopoverStack();
            stack.Toggle(p);
            Assert.That(stack.Count, Is.EqualTo(1));
            stack.Toggle(p);
            Assert.That(stack.Count, Is.EqualTo(0));
        }

        [Test]
        public void Show_is_idempotent_for_already_open() {
            var p = NewPop();
            var stack = new PopoverStack();
            stack.Show(p);
            stack.Show(p);
            Assert.That(stack.Count, Is.EqualTo(1));
        }

        [Test]
        public void HideTopAuto_skips_manual_popovers() {
            var auto = NewPop("a", "auto");
            var manual = NewPop("m", "manual");
            var stack = new PopoverStack();
            stack.Show(auto);
            stack.Show(manual);
            stack.HideTopAuto();
            // The manual at top is skipped; auto below is hidden.
            Assert.That(Popover.IsOpen(manual), Is.True);
            Assert.That(Popover.IsOpen(auto), Is.False);
        }

        [Test]
        public void Show_ignores_elements_without_popover_attribute() {
            var e = new Element("div");
            var stack = new PopoverStack();
            stack.Show(e);
            Assert.That(stack.Count, Is.EqualTo(0));
        }

        [Test]
        public void GetMode_defaults_to_auto_when_attribute_empty() {
            var e = new Element("div");
            e.SetAttribute("popover", "");
            Assert.That(Popover.GetMode(e), Is.EqualTo("auto"));
        }

        [Test]
        public void GetMode_returns_manual_when_specified() {
            var e = new Element("div");
            e.SetAttribute("popover", "manual");
            Assert.That(Popover.GetMode(e), Is.EqualTo("manual"));
        }

        [Test]
        public void Popover_open_pseudo_class_matches_when_shown() {
            var doc = HtmlParser.Parse("<main><div id='p' popover>x</div></main>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("[popover]:popover-open { color: red; }"))
            });
            var p = doc.GetElementById("p");
            // Initially closed: no rule match.
            Assert.That(engine.Compute(p).Get("color"), Is.Not.EqualTo("red"));
            // Show via stack and recompute.
            var stack = new PopoverStack();
            stack.Show(p);
            engine.InvalidateAll();
            Assert.That(engine.Compute(p).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Modal_pseudo_class_matches_after_ShowModal() {
            var doc = HtmlParser.Parse("<main><dialog id='d'>x</dialog></main>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse("dialog:modal { color: red; }"))
            });
            var d = doc.GetElementById("d");
            Assert.That(engine.Compute(d).Get("color"), Is.Not.EqualTo("red"));
            new DialogElement(d).ShowModal();
            engine.InvalidateAll();
            Assert.That(engine.Compute(d).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Multiple_popovers_track_top_correctly() {
            var a = NewPop("a");
            var b = NewPop("b");
            var stack = new PopoverStack();
            stack.Show(a);
            stack.Show(b);
            Assert.That(stack.Top, Is.SameAs(b));
            stack.Hide(b);
            Assert.That(stack.Top, Is.SameAs(a));
        }
    }
}
