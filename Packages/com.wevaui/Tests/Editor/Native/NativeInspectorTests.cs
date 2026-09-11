// The inspector surface of the C ABI (minor 27) from C#: tree navigation,
// the box model behind the border box, the matched rules in cascade order
// with the winner marked, and the whole computed style. This is what the
// Elements window needs to open a document hosted by the core.
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeInspectorTests
    {
        private NativeDocument _doc;
        private uint _a, _t, _s, _i, _b;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\" class=\"box\" style=\"color: red\"><p id=\"t\">text<span id=\"s\">x</span></p><i id=\"i\">y</i></div><div id=\"b\"></div>");
            _doc.SetCss("body{margin:0}#a{margin:5px 6px 7px 8px;padding:1px 2px 3px 4px;border:2px solid #000;width:100px;height:50px;--tone:warm}.box{color:blue}p{margin:0}");
            _doc.Update(0);
            _a = _doc.Query("#a"); _t = _doc.Query("#t"); _s = _doc.Query("#s"); _i = _doc.Query("#i"); _b = _doc.Query("#b");
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        [Test]
        public void Tree_ParentAndChildren()
        {
            Assert.That(_doc.Parent(_s), Is.EqualTo(_t));
            Assert.That(_doc.Parent(_t), Is.EqualTo(_a));
            Assert.That(_doc.Parent(_a), Is.EqualTo(_doc.Query("body")));
            Assert.That(_doc.Parent(_doc.Query("html")), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE));
            Assert.That(_doc.Children(_a), Is.EqualTo(new[] { _t, _i }));
            Assert.That(_doc.Children(_t), Is.EqualTo(new[] { _s }), "text nodes are not listed");
            Assert.That(_doc.Children(_b), Is.Empty);
            // The walk an Elements window makes: every element reachable from the root.
            int visited = 0;
            void Walk(uint e) { visited++; foreach (uint c in _doc.Children(e)) Walk(c); }
            Walk(_doc.Query("html"));
            Assert.That(visited, Is.EqualTo(8), "html, head, body, #a, #t, #s, #i, #b");
        }

        [Test]
        public void BoxModel_FourRectangles()
        {
            Assert.That(_doc.TryGetBoxModel(_a, out NativeDocument.BoxModel m));
            Assert.That((m.MarginTop, m.MarginRight, m.MarginBottom, m.MarginLeft), Is.EqualTo((5.0, 6.0, 7.0, 8.0)));
            Assert.That((m.BorderTop, m.BorderRight, m.BorderBottom, m.BorderLeft), Is.EqualTo((2.0, 2.0, 2.0, 2.0)));
            Assert.That((m.PaddingTop, m.PaddingRight, m.PaddingBottom, m.PaddingLeft), Is.EqualTo((1.0, 2.0, 3.0, 4.0)));
            Assert.That(_doc.TryGetBounds(_a, out NativeBounds b));
            Assert.That(m.ContentX, Is.EqualTo(b.X + 2 + 4));
            Assert.That(m.ContentY, Is.EqualTo(b.Y + 2 + 1));
            Assert.That(m.ContentWidth, Is.EqualTo(100));
            Assert.That(m.ContentHeight, Is.EqualTo(50));
            Assert.That(_doc.TryGetBoxModel(WevaNative.WEVA_ELEMENT_NONE, out _), Is.False);
        }

        [Test]
        public void MatchedRules_InCascadeOrder_WithTheWinnerMarked()
        {
            List<NativeDocument.MatchedRule> rules = _doc.MatchedRules(_a);
            Assert.That(rules.Count, Is.GreaterThan(5));
            NativeDocument.MatchedRule box = rules.Find(r => r.Selector == ".box" && r.Property == "color");
            Assert.That(box.Value, Is.EqualTo("blue"));
            Assert.That(box.Origin, Is.EqualTo("author"));
            Assert.That(box.Specificity, Is.EqualTo("0,1,0"));
            Assert.That(box.Applied, Is.False, "loses to the style attribute");
            NativeDocument.MatchedRule inline = rules.Find(r => r.Inline && r.Property == "color");
            Assert.That(inline.Value, Is.EqualTo("red"));
            Assert.That(inline.Applied, "the style attribute wins");
            Assert.That(inline.Source, Is.EqualTo(-1));
            Assert.That(rules.Find(r => r.Property == "margin-top").Value, Is.EqualTo("5px"), "shorthands are listed expanded");
            Assert.That(rules.Exists(r => r.Origin == "ua"), "the UA sheet's rules are listed too");
            Assert.That(rules.Find(r => r.Property == "--tone").Applied, "custom properties are declarations too");
            Assert.That(rules.IndexOf(box), Is.LessThan(rules.IndexOf(inline)), "cascade order: the winner comes last");
            Assert.That(_doc.MatchedRules(WevaNative.WEVA_ELEMENT_NONE), Is.Empty);
        }

        [Test]
        public void ComputedStyle_EveryPropertyThenCustomOnes()
        {
            List<KeyValuePair<string, string>> all = _doc.ComputedStyle(_s);
            Assert.That(all.Count, Is.GreaterThan(300), "every registered property is resolved");
            var map = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in all) map[kv.Key] = kv.Value;
            Assert.That(map["color"], Is.EqualTo("red"), "inherited from the style attribute up the tree");
            Assert.That(map["width"], Is.EqualTo("auto"), "an unset property answers its initial value");
            Assert.That(map["--tone"], Is.EqualTo("warm"), "custom properties inherit");
            Assert.That(all[all.Count - 1].Key, Is.EqualTo("--tone"), "custom properties come last");
            Assert.That(_doc.ComputedStyle(WevaNative.WEVA_ELEMENT_NONE), Is.Empty);
        }
    }
}
