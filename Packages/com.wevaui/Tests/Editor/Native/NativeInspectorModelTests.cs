// The Elements-panel model over a core-hosted document: the tree, search,
// selection with rule blocks winners-first, computed style and box model.
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeInspectorModelTests
    {
        private NativeDocument _doc;
        private NativeInspectorModel _model;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\" class=\"box big\" style=\"color: red\"><p id=\"t\">text<span id=\"s\">x</span></p></div><div id=\"b\"></div>");
            _doc.SetCss("body{margin:0}#a{padding:4px;width:100px;height:50px}.box{color:blue;padding:1px}");
            _doc.Update(0);
            _model = new NativeInspectorModel(_doc);
            _model.Rebuild();
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        [Test]
        public void Tree_IsTheDocument_WithLabels()
        {
            Assert.That(_model.Root, Is.Not.Null);
            Assert.That(_model.Root.Tag, Is.EqualTo("html"));
            Assert.That(_model.NodeCount, Is.EqualTo(7), "html, head, body, #a, #t, #s, #b");
            NativeInspectorModel.Node a = _model.Find(_doc.Query("#a"));
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Label, Is.EqualTo("div#a.box.big"));
            Assert.That(a.Depth, Is.EqualTo(2));
            Assert.That(a.Children.Count, Is.EqualTo(1));
            Assert.That(a.Children[0].Label, Is.EqualTo("p#t"));
            Assert.That(_model.Search("#a"), Has.Count.EqualTo(1));
            Assert.That(_model.Search("DIV"), Has.Count.EqualTo(2), "case-insensitive, document order");
            Assert.That(_model.Search(""), Is.Empty);
        }

        [Test]
        public void Select_GroupsRulesWinnersFirst()
        {
            uint a = _doc.Query("#a");
            _model.Select(a);
            Assert.That(_model.Selected, Is.EqualTo(a));
            Assert.That(_model.Rules.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(_model.Rules[0].Inline, "the style attribute block comes first (it won color)");
            Assert.That(_model.Rules[0].Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(_model.Rules[0].Declarations[0].Applied);
            NativeInspectorModel.RuleBlock box = _model.Rules.Find(b => b.Selector == ".box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Declarations.Count, Is.GreaterThanOrEqualTo(2), "color and the expanded padding");
            Assert.That(box.Declarations.Exists(d => d.Property == "color" && !d.Applied), ".box's color lost");
            Assert.That(box.Declarations.Exists(d => d.Property == "padding-top" && !d.Applied), "#a's padding beat .box's");
            NativeInspectorModel.RuleBlock id = _model.Rules.Find(b => b.Selector == "#a");
            Assert.That(id.Declarations.Exists(d => d.Property == "padding-top" && d.Applied));
            Assert.That(_model.Rules.IndexOf(id), Is.LessThan(_model.Rules.IndexOf(box)), "winners first");
            Assert.That(_model.Rules.Exists(b => b.Origin == "ua"), "the UA block is listed last-ish");
        }

        [Test]
        public void Select_ReadsComputedStyleAndBox()
        {
            uint a = _doc.Query("#a");
            _model.Select(a);
            var map = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in _model.Computed) map[kv.Key] = kv.Value;
            Assert.That(map["color"], Is.EqualTo("red"));
            Assert.That(map["width"], Is.EqualTo("100px"));
            Assert.That(_model.HasBox);
            Assert.That(_model.Box.PaddingTop, Is.EqualTo(4));
            Assert.That(_model.Box.ContentWidth, Is.EqualTo(100));
            Assert.That(_model.Bounds.Width, Is.EqualTo(108));
            Assert.That(_model.SelectAt(50, 20), "the point inside #a selects it");
            Assert.That(_doc.ElementContains(a, _model.Selected), "the deepest element under the point, inside #a");
            Assert.That(_model.SelectAt(390, 290), Is.EqualTo(_doc.ElementAt(390, 290) != WevaNative.WEVA_ELEMENT_NONE));
            _model.Select(WevaNative.WEVA_ELEMENT_NONE);
            Assert.That(_model.Rules, Is.Empty);
            Assert.That(_model.HasBox, Is.False);
        }

        [Test]
        public void Rebuild_KeepsTheSelectionWhenItSurvives()
        {
            uint a = _doc.Query("#a");
            _model.Select(a);
            _doc.SetCss("body{margin:0}#a{padding:9px}");
            _doc.Update(0);
            _model.Rebuild();
            Assert.That(_model.Selected, Is.EqualTo(a));
            Assert.That(_model.Box.PaddingTop, Is.EqualTo(9), "the selection was re-read");
            Assert.That(_model.NodeCount, Is.EqualTo(7));
        }
    }
}
