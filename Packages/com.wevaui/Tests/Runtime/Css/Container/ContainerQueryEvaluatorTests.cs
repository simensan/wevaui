using NUnit.Framework;
using Weva.Css.Container;

namespace Weva.Tests.Css.Container {
    public class ContainerQueryEvaluatorTests {
        static ContainerContext InlineSize(double w) => ContainerContext.InlineSize(w);
        static ContainerContext Size(double w, double h) => ContainerContext.Size(w, h);

        [Test]
        public void Min_width_hits_when_inline_size_meets_threshold() {
            var q = ContainerQueryParser.ParseCondition("(min-width: 600px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(400)), Is.False);
        }

        [Test]
        public void Max_width_hits_when_inline_size_is_below_or_equal() {
            var q = ContainerQueryParser.ParseCondition("(max-width: 600px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(600)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(400)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.False);
        }

        [Test]
        public void Min_height_evaluates_against_size_container_only() {
            var q = ContainerQueryParser.ParseCondition("(min-height: 400px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(800, 500)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(800, 300)), Is.False);
            // InlineSize-only container has no block axis exposed.
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.False);
        }

        [Test]
        public void Orientation_landscape_when_inline_ge_block() {
            var q = ContainerQueryParser.ParseCondition("(orientation: landscape)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(1600, 900)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(900, 1600)), Is.False);
        }

        [Test]
        public void Orientation_requires_size_container() {
            var q = ContainerQueryParser.ParseCondition("(orientation: portrait)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.False);
        }

        [Test]
        public void Aspect_ratio_evaluates_against_size_container() {
            var q = ContainerQueryParser.ParseCondition("(aspect-ratio: 16/9)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(1600, 900)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(1024, 768)), Is.False);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.False);
        }

        [Test]
        public void And_query_requires_all_children() {
            var q = ContainerQueryParser.ParseCondition("(min-width: 600px) and (max-width: 1200px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(500)), Is.False);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(1500)), Is.False);
        }

        [Test]
        public void Or_query_matches_when_any_child_matches() {
            var q = ContainerQueryParser.ParseCondition("(min-width: 1200px) or (max-width: 200px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(1500)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(150)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(500)), Is.False);
        }

        [Test]
        public void Not_query_inverts_match() {
            var q = ContainerQueryParser.ParseCondition("not (min-width: 600px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.False);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(400)), Is.True);
        }

        [Test]
        public void None_context_makes_every_feature_false() {
            // No matching ancestor. ContainerFeatureQuery treats this as indeterminate => false.
            var q = ContainerQueryParser.ParseCondition("(min-width: 0px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, ContainerContext.None), Is.False);
            // not(false) = true. The `not` operator at the query level still negates.
            var notQ = ContainerQueryParser.ParseCondition("not (min-width: 0px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(notQ, ContainerContext.None), Is.True);
        }

        [Test]
        public void Empty_list_evaluates_true() {
            var list = ContainerQueryParser.ParseCondition("");
            Assert.That(ContainerQueryEvaluator.Evaluate(list, InlineSize(800)), Is.True);
        }

        [Test]
        public void Static_evaluator_handles_null_query_as_true() {
            Assert.That(ContainerQueryEvaluator.Evaluate((ContainerQuery)null, InlineSize(1)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate((ContainerQueryList)null, InlineSize(1)), Is.True);
        }

        [Test]
        public void Top_level_comma_is_or() {
            var list = ContainerQueryParser.ParseCondition("(min-width: 1200px), (max-width: 200px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(list, InlineSize(1500)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(list, InlineSize(150)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(list, InlineSize(500)), Is.False);
        }

        [Test]
        public void Min_aspect_ratio_evaluates_when_size_container() {
            var q = ContainerQueryParser.ParseCondition("(min-aspect-ratio: 1/1)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(800, 600)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, Size(600, 800)), Is.False);
        }

        [Test]
        public void Inline_size_synonyms_for_width() {
            var q = ContainerQueryParser.ParseCondition("(min-inline-size: 600px)");
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(800)), Is.True);
            Assert.That(ContainerQueryEvaluator.Evaluate(q, InlineSize(400)), Is.False);
        }
    }
}
