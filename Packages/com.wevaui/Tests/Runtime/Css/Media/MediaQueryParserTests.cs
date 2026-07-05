using NUnit.Framework;
using Weva.Css.Media;

namespace Weva.Tests.Css.Media {
    public class MediaQueryParserTests {
        static MediaContext Big => MediaContext.Default(1920, 1080);
        static MediaContext Small => MediaContext.Default(400, 800);

        [Test]
        public void Empty_string_parses_to_empty_list_that_matches() {
            var list = MediaQueryParser.Parse("");
            Assert.That(list.Items.Count, Is.EqualTo(0));
            Assert.That(list.Evaluate(Big), Is.True);
        }

        [Test]
        public void Whitespace_only_parses_as_empty() {
            var list = MediaQueryParser.Parse("   \t\n");
            Assert.That(list.Items.Count, Is.EqualTo(0));
            Assert.That(list.Evaluate(Big), Is.True);
        }

        [Test]
        public void Type_screen_matches_screen_context() {
            var list = MediaQueryParser.Parse("screen");
            Assert.That(list.Evaluate(Big), Is.True);
        }

        [Test]
        public void Type_print_does_not_match_screen() {
            var list = MediaQueryParser.Parse("print");
            Assert.That(list.Evaluate(Big), Is.False);
        }

        [Test]
        public void Type_all_always_matches() {
            var list = MediaQueryParser.Parse("all");
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(Big.WithType(MediaType.Print)), Is.True);
        }

