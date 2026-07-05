using NUnit.Framework;
using Weva.Css.Container;

namespace Weva.Tests.Css.Container {
    public class ContainerQueryParserTests {
        static ContainerContext Big => ContainerContext.InlineSize(800);
        static ContainerContext Small => ContainerContext.InlineSize(200);

        [Test]
        public void Empty_string_parses_to_empty_list_that_matches() {
            var r = ContainerQueryParser.Parse("");
            Assert.That(r.Name, Is.Null);
            Assert.That(r.Condition.Items.Count, Is.EqualTo(0));
            Assert.That(r.Condition.Evaluate(Big), Is.True);
        }

        [Test]
        public void Whitespace_only_parses_as_empty() {
            var r = ContainerQueryParser.Parse("   \t\n");
            Assert.That(r.Name, Is.Null);
            Assert.That(r.Condition.Items.Count, Is.EqualTo(0));
        }

        [Test]
        public void Min_width_feature_evaluates() {
            var r = ContainerQueryParser.Parse("(min-width: 600px)");
            Assert.That(r.Name, Is.Null);
            Assert.That(r.Condition.Evaluate(Big), Is.True);
            Assert.That(r.Condition.Evaluate(Small), Is.False);
        }

        [Test]
        public void Max_width_feature_evaluates() {
            var r = ContainerQueryParser.Parse("(max-width: 250px)");
            Assert.That(r.Condition.Evaluate(Big), Is.False);
            Assert.That(r.Condition.Evaluate(Small), Is.True);
        }

        [Test]
        public void Width_exact_match() {
            var r = ContainerQueryParser.Parse("(width: 100px)");
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(100)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(200)), Is.False);
        }

        [Test]
        public void Min_height_evaluates_against_size_container() {
            var r = ContainerQueryParser.Parse("(min-height: 300px)");
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(800, 400)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(800, 200)), Is.False);
            // Inline-size container has no block axis: height query never matches.
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(800)), Is.False);
        }

        [Test]
        public void Orientation_portrait_and_landscape() {
            var portrait = ContainerQueryParser.Parse("(orientation: portrait)").Condition;
            var landscape = ContainerQueryParser.Parse("(orientation: landscape)").Condition;
            Assert.That(landscape.Evaluate(ContainerContext.Size(800, 400)), Is.True);
            Assert.That(portrait.Evaluate(ContainerContext.Size(800, 400)), Is.False);
            Assert.That(portrait.Evaluate(ContainerContext.Size(400, 800)), Is.True);
        }

        [Test]
        public void Aspect_ratio_feature_parses() {
            var r = ContainerQueryParser.Parse("(aspect-ratio: 16/9)");
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(1600, 900)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(1024, 768)), Is.False);
        }

        [Test]
        public void Min_and_max_width_combined_with_and() {
            var r = ContainerQueryParser.Parse("(min-width: 600px) and (max-width: 1200px)");
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(800)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(500)), Is.False);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(1500)), Is.False);
        }

        [Test]
        public void Or_combinator_alternative() {
            var r = ContainerQueryParser.Parse("(min-width: 1200px) or (max-width: 200px)");
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(1500)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(150)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(500)), Is.False);
        }

        [Test]
        public void Not_query_inverts_match() {
            var r = ContainerQueryParser.Parse("not (min-width: 600px)");
            Assert.That(r.Condition.Evaluate(Big), Is.False);
            Assert.That(r.Condition.Evaluate(Small), Is.True);
        }

        [Test]
        public void Comma_creates_or_list() {
            var r = ContainerQueryParser.Parse("(min-width: 1200px), (max-width: 200px)");
            Assert.That(r.Condition.Items.Count, Is.EqualTo(2));
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(1500)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(150)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(500)), Is.False);
        }

        [Test]
        public void Named_query_parses_with_name() {
            var r = ContainerQueryParser.Parse("card (min-width: 600px)");
            Assert.That(r.Name, Is.EqualTo("card"));
            Assert.That(r.Condition.Evaluate(Big), Is.True);
            Assert.That(r.Condition.Evaluate(Small), Is.False);
        }

        [Test]
        public void Named_query_with_kebab_case_identifier() {
            var r = ContainerQueryParser.Parse("my-card (min-width: 100px)");
            Assert.That(r.Name, Is.EqualTo("my-card"));
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(120)), Is.True);
        }

        [Test]
        public void Not_keyword_is_not_treated_as_name() {
            var r = ContainerQueryParser.Parse("not (min-width: 600px)");
            Assert.That(r.Name, Is.Null);
            Assert.That(r.Condition.Items.Count, Is.EqualTo(1));
        }

        [Test]
        public void Inline_size_feature_alias_for_width() {
            var r = ContainerQueryParser.Parse("(min-inline-size: 400px)");
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(800)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(200)), Is.False);
        }

        [Test]
        public void Block_size_feature_alias_for_height() {
            var r = ContainerQueryParser.Parse("(min-block-size: 300px)");
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(800, 400)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(800, 200)), Is.False);
        }

        [Test]
        public void Unknown_feature_evaluates_false_but_parses() {
            var r = ContainerQueryParser.Parse("(banana: 5)");
            Assert.That(r.Condition.Items.Count, Is.EqualTo(1));
            Assert.That(r.Condition.Evaluate(ContainerContext.InlineSize(800)), Is.False);
        }

        [Test]
        public void Error_unmatched_paren_throws() {
            Assert.Throws<ContainerQueryParseException>(() => ContainerQueryParser.Parse("(min-width: 600px"));
        }

        [Test]
        public void Error_bare_ident_with_no_condition_throws() {
            // 'card' alone (no condition after) is not a complete query.
            Assert.Throws<ContainerQueryParseException>(() => ContainerQueryParser.Parse("card"));
        }

        [Test]
        public void Mixing_and_or_without_parens_throws() {
            Assert.Throws<ContainerQueryParseException>(
                () => ContainerQueryParser.Parse("(min-width: 600px) and (max-width: 1200px) or (orientation: portrait)"));
        }

        [Test]
        public void Aspect_ratio_with_min_prefix() {
            var r = ContainerQueryParser.Parse("(min-aspect-ratio: 4/3)");
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(1920, 1080)), Is.True);
            Assert.That(r.Condition.Evaluate(ContainerContext.Size(800, 1200)), Is.False);
        }
    }
}
