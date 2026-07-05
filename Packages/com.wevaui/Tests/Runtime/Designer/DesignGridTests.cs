using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for Grid layout (was a half-wired enum value: serialized but the compiler
    /// emitted nothing, so a Grid node silently fell through to block flow). Grid compiles
    /// to a CSS grid with N equal minmax(0,1fr) columns + gap, round-trips, is editable
    /// with undo, and — proven through the real engine — actually splits its width.
    /// </summary>
    public class DesignGridTests
    {
        const double Tol = 0.5;

        static string Css(DesignNode root, System.Action<DesignTokens> tokens = null)
        {
            var doc = new DesignDocument(root);
            tokens?.Invoke(doc.Tokens);
            return doc.Compile().Css;
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

        // --- Compilation ---

        [Test]
        public void Grid_emits_display_grid_with_equal_columns()
        {
            var n = new DesignNode("g") { Layout = LayoutMode.Grid, GridColumns = 3 };
            string css = Css(n);
            Assert.That(css, Does.Contain("display: grid"));
            Assert.That(css, Does.Contain("grid-template-columns: repeat(3, minmax(0, 1fr))"));
        }

        [Test]
        public void Single_column_grid_emits_one_track()
        {
            var n = new DesignNode("g") { Layout = LayoutMode.Grid, GridColumns = 1 };
            Assert.That(Css(n), Does.Contain("grid-template-columns: minmax(0, 1fr)"));
        }

        [Test]
        public void Grid_without_column_count_defaults_to_single_track()
        {
            // ≤1 ⇒ safe single column rather than silently no display.
            var n = new DesignNode("g") { Layout = LayoutMode.Grid };
            string css = Css(n);
            Assert.That(css, Does.Contain("display: grid"));
            Assert.That(css, Does.Contain("grid-template-columns: minmax(0, 1fr)"));
        }

        [Test]
        public void Grid_gap_is_emitted()
        {
            var n = new DesignNode("g") { Layout = LayoutMode.Grid, GridColumns = 2, Gap = 12 };
            Assert.That(Css(n), Does.Contain("gap: 12px"));
        }

        [Test]
        public void Grid_gap_resolves_spacing_token()
        {
            var n = new DesignNode("g") { Layout = LayoutMode.Grid, GridColumns = 2, Gap = Dim.Token("m") };
            string css = Css(n, t => t.Space("m", 16));
            Assert.That(css, Does.Contain("gap: var(--space-m)"));
        }

        // --- Engine round-trip (verify for real) ---

        [Test]
        public void Three_column_grid_splits_width_through_engine()
        {
            var root = new DesignNode("grid") { Layout = LayoutMode.Grid, GridColumns = 3 };
            root.SetFixedSize(300, 100);
            for (int i = 0; i < 3; i++)
                root.Add(new DesignNode("cell" + i) { HeightMode = SizeMode.Fixed, Height = 40 });

            DesignCompileResult r = new DesignDocument(root).Compile();
            var (layoutRoot, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 800, 600);
            // 300 wide / 3 columns, no gap → 100 each.
            Assert.That(BoxByClass(layoutRoot, "w1").Width, Is.EqualTo(100).Within(Tol));
            Assert.That(BoxByClass(layoutRoot, "w2").Width, Is.EqualTo(100).Within(Tol));
            Assert.That(BoxByClass(layoutRoot, "w3").Width, Is.EqualTo(100).Within(Tol));
        }

        [Test]
        public void Grid_gap_shrinks_tracks_through_engine()
        {
            var root = new DesignNode("grid") { Layout = LayoutMode.Grid, GridColumns = 3, Gap = 30 };
            root.SetFixedSize(300, 100);
            for (int i = 0; i < 3; i++)
                root.Add(new DesignNode("cell" + i) { HeightMode = SizeMode.Fixed, Height = 40 });

            DesignCompileResult r = new DesignDocument(root).Compile();
            var (layoutRoot, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 800, 600);
            // (300 - 2*30 gaps) / 3 = 80 each.
            Assert.That(BoxByClass(layoutRoot, "w1").Width, Is.EqualTo(80).Within(Tol));
        }

        // --- Serializer ---

        [Test]
        public void Grid_round_trips_through_serializer()
        {
            var root = new DesignNode("g") { Layout = LayoutMode.Grid, GridColumns = 4, Gap = 8 };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Layout, Is.EqualTo(LayoutMode.Grid));
            Assert.That(reloaded.Root.GridColumns, Is.EqualTo(4));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Editor ---

        [Test]
        public void Editor_sets_grid_columns_with_undo()
        {
            var root = new DesignNode("g") { Layout = LayoutMode.Grid };
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetGridColumns(root, 3);
            Assert.That(root.GridColumns, Is.EqualTo(3));
            ed.Undo();
            Assert.That(root.GridColumns, Is.EqualTo(0));
        }

        // --- Clone ---

        [Test]
        public void Clone_copies_grid_columns()
        {
            var n = new DesignNode("g") { Layout = LayoutMode.Grid, GridColumns = 5 };
            Assert.That(n.Clone().GridColumns, Is.EqualTo(5));
        }
    }
}
