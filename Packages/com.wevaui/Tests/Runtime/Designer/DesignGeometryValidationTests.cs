using System.Collections.Generic;
using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Validation;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the geometry-mistake diagnostics: the sizing/placement properties make
    /// a few silent-mis-render traps easy to hit (max &lt; min, Fixed with no size, offsets
    /// on a non-absolute node, negative aspect ratio). The validator surfaces each so the
    /// editor can warn instead of letting the document quietly do the wrong thing.
    /// </summary>
    public class DesignGeometryValidationTests
    {
        static List<DesignDiagnostic> Validate(DesignNode root) => DesignValidator.Validate(new DesignDocument(root));
        static bool Has(List<DesignDiagnostic> d, string code) => d.Exists(x => x.Code == code);

        [Test]
        public void Max_width_below_min_width_is_flagged()
        {
            var n = new DesignNode("box") { MinWidth = 300, MaxWidth = 200 };
            Assert.That(Has(Validate(n), "max-below-min"), Is.True);
        }

        [Test]
        public void Max_height_below_min_height_is_flagged()
        {
            var n = new DesignNode("box") { MinHeight = 300, MaxHeight = 200 };
            Assert.That(Has(Validate(n), "max-below-min"), Is.True);
        }

        [Test]
        public void Max_above_min_is_clean()
        {
            var n = new DesignNode("box") { MinWidth = 100, MaxWidth = 400 };
            Assert.That(Has(Validate(n), "max-below-min"), Is.False);
        }

        [Test]
        public void Fixed_width_without_size_is_flagged()
        {
            var n = new DesignNode("box") { WidthMode = SizeMode.Fixed }; // Width left at 0
            Assert.That(Has(Validate(n), "fixed-without-size"), Is.True);
        }

        [Test]
        public void Fixed_width_with_size_is_clean()
        {
            var n = new DesignNode("box") { WidthMode = SizeMode.Fixed, Width = 120 };
            Assert.That(Has(Validate(n), "fixed-without-size"), Is.False);
        }

        [Test]
        public void Offsets_on_non_absolute_node_warn_as_info()
        {
            var n = new DesignNode("box") { OffTop = Dim.Of(8) }; // Position left InFlow
            List<DesignDiagnostic> d = Validate(n);
            Assert.That(Has(d, "offsets-without-absolute"), Is.True);
            Assert.That(d.Find(x => x.Code == "offsets-without-absolute").Severity, Is.EqualTo(DiagnosticSeverity.Info));
        }

        [Test]
        public void Offsets_on_absolute_node_are_clean()
        {
            var n = new DesignNode("badge") { Position = Position.Absolute, OffTop = Dim.Of(8) };
            Assert.That(Has(Validate(n), "offsets-without-absolute"), Is.False);
        }

        [Test]
        public void Negative_aspect_ratio_is_flagged()
        {
            var n = new DesignNode("box") { AspectRatio = -1 };
            Assert.That(Has(Validate(n), "invalid-aspect-ratio"), Is.True);
        }

        [Test]
        public void Geometry_diagnostics_are_not_errors()
        {
            // They are warnings/info, not errors — they shouldn't block a save/compile.
            var n = new DesignNode("box") { MinWidth = 300, MaxWidth = 200, WidthMode = SizeMode.Fixed };
            Assert.That(DesignValidator.HasErrors(Validate(n)), Is.False);
        }
    }
}
