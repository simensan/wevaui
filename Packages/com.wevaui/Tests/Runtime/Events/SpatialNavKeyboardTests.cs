using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Events {
    // W3 phase 2 — arrow keys (and, via the same entry point, gamepad
    // d-pad/stick) drive SpatialNavigator-based focus movement through
    // EventDispatcher.DispatchKeyDown. These tests run the REAL pipeline:
    // laid-out boxes + the dispatcher's provider wiring, exactly as
    // UIDocumentBuilder configures it.
    public class SpatialNavKeyboardTests {

        static (Box root, Document doc) BuildTree(string html, string css) {
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            Node n = AllBoxes(root).First(b => b.Element != null).Element;
            while (n.Parent != null) n = n.Parent;
            return (root, (Document)n);
        }

        static EventDispatcher Wire(Box root, Document doc) {
            var d = new EventDispatcher(doc, new FakeHitTester(), new FakeUIClock());
            d.RootBoxProvider = () => root;
            d.ElementToBox = e => AllBoxes(root)
                .FirstOrDefault(b => !(b is TextRun) && b.Element == e);
            return d;
        }

        const string RowCss =
            ".row { display: flex; gap: 10px; padding: 10px; }" +
            "button { width: 60px; height: 30px; }";
        const string RowHtml =
            "<div class='row'>" +
            "<button id='a' tabindex='0'>A</button>" +
            "<button id='b' tabindex='0'>B</button>" +
            "<button id='c' tabindex='0'>C</button>" +
            "</div>";

        [Test]
        public void ArrowRight_moves_focus_to_spatial_neighbor() {
            var (root, doc) = BuildTree(RowHtml, RowCss);
            var d = Wire(root, doc);
            d.Focus(doc.GetElementById("a"));
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("b"));
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("c"));
        }

        [Test]
        public void ArrowLeft_at_edge_keeps_focus() {
            var (root, doc) = BuildTree(RowHtml, RowCss);
            var d = Wire(root, doc);
            d.Focus(doc.GetElementById("a"));
            d.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("a"),
                "no candidate left of the leftmost button — focus stays");
        }

        [Test]
        public void Arrow_with_no_focus_enters_the_ui_at_first_focusable() {
            var (root, doc) = BuildTree(RowHtml, RowCss);
            var d = Wire(root, doc);
            Assert.That(d.FocusedElement, Is.Null);
            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("a"),
                "first d-pad/arrow press enters the UI (document-order first focusable)");
        }

        [Test]
        public void Text_editing_target_keeps_arrows_for_the_caret() {
            const string html =
                "<div class='row'>" +
                "<input id='field' tabindex='0' />" +
                "<button id='b' tabindex='0'>B</button>" +
                "</div>";
            var (root, doc) = BuildTree(html, RowCss + "input { width: 120px; height: 30px; }");
            var d = Wire(root, doc);
            d.Focus(doc.GetElementById("field"));
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("field"),
                "arrows move the caret inside inputs, never focus");
        }

        [Test]
        public void Flag_off_disables_arrow_navigation() {
            var (root, doc) = BuildTree(RowHtml, RowCss);
            var d = Wire(root, doc);
            d.ArrowKeySpatialNavigation = false;
            d.Focus(doc.GetElementById("a"));
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("a"));
        }

        [Test]
        public void PreventDefault_on_keydown_blocks_navigation() {
            var (root, doc) = BuildTree(RowHtml, RowCss);
            var d = Wire(root, doc);
            var a = doc.GetElementById("a");
            d.AddEventListener(a, EventKind.KeyDown, e => e.PreventDefault());
            d.Focus(a);
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("a"),
                "listener preventDefault opts the element out (same contract as Tab)");
        }

        [Test]
        public void Navigation_marks_focus_visible_like_tab() {
            var (root, doc) = BuildTree(RowHtml, RowCss);
            var d = Wire(root, doc);
            d.Focus(doc.GetElementById("a")); // programmatic: not keyboard
            d.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, repeat: false);
            // FocusInternal(keyboard: true) drives :focus-visible — assert via
            // the public API we have: the move happened through the keyboard
            // path (b focused). Pixel-level ring rendering is a paint concern.
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("b"));
        }

        [Test]
        public void Grid_down_navigates_to_the_row_below() {
            const string css =
                ".grid { display: grid; grid-template-columns: repeat(3, 70px); gap: 8px; padding: 8px; }" +
                "button { height: 30px; }";
            const string html =
                "<div class='grid'>" +
                "<button id='a' tabindex='0'>a</button><button id='b' tabindex='0'>b</button><button id='c' tabindex='0'>c</button>" +
                "<button id='d' tabindex='0'>d</button><button id='e' tabindex='0'>e</button><button id='f' tabindex='0'>f</button>" +
                "</div>";
            var (root, doc) = BuildTree(html, css);
            var d = Wire(root, doc);
            d.Focus(doc.GetElementById("b"));
            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, repeat: false);
            Assert.That(d.FocusedElement?.Id, Is.EqualTo("e"));
        }
    }
}
