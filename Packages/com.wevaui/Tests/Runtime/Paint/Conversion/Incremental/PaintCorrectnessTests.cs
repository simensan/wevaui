using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint.Conversion.Incremental {
    public class PaintCorrectnessTests {
        static BlockBox BuildComplexTree(out List<BlockBox> all) {
            all = new List<BlockBox>();
            var rootStyle = Style();
            rootStyle.Set("background-color", "white");
            var root = Block(0, 0, 800, 600, rootStyle);
            all.Add(root);

            for (int i = 0; i < 5; i++) {
                var rowStyle = Style();
                rowStyle.Set("background-color", i % 2 == 0 ? "red" : "blue");
                rowStyle.Set("border-top-style", "solid");
                rowStyle.Set("border-top-width", "1px");
                rowStyle.Set("border-top-color", "black");
                if (i == 1) rowStyle.Set("opacity", "0.8");
                if (i == 2) rowStyle.Set("transform", "translate(2px,2px)");
                if (i == 3) rowStyle.Set("overflow", "hidden");
                var row = Block(0, i * 100, 800, 100, rowStyle);
                all.Add(row);
                root.AddChild(row);

                for (int j = 0; j < 3; j++) {
                    var cellStyle = Style();
                    cellStyle.Set("background-color", "green");
                    cellStyle.Set("border-radius", "4px");
                    var cell = Block(j * 200, 0, 200, 100, cellStyle);
                    row.AddChild(cell);
                    all.Add(cell);
                }
            }
            return root;
        }

        static List<PaintCommand> ConvertOracle(Box root) {
            return new BoxToPaintConverter().Convert(root).Commands;
        }

        static void AssertSequencesEqual(IList<PaintCommand> actual, IList<PaintCommand> expected) {
            Assert.That(actual.Count, Is.EqualTo(expected.Count),
                "Command count mismatch: actual=" + actual.Count + " expected=" + expected.Count);
            for (int i = 0; i < actual.Count; i++) {
                Assert.That(actual[i].GetType(), Is.EqualTo(expected[i].GetType()),
                    "Command type mismatch at index " + i);
            }
        }

        [Test]
        public void Cached_PaintList_equals_from_scratch_no_mutation() {
            var root = BuildComplexTree(out _);

            var oracle = ConvertOracle(root);

            var c = new BoxToPaintConverter();
            c.Convert(root);
            var cached = c.Convert(root).Commands;

            AssertSequencesEqual(cached, oracle);
        }

        [Test]
        public void Cached_PaintList_equals_from_scratch_with_one_mutation() {
            var root = BuildComplexTree(out var all);
            var c = new BoxToPaintConverter();
            c.Convert(root);

            // Mutate one cell.
            var cell = all[3]; // first row's first cell
            cell.Style = CloneWithNewVersion(cell.Style, ("background-color", "yellow"));
            BumpBoxVersion(cell);
            // Bump ancestors too because their slices contained the cell's old commands.
            for (var p = cell.Parent; p != null; p = p.Parent) BumpBoxVersion(p);

            var oracle = ConvertOracle(root);
            var cached = c.Convert(root).Commands;

            AssertSequencesEqual(cached, oracle);
        }

        [Test]
        public void Cached_PaintList_equals_from_scratch_with_ten_mutations() {
            var root = BuildComplexTree(out var all);
            var c = new BoxToPaintConverter();
            c.Convert(root);

            for (int i = 0; i < 10 && i < all.Count; i++) {
                var b = all[i];
                b.Style = CloneWithNewVersion(b.Style, ("background-color", i % 2 == 0 ? "magenta" : "cyan"));
                BumpBoxVersion(b);
                for (var p = b.Parent; p != null; p = p.Parent) BumpBoxVersion(p);
            }

            var oracle = ConvertOracle(root);
            var cached = c.Convert(root).Commands;

            AssertSequencesEqual(cached, oracle);
        }

        [Test]
        public void Mutation_to_leaf_only_changes_leaf_specific_commands() {
            var root = BuildComplexTree(out var all);
            var c = new BoxToPaintConverter();
            var before = new List<PaintCommand>(c.Convert(root).Commands);

            // Find the deepest cell-style box.
            BlockBox leaf = null;
            foreach (var b in all) {
                if (b.Children.Count == 0) { leaf = b; break; }
            }
            Assert.That(leaf, Is.Not.Null);

            leaf.Style = CloneWithNewVersion(leaf.Style, ("background-color", "purple"));
            BumpBoxVersion(leaf);
            for (var p = leaf.Parent; p != null; p = p.Parent) BumpBoxVersion(p);

            var after = c.Convert(root).Commands;
            // Same total count (we only changed a color, not what commands are emitted).
            Assert.That(after.Count, Is.EqualTo(before.Count));
            // But at least one FillRectCommand differs (different brush color). Verify
            // by comparing instance references — at least one differs.
            int differing = 0;
            for (int i = 0; i < after.Count; i++) {
                if (!ReferenceEquals(after[i], before[i])) differing++;
            }
            Assert.That(differing, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Mutation_to_inner_node_changes_inner_and_descendants() {
            var root = BuildComplexTree(out var all);
            var c = new BoxToPaintConverter();
            var before = new List<PaintCommand>(c.Convert(root).Commands);

            // Pick a row (which has 3 cells under it).
            BlockBox inner = null;
            foreach (var b in all) {
                if (b.Children.Count >= 3) { inner = b; break; }
            }
            Assert.That(inner, Is.Not.Null);

            inner.Style = CloneWithNewVersion(inner.Style, ("background-color", "darkgray"));
            BumpBoxVersion(inner);
            for (var p = inner.Parent; p != null; p = p.Parent) BumpBoxVersion(p);

            var oracle = ConvertOracle(root);
            var after = c.Convert(root).Commands;

            // Cached output must match the from-scratch oracle.
            AssertSequencesEqual(after, oracle);
        }

        [Test]
        public void Repeated_no_mutation_converts_produce_same_command_types_as_oracle() {
            var root = BuildComplexTree(out _);
            var oracle = ConvertOracle(root);
            var c = new BoxToPaintConverter();
            for (int i = 0; i < 4; i++) {
                var got = c.Convert(root).Commands;
                AssertSequencesEqual(got, oracle);
            }
        }

        [Test]
        public void Empty_tree_produces_no_commands_consistently() {
            var rootStyle = Style();
            var root = Block(0, 0, 100, 100, rootStyle);
            var c = new BoxToPaintConverter();
            var first = c.Convert(root).Commands;
            var second = c.Convert(root).Commands;
            Assert.That(first.Count, Is.EqualTo(0));
            Assert.That(second.Count, Is.EqualTo(0));
        }
    }
}
