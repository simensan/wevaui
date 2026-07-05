using NUnit.Framework;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout.Grid {
    public class GridAreasParserTests {
        [Test]
        public void Parses_single_area_one_row() {
            var m = GridAreasParser.Parse("\"header\"");
            Assert.That(m.Rows, Is.EqualTo(1));
            Assert.That(m.Columns, Is.EqualTo(1));
            Assert.That(m.Areas, Contains.Key("header"));
            var area = m.Areas["header"];
            Assert.That(area.RowStart, Is.EqualTo(1));
            Assert.That(area.RowEnd, Is.EqualTo(2));
            Assert.That(area.ColumnStart, Is.EqualTo(1));
            Assert.That(area.ColumnEnd, Is.EqualTo(2));
        }

        [Test]
        public void Parses_holy_grail_layout() {
            const string s = "\"header header header\" \"nav main aside\" \"footer footer footer\"";
            var m = GridAreasParser.Parse(s);
            Assert.That(m.Rows, Is.EqualTo(3));
            Assert.That(m.Columns, Is.EqualTo(3));
            var header = m.Areas["header"];
            Assert.That(header.RowStart, Is.EqualTo(1));
            Assert.That(header.RowEnd, Is.EqualTo(2));
            Assert.That(header.ColumnStart, Is.EqualTo(1));
            Assert.That(header.ColumnEnd, Is.EqualTo(4));
            var main = m.Areas["main"];
            Assert.That(main.RowStart, Is.EqualTo(2));
            Assert.That(main.ColumnStart, Is.EqualTo(2));
            Assert.That(main.ColumnEnd, Is.EqualTo(3));
        }

        [Test]
        public void Treats_dot_as_empty_cell() {
            var m = GridAreasParser.Parse("\"a . b\"");
            Assert.That(m.Columns, Is.EqualTo(3));
            Assert.That(m.Areas, Contains.Key("a"));
            Assert.That(m.Areas, Contains.Key("b"));
            Assert.That(m.Areas.ContainsKey("."), Is.False);
        }

        [Test]
        public void Validates_rectangular_named_region() {
            // Non-rectangular: 'a' appears in (0,0) and (1,1) but not (0,1) or (1,0).
            const string s = "\"a b\" \"b a\"";
            Assert.Throws<GridAreasParser.ParseException>(() => GridAreasParser.Parse(s));
        }

        [Test]
        public void Errors_on_mismatched_row_lengths() {
            const string s = "\"a a\" \"a a a\"";
            Assert.Throws<GridAreasParser.ParseException>(() => GridAreasParser.Parse(s));
        }

        [Test]
        public void Multi_row_named_area_has_correct_extents() {
            const string s = "\"a a\" \"a a\" \"b b\"";
            var m = GridAreasParser.Parse(s);
            var a = m.Areas["a"];
            Assert.That(a.RowStart, Is.EqualTo(1));
            Assert.That(a.RowEnd, Is.EqualTo(3));
            Assert.That(a.ColumnStart, Is.EqualTo(1));
            Assert.That(a.ColumnEnd, Is.EqualTo(3));
            var b = m.Areas["b"];
            Assert.That(b.RowStart, Is.EqualTo(3));
            Assert.That(b.RowEnd, Is.EqualTo(4));
        }

        [Test]
        public void Multi_dot_run_creates_distinct_empty_cells() {
            var m = GridAreasParser.Parse("\"a ... b\"");
            Assert.That(m.Columns, Is.EqualTo(5));
            Assert.That(m.Areas, Contains.Key("a"));
            Assert.That(m.Areas, Contains.Key("b"));
        }

        [Test]
        public void None_returns_empty_map() {
            var m = GridAreasParser.Parse("none");
            Assert.That(m.Rows, Is.EqualTo(0));
            Assert.That(m.Columns, Is.EqualTo(0));
        }
    }
}
