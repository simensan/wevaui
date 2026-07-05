using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for interactivity polish: cursor:pointer (the clickable affordance) and a
    /// transition duration (smoothly animate into hover/pressed). Both default off, emit
    /// clean CSS, round-trip, and are editable with undo.
    /// </summary>
    public class DesignInteractivityTests
    {
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;

        [Test]
        public void Pointer_cursor_emits()
        {
            var n = new DesignNode("btn") { Cursor = Cursor.Pointer };
            Assert.That(Css(n), Does.Contain("cursor: pointer"));
        }

        [Test]
        public void Default_cursor_emits_nothing()
        {
            Assert.That(Css(new DesignNode("box") { Fill = "#fff" }), Does.Not.Contain("cursor"));
        }

        [Test]
        public void Transition_emits_all_with_duration_and_ease()
        {
            var n = new DesignNode("btn") { TransitionMs = 150 };
            Assert.That(Css(n), Does.Contain("transition: all 150ms ease"));
        }

        [Test]
        public void Zero_transition_emits_nothing()
        {
            Assert.That(Css(new DesignNode("box")), Does.Not.Contain("transition"));
        }

        [Test]
        public void Fractional_duration_formats_cleanly()
        {
            var n = new DesignNode("btn") { TransitionMs = 83.5 };
            Assert.That(Css(n), Does.Contain("transition: all 83.5ms ease"));
        }

        [Test]
        public void Transition_pairs_with_hover_state()
        {
            // The whole point: a transition + a hover override = a smooth hover.
            var n = new DesignNode("btn") { Fill = "#333", TransitionMs = 120, Cursor = Cursor.Pointer };
            n.State(InteractionState.Hover).Fill = "#555";
            string css = Css(n);
            Assert.That(css, Does.Contain("transition: all 120ms ease"));
            Assert.That(css, Does.Contain("cursor: pointer"));
            Assert.That(css, Does.Contain(".w0:hover {"));
        }

        [Test]
        public void Interactivity_round_trips_through_serializer()
        {
            var root = new DesignNode("btn") { Cursor = Cursor.Pointer, TransitionMs = 150 };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Cursor, Is.EqualTo(Cursor.Pointer));
            Assert.That(reloaded.Root.TransitionMs, Is.EqualTo(150).Within(1e-9));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Editor_sets_interactivity_with_undo()
        {
            var root = new DesignNode("btn");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetCursor(root, Cursor.Pointer);
            ed.SetTransition(root, 200);
            Assert.That(root.Cursor, Is.EqualTo(Cursor.Pointer));
            Assert.That(root.TransitionMs, Is.EqualTo(200).Within(1e-9));
            ed.Undo();
            Assert.That(root.TransitionMs, Is.EqualTo(0).Within(1e-9));
            ed.Undo();
            Assert.That(root.Cursor, Is.EqualTo(Cursor.Default));
        }

        [Test]
        public void Clone_copies_interactivity()
        {
            var n = new DesignNode("btn") { Cursor = Cursor.Pointer, TransitionMs = 150 };
            DesignNode c = n.Clone();
            Assert.That(c.Cursor, Is.EqualTo(Cursor.Pointer));
            Assert.That(c.TransitionMs, Is.EqualTo(150).Within(1e-9));
        }
    }
}
