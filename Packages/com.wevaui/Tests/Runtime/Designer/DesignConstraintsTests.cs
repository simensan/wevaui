using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for min/max size constraints — the constraint-driven escape hatch on top
    /// of Fill/Hug/Fixed ("fill, but never exceed 400px"; "at least 200px"). They compile
    /// to min/max-width/height, must not duplicate the Fill min-width:0 floor, round-trip,
    /// are editable with undo, and — proven through the real engine — actually clamp.
    /// </summary>
    public class DesignConstraintsTests
    {
        const double Tol = 0.5;

        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;

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

        static Box Layout(DesignNode root, double vw = 800, double vh = 600)
        {
            DesignCompileResult r = new DesignDocument(root).Compile();
            var (layoutRoot, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, vw, vh);
            return layoutRoot;
        }

        // --- Compilation ---

        [Test]
        public void Min_and_max_emit_constraints()
        {
            var n = new DesignNode("box") { MinWidth = 100, MaxWidth = 400, MinHeight = 50, MaxHeight = 300 };
            string css = Css(n);
            Assert.That(css, Does.Contain("min-width: 100px"));
            Assert.That(css, Does.Contain("max-width: 400px"));
            Assert.That(css, Does.Contain("min-height: 50px"));
            Assert.That(css, Does.Contain("max-height: 300px"));
        }

        [Test]
        public void Unset_constraints_emit_nothing()
        {
            var n = new DesignNode("box") { Layout = LayoutMode.Column };
            string css = Css(n);
            Assert.That(css, Does.Not.Contain("min-width"));
            Assert.That(css, Does.Not.Contain("max-width"));
            Assert.That(css, Does.Not.Contain("max-height"));
        }

        [Test]
        public void Fill_child_min_width_zero_floor_is_emitted_when_no_user_min()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.Add(new DesignNode("fill").SetSize(SizeMode.Fill, SizeMode.Hug));
            // The Fill main-axis floor.
            Assert.That(Css(root), Does.Contain("min-width: 0"));
        }

        [Test]
        public void User_min_width_replaces_the_fill_floor_without_duplicating()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            var fill = new DesignNode("fill").SetSize(SizeMode.Fill, SizeMode.Hug);
            fill.MinWidth = 200;
            root.Add(fill);
            string css = Css(root);
            Assert.That(css, Does.Contain("min-width: 200px"));
            Assert.That(css, Does.Not.Contain("min-width: 0")); // no leftover floor
            // Exactly one min-width declaration on the child.
            int first = css.IndexOf("min-width", System.StringComparison.Ordinal);
            int last = css.LastIndexOf("min-width", System.StringComparison.Ordinal);
            Assert.That(first, Is.EqualTo(last));
        }

        // --- Engine round-trip (verify for real) ---

        [Test]
        public void Max_width_caps_a_fill_child_through_engine()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.SetFixedSize(800, 100);
            var fill = new DesignNode("fill").SetSize(SizeMode.Fill, SizeMode.Hug);
            fill.MaxWidth = 200; // would otherwise fill to 800
            root.Add(fill);
            Assert.That(BoxByClass(Layout(root), "w1").Width, Is.EqualTo(200).Within(Tol));
        }

        [Test]
        public void Min_width_floors_a_hug_child_through_engine()
        {
            var root = new DesignNode("row") { Layout = LayoutMode.Row };
            root.SetFixedSize(800, 100);
            var hug = new DesignNode("hug").SetSize(SizeMode.Hug, SizeMode.Hug); // empty → ~0 wide
            hug.MinWidth = 150;
            root.Add(hug);
            Assert.That(BoxByClass(Layout(root), "w1").Width, Is.EqualTo(150).Within(Tol));
        }

        // --- Serializer ---

        [Test]
        public void Constraints_round_trip_through_serializer()
        {
            var root = new DesignNode("box") { MinWidth = 100, MaxWidth = 400, MinHeight = 50, MaxHeight = 300 };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.MinWidth, Is.EqualTo(100).Within(1e-9));
            Assert.That(reloaded.Root.MaxWidth, Is.EqualTo(400).Within(1e-9));
            Assert.That(reloaded.Root.MinHeight, Is.EqualTo(50).Within(1e-9));
            Assert.That(reloaded.Root.MaxHeight, Is.EqualTo(300).Within(1e-9));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Editor ---

        [Test]
        public void Editor_sets_constraints_with_undo()
        {
            var root = new DesignNode("box");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetMaxWidth(root, 400);
            Assert.That(root.MaxWidth, Is.EqualTo(400).Within(1e-9));
            ed.Undo();
            Assert.That(root.MaxWidth, Is.EqualTo(0).Within(1e-9));
        }

        // --- Clone ---

        [Test]
        public void Clone_copies_constraints()
        {
            var n = new DesignNode("box") { MinWidth = 100, MaxWidth = 400, MinHeight = 50, MaxHeight = 300 };
            DesignNode c = n.Clone();
            Assert.That(c.MinWidth, Is.EqualTo(100).Within(1e-9));
            Assert.That(c.MaxHeight, Is.EqualTo(300).Within(1e-9));
        }
    }
}
