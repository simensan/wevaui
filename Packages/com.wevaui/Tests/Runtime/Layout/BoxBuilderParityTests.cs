using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // The engine has TWO box builders. BoxBuilder walks a live DOM;
    // SnapshotBoxBuilder walks a DomSnapshot's flat node ids, and LayoutEngine
    // picks that one whenever `ctx.Snapshot` is set — which is the normal path,
    // and the one the headless layout dump and every LayoutTestHelpers.Build
    // test goes through. Their traversal differs by necessity; the SPEC RULES
    // they apply while traversing must not.
    //
    // They drifted. `position: absolute` blockifies an element's outer display
    // (CSS Display 3 §2.7); both builders handled only plain `inline`, so an
    // absolutely positioned <img> — inline-block by the UA sheet — stayed an
    // inline atom and shrink-to-fit sized to ZERO width. The rules now live in
    // BoxBuildRules, but shared code alone does not stop the next person from
    // adding a rule to one builder only.
    //
    // So this fixture is the guard: every case below is built through BOTH
    // builders and the resulting box trees are compared structurally. A rule
    // that lands in one builder and not the other fails here, on the case that
    // exercises it, rather than silently changing what the engine renders.
    public class BoxBuilderParityTests {
        // A box tree flattened to one line per box: shape, not geometry
        // (nothing here is laid out). Enough to catch a box that changed kind,
        // moved depth, gained a wrapper, or went missing.
        static string Shape(Box root) {
            var sb = new StringBuilder();
            Walk(root, 0, sb);
            return sb.ToString();
        }

        static void Walk(Box b, int depth, StringBuilder sb) {
            if (b == null) return;
            sb.Append(' ', depth * 2);
            sb.Append(b.GetType().Name);
            if (b.Element != null) {
                sb.Append(' ').Append(b.Element.TagName);
                string cls = b.Element.GetAttribute("class");
                if (!string.IsNullOrEmpty(cls)) sb.Append('.').Append(cls);
            }
            if (b is BlockBox bb) {
                if (bb.IsInlineBlock) sb.Append(" inline-block");
                if (bb.IsFloat) sb.Append(" float");
            }
            if (b is TextRun tr) sb.Append(" \"").Append(tr.Text).Append('"');
            sb.Append('\n');
            foreach (var c in b.Children) Walk(c, depth + 1, sb);
        }

        static void AssertBuildersAgree(string html, string css, string label) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent) };
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));

            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            ComputedStyle StyleOf(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            var liveRoot = new BoxBuilder(StyleOf).BuildDocument(doc);

            var snap = engine.LastSnapshot;
            Assert.That(snap, Is.Not.Null, label + ": the cascade produced no snapshot");
            var arr = SnapshotStyleArray.Build(snap, StyleOf);
            var snapRoot = new SnapshotBoxBuilder(arr.At).BuildFromSnapshot(snap);

            Assert.That(Shape(snapRoot), Is.EqualTo(Shape(liveRoot)),
                label + ": the two builders produced different box trees. A rule "
                + "was almost certainly added to one and not the other — put it "
                + "in BoxBuildRules so both see it.");
        }

        // Out-of-flow blockification across every inline-level display, which
        // is the rule that actually drifted.
        [Test]
        public void Builders_agree_on_out_of_flow_blockification() {
            const string css = @"
                .cb { position: relative; width: 80px; height: 80px; }
                .abs { position: absolute; left: 0; top: 0; width: 20px; height: 20px; }
                .ib   { display: inline-block; }
                .ifl  { display: inline-flex; }
                .igr  { display: inline-grid; }
                .itb  { display: inline-table; }
                .inl  { display: inline; }
                .flt  { float: left; width: 20px; height: 20px; }
            ";
            AssertBuildersAgree(
                "<div class=\"cb\"><img class=\"abs\" src=\"\" alt=\"\"/></div>"
                + "<div class=\"cb\"><span class=\"abs ib\">x</span></div>"
                + "<div class=\"cb\"><span class=\"abs ifl\">x</span></div>"
                + "<div class=\"cb\"><span class=\"abs igr\">x</span></div>"
                + "<div class=\"cb\"><span class=\"abs itb\">x</span></div>"
                + "<div class=\"cb\"><span class=\"abs inl\">x</span></div>"
                + "<div class=\"cb\"><button class=\"abs\">b</button></div>"
                + "<div class=\"cb\"><input class=\"abs\"/></div>"
                + "<div class=\"cb\"><span class=\"flt ib\">f</span>after</div>",
                css, "out-of-flow blockification");
        }

        // Anonymous-block wrapping, inline splitting and the mixed-content
        // rules — the other place the two walks could disagree.
        [Test]
        public void Builders_agree_on_anonymous_wrapping_and_inline_splitting() {
            AssertBuildersAgree(
                "<div>text<div>block</div>more text</div>"
                + "<div><span>inline <div>block inside inline</div> tail</span></div>"
                + "<div>   </div>"
                + "<p>a<br/>b</p>",
                ".x { }", "anonymous wrapping");
        }

        // Flex and grid blockify their in-flow children; both builders have
        // their own copy of that branch.
        [Test]
        public void Builders_agree_on_flex_and_grid_item_blockification() {
            const string css = @"
                .f { display: flex; }
                .g { display: grid; grid-template-columns: 1fr 1fr; }
                .ib { display: inline-block; }
            ";
            AssertBuildersAgree(
                "<div class=\"f\">bare text<span>inline</span><span class=\"ib\">ib</span>"
                + "<div>block</div></div>"
                + "<div class=\"g\">bare<span>inline</span><div>block</div></div>",
                css, "flex/grid item blockification");
        }

        // Tables, multicol and list markers each pick a box type from the
        // display value; both builders duplicate that mapping.
        [Test]
        public void Builders_agree_on_table_multicol_and_list_boxes() {
            const string css = @"
                .mc { column-count: 2; }
                .t  { display: table; }
                .tr { display: table-row; }
                .td { display: table-cell; }
            ";
            AssertBuildersAgree(
                "<div class=\"mc\">a b c</div>"
                + "<div class=\"t\"><div class=\"tr\"><div class=\"td\">cell</div></div></div>"
                + "<ul><li>one</li><li>two</li></ul>"
                + "<ol><li>first</li><li value=\"5\">fifth</li></ol>"
                + "<table><tr><td>real</td></tr></table>",
                css, "table/multicol/list boxes");
        }

        // `display: none` and `display: contents` remove or splice a box; a
        // builder that forgot either would produce a different tree.
        [Test]
        public void Builders_agree_on_none_and_contents() {
            AssertBuildersAgree(
                "<div><span style=\"display:none\">gone</span>kept</div>"
                + "<div style=\"display:contents\"><span>spliced</span></div>"
                + "<div style=\"display:contents\">bare text</div>",
                null, "none/contents");
        }
    }
}
