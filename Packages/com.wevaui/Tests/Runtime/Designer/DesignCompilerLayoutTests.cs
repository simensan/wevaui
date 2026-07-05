using NUnit.Framework;
using Weva.Designer;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Engine round-trip coverage: compile a Design Document, feed the emitted
    /// HTML/CSS through the real cascade + layout engine, and assert the resulting
    /// boxes. This proves the compiler's Fill/Hug/Fixed → flex lowering actually
    /// produces the intended layout — not just plausible-looking CSS strings.
    /// (Verify for real, don't overclaim.)
    /// </summary>
    public class DesignCompilerLayoutTests
    {
        const double Tol = 0.5;

        static Box Layout(DesignDocument doc, double vw = 800, double vh = 600)
        {
            DesignCompileResult r = doc.Compile();
            var (root, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, vw, vh);
            return root;
        }

        static Box BoxByClass(Box b, string cls)
        {
            if (b.Element != null)
                foreach (string c in b.Element.ClassList)
                    if (c == cls) return b;
            foreach (Box child in b.Children)
            {
                Box found = BoxByClass(child, cls);
                if (found != null) return found;
            }
            return null;
        }

        [Test]
        public void Root_fixed_width_is_honored_by_engine()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Row };
            root.SetFixedSize(300, 100);
            var box = BoxByClass(Layout(new DesignDocument(root)), "w0");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Width, Is.EqualTo(300).Within(Tol));
        }

        [Test]
        public void Two_fill_children_split_main_axis_equally()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.SetFixedSize(300, 100);
            root.Add(new DesignNode("a").SetSize(SizeMode.Fill, SizeMode.Hug));
            root.Add(new DesignNode("b").SetSize(SizeMode.Fill, SizeMode.Hug));

            Box layoutRoot = Layout(new DesignDocument(root));
            Box a = BoxByClass(layoutRoot, "w1");
            Box b = BoxByClass(layoutRoot, "w2");

            Assert.That(a.Width, Is.EqualTo(150).Within(Tol));
            Assert.That(b.Width, Is.EqualTo(150).Within(Tol));
        }

        [Test]
        public void Fixed_child_keeps_size_and_fill_sibling_takes_the_rest()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.SetFixedSize(300, 100);
            var fixedKid = new DesignNode("fixed");
            fixedKid.WidthMode = SizeMode.Fixed; fixedKid.Width = 80;
            fixedKid.HeightMode = SizeMode.Hug;
            root.Add(fixedKid);
            root.Add(new DesignNode("fill").SetSize(SizeMode.Fill, SizeMode.Hug));

            Box layoutRoot = Layout(new DesignDocument(root));
            Assert.That(BoxByClass(layoutRoot, "w1").Width, Is.EqualTo(80).Within(Tol));
            Assert.That(BoxByClass(layoutRoot, "w2").Width, Is.EqualTo(220).Within(Tol));
        }

        [Test]
        public void Padding_reduces_content_area_for_fill_child()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.SetFixedSize(300, 200);
            root.SetPadding(50);
            root.Add(new DesignNode("fill").SetSize(SizeMode.Fill, SizeMode.Hug));

            Box fill = BoxByClass(Layout(new DesignDocument(root)), "w1");
            Assert.That(fill.Width, Is.EqualTo(200).Within(Tol)); // 300 - 50 - 50
        }

        [Test]
        public void Column_gap_spaces_children_vertically()
        {
            var root = new DesignNode("col") { Layout = LayoutMode.Column, Gap = 20 };
            root.SetFixedSize(200, 400);
            var a = new DesignNode("a") { HeightMode = SizeMode.Fixed, Height = 100, WidthMode = SizeMode.Fill };
            var b = new DesignNode("b") { HeightMode = SizeMode.Fixed, Height = 100, WidthMode = SizeMode.Fill };
            root.Add(a).Add(b);

            Box layoutRoot = Layout(new DesignDocument(root));
            var (_, ay) = LayoutTestHelpers.AbsoluteOrigin(BoxByClass(layoutRoot, "w1"));
            var (_, by) = LayoutTestHelpers.AbsoluteOrigin(BoxByClass(layoutRoot, "w2"));

            Assert.That(BoxByClass(layoutRoot, "w1").Height, Is.EqualTo(100).Within(Tol));
            Assert.That(by - ay, Is.EqualTo(120).Within(Tol)); // 100 tall + 20 gap
        }

        [Test]
        public void Fill_cross_axis_stretches_child_to_container()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.SetFixedSize(300, 200);
            // Hug main (width), Fill cross (height) → align-self: stretch → full height.
            root.Add(new DesignNode("tall").SetSize(SizeMode.Hug, SizeMode.Fill));

            Box tall = BoxByClass(Layout(new DesignDocument(root)), "w1");
            Assert.That(tall.Height, Is.EqualTo(200).Within(Tol));
        }
    }
}
