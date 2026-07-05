using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout.Grid {
    // DD7 / IF1 regression pins. CSS Grid L1 + CSS Cascade L5 invalid-at-
    // computed-value-time semantics: an unparseable track-list value reverts
    // to the property's initial value, NOT the cascaded value. Both the
    // grid-template-* and grid-auto-* properties must follow the same
    // disposition — the pre-fix code cleared grid-template-* but silently
    // preserved grid-auto-* on a ParseException, which is the bug DD7/IF1
    // tracked.
    public class GridContainerPropertiesParseFailureTests {
        static ComputedStyle Style(params (string, string)[] props) {
            var s = new ComputedStyle(new Element("div"));
            foreach (var (k, v) in props) s.Set(k, v);
            return s;
        }

        static LengthContext Lc => LengthContext.Default;

        // Existing-behaviour pin: an invalid grid-template-columns value
        // reverts to the initial value (Empty template). This was already the
        // contract before DD7 — keep it pinned so the consistency fix doesn't
        // accidentally regress the other direction.
        [Test]
        public void Invalid_grid_template_columns_reverts_to_empty() {
            var style = Style(("grid-template-columns", "xyzzy 100px"));
            var p = GridContainerProperties.From(style, Lc);
            Assert.That(p.Columns.Tracks.Count, Is.EqualTo(0),
                "grid-template-columns: <garbage> must clear to GridTemplate.Empty (initial value).");
        }

        // The DD7 / IF1 fix proper: grid-auto-columns parse failure used to
        // leave AutoColumns unchanged (i.e. the struct retained whatever
        // initial-fill we'd stamped at construction). After the fix the
        // failure path must explicitly reset to the initial-value sentinel
        // (`[ GridTrackSize.Auto ]`) — mirroring grid-template-columns above.
        [Test]
        public void Invalid_grid_auto_columns_reverts_to_initial_auto_track() {
            var style = Style(("grid-auto-columns", "xyzzy"));
            var p = GridContainerProperties.From(style, Lc);
            Assert.That(p.AutoColumns.Length, Is.EqualTo(1),
                "Initial value for grid-auto-columns is a single `auto` track.");
            Assert.That(p.AutoColumns[0].Kind, Is.EqualTo(GridTrackKind.Auto),
                "Parse failure must revert to GridTrackSize.Auto, not preserve the cascaded value.");
        }

        [Test]
        public void Invalid_grid_auto_rows_reverts_to_initial_auto_track() {
            var style = Style(("grid-auto-rows", "not-a-track"));
            var p = GridContainerProperties.From(style, Lc);
            Assert.That(p.AutoRows.Length, Is.EqualTo(1));
            Assert.That(p.AutoRows[0].Kind, Is.EqualTo(GridTrackKind.Auto));
        }

        // The author's mental model: setting grid-auto-columns to a valid
        // value then later re-setting it to garbage must NOT keep the earlier
        // valid value alive. The pre-fix code silently preserved the prior
        // AutoColumns assignment (well, the struct default — see comment on
        // the failure path). After the fix the last computed-value-time
        // failure wins by reverting to the initial value.
        [Test]
        public void Valid_then_invalid_grid_auto_columns_does_not_retain_prior_value() {
            // Sanity: a valid 200px track resolves correctly on its own.
            var ok = Style(("grid-auto-columns", "200px"));
            var pOk = GridContainerProperties.From(ok, Lc);
            Assert.That(pOk.AutoColumns.Length, Is.EqualTo(1));
            Assert.That(pOk.AutoColumns[0].Kind, Is.EqualTo(GridTrackKind.Length));
            Assert.That(pOk.AutoColumns[0].Value, Is.EqualTo(200));

            // Now the cascade re-resolves with a garbage value. The
            // computed-value-time failure must clear back to the initial
            // `auto` sentinel — NOT keep the previous 200px track.
            var style = Style(("grid-auto-columns", "200px"));
            style.Set("grid-auto-columns", "xyzzy");
            var p = GridContainerProperties.From(style, Lc);
            Assert.That(p.AutoColumns.Length, Is.EqualTo(1));
            Assert.That(p.AutoColumns[0].Kind, Is.EqualTo(GridTrackKind.Auto),
                "Re-setting to garbage must revert to initial, not preserve the prior 200px value.");
        }

        // Symmetry pin: same failure mode for both axes.
        [Test]
        public void Invalid_grid_auto_rows_matches_grid_template_rows_disposition() {
            var styleTpl = Style(("grid-template-rows", "garbage"));
            var styleAuto = Style(("grid-auto-rows", "garbage"));
            var pTpl = GridContainerProperties.From(styleTpl, Lc);
            var pAuto = GridContainerProperties.From(styleAuto, Lc);
            // grid-template-rows initial: Empty (zero tracks).
            Assert.That(pTpl.Rows.Tracks.Count, Is.EqualTo(0));
            // grid-auto-rows initial: a single `auto` track.
            Assert.That(pAuto.AutoRows.Length, Is.EqualTo(1));
            Assert.That(pAuto.AutoRows[0].Kind, Is.EqualTo(GridTrackKind.Auto));
        }
    }
}