        [Test]
        public void Min_width_feature_evaluates() {
            var list = MediaQueryParser.Parse("(min-width: 600px)");
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(Small), Is.False);
        }

        [Test]
        public void Max_width_feature_evaluates() {
            var list = MediaQueryParser.Parse("(max-width: 600px)");
            Assert.That(list.Evaluate(Big), Is.False);
            Assert.That(list.Evaluate(Small), Is.True);
        }

        [Test]
        public void Equality_width_matches_only_exact() {
            var list = MediaQueryParser.Parse("(width: 1920px)");
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(Small), Is.False);
        }

        [Test]
        public void Min_height_evaluates() {
            var list = MediaQueryParser.Parse("(min-height: 700px)");
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(MediaContext.Default(400, 500)), Is.False);
        }

        [Test]
        public void Orientation_portrait_and_landscape() {
            var portrait = MediaQueryParser.Parse("(orientation: portrait)");
            var landscape = MediaQueryParser.Parse("(orientation: landscape)");
            Assert.That(portrait.Evaluate(Big), Is.False);
            Assert.That(landscape.Evaluate(Big), Is.True);
            Assert.That(portrait.Evaluate(Small), Is.True);
        }

        [Test]
        public void Prefers_color_scheme_dark() {
            var list = MediaQueryParser.Parse("(prefers-color-scheme: dark)");
            Assert.That(list.Evaluate(Big.WithColorScheme(ColorScheme.Dark)), Is.True);
            Assert.That(list.Evaluate(Big), Is.False);
        }

        [Test]
        public void Hover_with_value() {
            var hover = MediaQueryParser.Parse("(hover: hover)");
            var none = MediaQueryParser.Parse("(hover: none)");
            Assert.That(hover.Evaluate(Big), Is.True);
            Assert.That(none.Evaluate(Big), Is.False);
            Assert.That(none.Evaluate(Big.WithHover(HoverCapability.None)), Is.True);
        }

        [Test]
        public void Hover_boolean_form() {
            var list = MediaQueryParser.Parse("(hover)");
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(Big.WithHover(HoverCapability.None)), Is.False);
        }

        [Test]
        public void Pointer_fine_coarse_none() {
            var fine = MediaQueryParser.Parse("(pointer: fine)");
            var coarse = MediaQueryParser.Parse("(pointer: coarse)");
            var none = MediaQueryParser.Parse("(pointer: none)");
            Assert.That(fine.Evaluate(Big), Is.True);
            Assert.That(coarse.Evaluate(Big), Is.False);
            Assert.That(coarse.Evaluate(Big.WithPointer(PointerCapability.Coarse)), Is.True);
            Assert.That(none.Evaluate(Big.WithPointer(PointerCapability.None)), Is.True);
        }

        [Test]
        public void Prefers_reduced_motion_reduce() {
            var list = MediaQueryParser.Parse("(prefers-reduced-motion: reduce)");
            Assert.That(list.Evaluate(Big), Is.False);
            Assert.That(list.Evaluate(Big.WithReducedMotion(true)), Is.True);
        }

        [Test]
        public void Min_resolution_dppx_against_dpi_context() {
            var list = MediaQueryParser.Parse("(min-resolution: 2dppx)");
            Assert.That(list.Evaluate(Big.WithDpi(192)), Is.True);
            Assert.That(list.Evaluate(Big.WithDpi(96)), Is.False);
        }

        [Test]
        public void Min_resolution_dpi() {
            var list = MediaQueryParser.Parse("(min-resolution: 192dpi)");
            Assert.That(list.Evaluate(Big.WithDpi(192)), Is.True);
            Assert.That(list.Evaluate(Big.WithDpi(150)), Is.False);
        }

        [Test]
        public void Screen_and_min_width_combined() {
            var list = MediaQueryParser.Parse("screen and (min-width: 600px)");
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(Big.WithType(MediaType.Print)), Is.False);
            Assert.That(list.Evaluate(Small), Is.False);
        }

        [Test]
        public void Min_and_max_width_combined() {
            var list = MediaQueryParser.Parse("(min-width: 600px) and (max-width: 1200px)");
            Assert.That(list.Evaluate(MediaContext.Default(800, 600)), Is.True);
            Assert.That(list.Evaluate(MediaContext.Default(1500, 600)), Is.False);
            Assert.That(list.Evaluate(MediaContext.Default(500, 600)), Is.False);
        }

        [Test]
        public void Not_query_inverts_a_match() {
            var list = MediaQueryParser.Parse("not (min-width: 600px)");
            Assert.That(list.Evaluate(Big), Is.False);
            Assert.That(list.Evaluate(Small), Is.True);
        }

        [Test]
        public void Top_level_comma_creates_or_list() {
            var list = MediaQueryParser.Parse("screen and (min-width: 600px), (orientation: portrait)");
            Assert.That(list.Items.Count, Is.EqualTo(2));
            Assert.That(list.Evaluate(Big), Is.True);
            Assert.That(list.Evaluate(Small), Is.True);
            Assert.That(list.Evaluate(MediaContext.Default(400, 300).WithType(MediaType.Print)), Is.False);
        }

        [Test]
        public void Unknown_feature_parses_but_evaluates_false() {
            var list = MediaQueryParser.Parse("(banana: 5)");
            Assert.That(list.Items.Count, Is.EqualTo(1));
            Assert.That(list.Evaluate(Big), Is.False);
        }

        [Test]
        public void Aspect_ratio_feature_parses() {
            var list = MediaQueryParser.Parse("(aspect-ratio: 16/9)");
            Assert.That(list.Evaluate(MediaContext.Default(1600, 900)), Is.True);
            Assert.That(list.Evaluate(MediaContext.Default(1024, 768)), Is.False);
        }

        [Test]
        public void Min_aspect_ratio_evaluates() {
            var list = MediaQueryParser.Parse("(min-aspect-ratio: 4/3)");
            Assert.That(list.Evaluate(MediaContext.Default(1920, 1080)), Is.True);
            Assert.That(list.Evaluate(MediaContext.Default(800, 1200)), Is.False);
        }

        [Test]
        public void Only_keyword_treated_as_passthrough() {
            var list = MediaQueryParser.Parse("only screen and (min-width: 600px)");
            Assert.That(list.Evaluate(Big), Is.True);
        }

        [Test]
        public void Error_unmatched_paren_throws() {
            Assert.Throws<MediaQueryParseException>(() => MediaQueryParser.Parse("(min-width: 600px"));
        }

        [Test]
        public void Error_missing_value_after_colon_is_tolerated_or_throws() {
            // A colon with no value isn't well-formed, but our parser collects empty value
            // and the feature simply evaluates to false. Either behavior is acceptable; we
            // pick the lenient one and assert it does not throw.
            Assert.DoesNotThrow(() => MediaQueryParser.Parse("(min-width:)"));
            var list = MediaQueryParser.Parse("(min-width:)");
            Assert.That(list.Evaluate(MediaContext.Default(800, 600)), Is.False);
        }

        [Test]
        public void Error_malformed_length_evaluates_false() {
            var list = MediaQueryParser.Parse("(min-width: not-a-length)");
            Assert.That(list.Evaluate(MediaContext.Default(800, 600)), Is.False);
        }

        [Test]
        public void Error_unknown_type_throws() {
            Assert.Throws<MediaQueryParseException>(() => MediaQueryParser.Parse("banana"));
        }
    }
}
