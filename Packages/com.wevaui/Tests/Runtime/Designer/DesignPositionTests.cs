using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Layout.Boxes;
using Weva.Tests.Layout;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for out-of-flow placement: an Absolute node pins to its parent box via
    /// top/right/bottom/left offsets (HUD badges, corner buttons, overlays). The parent
    /// auto-becomes the positioning context; offsets round-trip and the badge lands at the
    /// corner through the real engine.
    /// </summary>
    public class DesignPositionTests
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

        // --- Compilation ---

        [Test]
        public void Absolute_node_emits_position_absolute_and_offsets()
        {
            var n = new DesignNode("badge") { Position = Position.Absolute, OffTop = Dim.Of(8), OffRight = Dim.Of(8) };
            string css = Css(n);
            Assert.That(css, Does.Contain("position: absolute"));
            Assert.That(css, Does.Contain("top: 8px"));
            Assert.That(css, Does.Contain("right: 8px"));
        }

        [Test]
        public void Zero_offset_pins_to_edge()
        {
            // 0 is a real pin (distinct from unset), so it must be emitted.
            var n = new DesignNode("o") { Position = Position.Absolute, OffTop = Dim.Of(0), OffLeft = Dim.Of(0) };
            string css = Css(n);
            Assert.That(css, Does.Contain("top: 0px"));
            Assert.That(css, Does.Contain("left: 0px"));
        }

        [Test]
        public void Unpinned_edges_emit_nothing()
        {
            var n = new DesignNode("o") { Position = Position.Absolute, OffTop = Dim.Of(4) };
            string css = Css(n);
            Assert.That(css, Does.Not.Contain("bottom:"));
            Assert.That(css, Does.Not.Contain("left:"));
        }

        [Test]
        public void Offsets_resolve_spacing_tokens()
        {
            var n = new DesignNode("o") { Position = Position.Absolute, OffTop = Dim.Token("s") };
            var doc = new DesignDocument(n);
            doc.Tokens.Space("s", 4);
            Assert.That(doc.Compile().Css, Does.Contain("top: var(--space-s)"));
        }

        [Test]
        public void Parent_of_absolute_child_becomes_relative()
        {
            var parent = new DesignNode("card") { Layout = LayoutMode.Column };
            parent.Add(new DesignNode("badge") { Position = Position.Absolute, OffTop = Dim.Of(0) });
            // The parent (w0) gets position: relative so the child pins to it.
            Assert.That(Css(parent), Does.Contain("position: relative"));
        }

        [Test]
        public void In_flow_node_has_no_position()
        {
            var n = new DesignNode("box") { Layout = LayoutMode.Row };
            Assert.That(Css(n), Does.Not.Contain("position:"));
        }

        // --- Engine round-trip (verify for real) ---

        [Test]
        public void Badge_pins_to_parent_top_right_through_engine()
        {
            var card = new DesignNode("card") { Layout = LayoutMode.Column };
            card.SetFixedSize(200, 120);
            var badge = new DesignNode("badge") { Position = Position.Absolute, OffTop = Dim.Of(10), OffRight = Dim.Of(10) };
            badge.SetFixedSize(30, 30);
            card.Add(badge);

            DesignCompileResult r = new DesignDocument(card).Compile();
            var (root, _, _) = LayoutTestHelpers.Build(r.Html, r.Css, 800, 600);
            Box b = BoxByClass(root, "w1");
            var (bx, by) = LayoutTestHelpers.AbsoluteOrigin(b);
            var (cx, cy) = LayoutTestHelpers.AbsoluteOrigin(BoxByClass(root, "w0"));
            // Right edge = card right - 10; with a 30px badge, left = (cx+200-10) - 30 = cx+160.
            Assert.That(bx - cx, Is.EqualTo(160).Within(Tol));
            Assert.That(by - cy, Is.EqualTo(10).Within(Tol));
        }

        // --- Serializer ---

        [Test]
        public void Position_and_offsets_round_trip()
        {
            var root = new DesignNode("badge") { Position = Position.Absolute, OffTop = Dim.Of(0), OffRight = Dim.Token("s") };
            var doc = new DesignDocument(root);
            doc.Tokens.Space("s", 8);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Position, Is.EqualTo(Position.Absolute));
            Assert.That(reloaded.Root.OffTop, Is.EqualTo((Dim?)Dim.Of(0)));
            Assert.That(reloaded.Root.OffRight, Is.EqualTo((Dim?)Dim.Token("s")));
            Assert.That(reloaded.Root.OffBottom, Is.Null);
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        // --- Editor ---

        [Test]
        public void Editor_sets_position_and_offsets_with_undo()
        {
            var root = new DesignNode("badge");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetPosition(root, Position.Absolute);
            ed.SetOffsets(root, Dim.Of(4), null, null, Dim.Of(4));
            Assert.That(root.IsAbsolute, Is.True);
            Assert.That(root.OffTop, Is.EqualTo((Dim?)Dim.Of(4)));
            Assert.That(root.OffLeft, Is.EqualTo((Dim?)Dim.Of(4)));

            ed.Undo(); // offsets
            Assert.That(root.OffTop, Is.Null);
            ed.Undo(); // position
            Assert.That(root.IsAbsolute, Is.False);
        }

        // --- Clone ---

        [Test]
        public void Clone_copies_position_and_offsets()
        {
            var n = new DesignNode("badge") { Position = Position.Absolute, OffTop = Dim.Of(8), OffLeft = Dim.Of(8) };
            DesignNode c = n.Clone();
            Assert.That(c.Position, Is.EqualTo(Position.Absolute));
            Assert.That(c.OffTop, Is.EqualTo((Dim?)Dim.Of(8)));
            Assert.That(c.OffLeft, Is.EqualTo((Dim?)Dim.Of(8)));
        }
    }
}
