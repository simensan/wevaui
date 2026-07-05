using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the transform properties (paint-time, no layout effect): rotation
    /// (tilted badges) and uniform scale (emphasis pop). They compose into one transform,
    /// default off (rotation 0 / scale 1), round-trip and are editable with undo.
    /// </summary>
    public class DesignTransformTests
    {
        static string Css(DesignNode root) => new DesignDocument(root).Compile().Css;

        [Test]
        public void Rotation_emits_rotate()
        {
            var n = new DesignNode("badge") { Rotation = -12 };
            Assert.That(Css(n), Does.Contain("transform: rotate(-12deg)"));
        }

        [Test]
        public void Scale_emits_scale()
        {
            var n = new DesignNode("pop") { Scale = 1.1 };
            Assert.That(Css(n), Does.Contain("transform: scale(1.1)"));
        }

        [Test]
        public void Rotation_and_scale_compose()
        {
            var n = new DesignNode("badge") { Rotation = 8, Scale = 1.2 };
            Assert.That(Css(n), Does.Contain("transform: rotate(8deg) scale(1.2)"));
        }

        [Test]
        public void Defaults_emit_no_transform()
        {
            var n = new DesignNode("box") { Fill = "#fff" }; // Rotation 0, Scale 1
            Assert.That(Css(n), Does.Not.Contain("transform"));
        }

        [Test]
        public void Transform_round_trips_through_serializer()
        {
            var root = new DesignNode("badge") { Rotation = -12, Scale = 1.1 };
            var doc = new DesignDocument(root);
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Rotation, Is.EqualTo(-12).Within(1e-9));
            Assert.That(reloaded.Root.Scale, Is.EqualTo(1.1).Within(1e-9));
            Assert.That(reloaded.Compile().Css, Is.EqualTo(doc.Compile().Css));
        }

        [Test]
        public void Default_scale_survives_round_trip_as_one()
        {
            // Scale defaults to 1 (not 0) — a fresh node must reload as 1, not 0.
            var doc = new DesignDocument(new DesignNode("box"));
            DesignDocument reloaded = DesignSerializer.Deserialize(DesignSerializer.Serialize(doc));
            Assert.That(reloaded.Root.Scale, Is.EqualTo(1).Within(1e-9));
        }

        [Test]
        public void Editor_sets_transform_with_undo()
        {
            var root = new DesignNode("badge");
            var ed = new DocumentEditor(new DesignDocument(root));
            ed.SetRotation(root, 15);
            ed.SetScale(root, 1.25);
            Assert.That(root.Rotation, Is.EqualTo(15).Within(1e-9));
            Assert.That(root.Scale, Is.EqualTo(1.25).Within(1e-9));
            ed.Undo();
            Assert.That(root.Scale, Is.EqualTo(1).Within(1e-9));
            ed.Undo();
            Assert.That(root.Rotation, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void Clone_copies_transform()
        {
            var n = new DesignNode("badge") { Rotation = -12, Scale = 1.1 };
            DesignNode c = n.Clone();
            Assert.That(c.Rotation, Is.EqualTo(-12).Within(1e-9));
            Assert.That(c.Scale, Is.EqualTo(1.1).Within(1e-9));
        }
    }
}
