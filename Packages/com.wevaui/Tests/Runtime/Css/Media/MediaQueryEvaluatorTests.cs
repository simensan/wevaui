using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Media;

namespace Weva.Tests.Css.Media {
    public class MediaQueryEvaluatorTests {
        static MediaContext Ctx(double w, double h) => MediaContext.Default(w, h);

        [Test]
        public void Min_width_hits_when_viewport_is_larger() {
            var q = MediaQueryParser.Parse("(min-width: 600px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(800, 600)), Is.True);
        }

        [Test]
        public void Min_width_misses_when_viewport_is_smaller() {
            var q = MediaQueryParser.Parse("(min-width: 600px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(400, 600)), Is.False);
        }

        [Test]
        public void Max_width_hits_when_viewport_is_smaller_or_equal() {
            var q = MediaQueryParser.Parse("(max-width: 600px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(600, 600)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(400, 600)), Is.True);
        }

        [Test]
        public void Max_width_misses_when_viewport_is_larger() {
            var q = MediaQueryParser.Parse("(max-width: 600px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(800, 600)), Is.False);
        }

        [Test]
        public void Orientation_evaluates_against_aspect() {
            var portrait = MediaQueryParser.Parse("(orientation: portrait)");
            var landscape = MediaQueryParser.Parse("(orientation: landscape)");
            Assert.That(MediaQueryEvaluator.Evaluate(portrait, Ctx(400, 800)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(landscape, Ctx(400, 800)), Is.False);
            Assert.That(MediaQueryEvaluator.Evaluate(portrait, Ctx(1920, 1080)), Is.False);
            Assert.That(MediaQueryEvaluator.Evaluate(landscape, Ctx(1920, 1080)), Is.True);
        }

        [Test]
        public void Resolution_dppx_compares_against_dpi() {
            var q = MediaQueryParser.Parse("(min-resolution: 2dppx)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithDpi(192)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithDpi(96)), Is.False);
        }

        [Test]
        public void Resolution_dpi_compares_against_dpi() {
            var q = MediaQueryParser.Parse("(min-resolution: 150dpi)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithDpi(150)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithDpi(96)), Is.False);
        }

        [Test]
        public void Prefers_color_scheme_dark_matches_only_when_dark() {
            var q = MediaQueryParser.Parse("(prefers-color-scheme: dark)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithColorScheme(ColorScheme.Dark)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1)), Is.False);
        }

        [Test]
        public void Hover_none_matches_only_when_no_hover() {
            var q = MediaQueryParser.Parse("(hover: none)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithHover(HoverCapability.None)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1)), Is.False);
        }

        [Test]
        public void Pointer_coarse_matches_only_with_coarse_pointer() {
            var q = MediaQueryParser.Parse("(pointer: coarse)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithPointer(PointerCapability.Coarse)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1)), Is.False);
        }

        [Test]
        public void And_query_requires_all_children() {
            var q = MediaQueryParser.Parse("(min-width: 600px) and (max-width: 1200px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(800, 600)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(500, 600)), Is.False);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1500, 600)), Is.False);
        }

        [Test]
        public void Not_query_inverts_match() {
            var q = MediaQueryParser.Parse("not (min-width: 600px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(800, 600)), Is.False);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(400, 600)), Is.True);
        }

        [Test]
        public void Top_level_comma_is_or() {
            var list = MediaQueryParser.Parse("(min-width: 1200px), (orientation: portrait)");
            Assert.That(MediaQueryEvaluator.Evaluate(list, Ctx(1920, 1080)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(list, Ctx(400, 800)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(list, Ctx(800, 600)), Is.False);
        }

        [Test]
        public void Type_screen_matches_when_context_is_screen() {
            var q = MediaQueryParser.Parse("screen");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1)), Is.True);
        }

        [Test]
        public void Type_print_does_not_match_screen_context() {
            var q = MediaQueryParser.Parse("print");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1)), Is.False);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithType(MediaType.Print)), Is.True);
        }

        [Test]
        public void Unknown_feature_evaluates_false() {
            var q = MediaQueryParser.Parse("(banana: 5)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1920, 1080)), Is.False);
        }

        [Test]
        public void Empty_list_evaluates_true() {
            var list = MediaQueryParser.Parse("");
            Assert.That(MediaQueryEvaluator.Evaluate(list, Ctx(0, 0)), Is.True);
        }

        [Test]
        public void Combined_screen_and_min_width() {
            var q = MediaQueryParser.Parse("screen and (min-width: 600px)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(800, 600)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(800, 600).WithType(MediaType.Print)), Is.False);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(400, 600)), Is.False);
        }

        [Test]
        public void Aspect_ratio_evaluates() {
            var q = MediaQueryParser.Parse("(aspect-ratio: 16/9)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1600, 900)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1024, 768)), Is.False);
        }

        [Test]
        public void Hover_boolean_matches_when_capability_present() {
            var q = MediaQueryParser.Parse("(hover)");
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate(q, Ctx(1, 1).WithHover(HoverCapability.None)), Is.False);
        }

        [Test]
        public void Static_evaluator_handles_null_query_as_true() {
            Assert.That(MediaQueryEvaluator.Evaluate((MediaQuery)null, Ctx(1, 1)), Is.True);
            Assert.That(MediaQueryEvaluator.Evaluate((MediaQueryList)null, Ctx(1, 1)), Is.True);
        }
    }
}
