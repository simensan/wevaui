using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Inline {
    // Regression (weva-landing code panel): a text node that is a LONE newline
    // ("\n", no surrounding spaces) sitting between two inline elements inside a
    // `white-space: pre` block must produce a forced line break. The engine
    // dropped it — "}" and ".health {" rendered on the same line — while a
    // "\n  " (newline + spaces) node between elements broke correctly. CSS Text
    // L3 §4.1.2: under `pre`, every preserved newline is a forced break.
    public class PreLoneNewlineBreakTests {

        static int LineCount(Box root) => AllLineBoxes(root).Count();

        [Test]
        public void Lone_newline_between_inline_elements_forces_break_in_pre() {
            // <pre>}\n.health</pre> with the }/.health each wrapped in a span and
            // ONLY a newline between them. white-space:pre => two lines.
            const string html =
                "<pre class='p'><span class='a'>}</span>\n<span class='b'>.health</span></pre>";
            const string css = ".p { white-space: pre; font-family: monospace; }";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            Assert.That(LineCount(root), Is.EqualTo(2),
                "lone '\\n' between two inline elements under white-space:pre must force a line break");
        }

        [Test]
        public void Newline_with_trailing_spaces_between_inline_elements_breaks_in_pre() {
            // Control: "\n  " (newline + spaces) already worked — keep it green.
            const string html =
                "<pre class='p'><span class='a'>}</span>\n  <span class='b'>.health</span></pre>";
            const string css = ".p { white-space: pre; font-family: monospace; }";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            Assert.That(LineCount(root), Is.EqualTo(2));
        }

        [Test]
        public void Lone_newline_survives_flex_shrink_to_fit_relayout() {
            // The real failure: the <pre> sits in a display:flex item, so the
            // flex min-content probe re-lays the inline content at width=1 (all
            // wraps), then the line boxes are unwrapped and coalesced back via
            // SourceNode.Data. A lone "\n" produces a forced break but NO text
            // fragment, so it must not be lost from the box tree across that
            // re-layout — otherwise the break disappears.
            const string html =
                "<div class='flex'><pre class='p'><span class='a'>}</span>\n<span class='b'>.health</span></pre></div>";
            const string css =
                ".flex { display: flex; } .p { white-space: pre; font-family: monospace; }";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            Assert.That(LineCount(root), Is.EqualTo(2),
                "lone '\\n' break must survive flex shrink-to-fit re-layout");
        }

        [Test]
        public void Multiple_lone_newlines_in_pre_each_break() {
            // <pre>a\n\nb</pre> via spans: a, "\n", "\n", b => 3 lines (blank middle).
            const string html =
                "<pre class='p'><span>a</span>\n\n<span>b</span></pre>";
            const string css = ".p { white-space: pre; font-family: monospace; }";
            var (root, _, _) = Build(html, css, viewportWidth: 600);
            Assert.That(LineCount(root), Is.EqualTo(3),
                "two consecutive lone newlines produce a blank line between a and b");
        }
    }
}
