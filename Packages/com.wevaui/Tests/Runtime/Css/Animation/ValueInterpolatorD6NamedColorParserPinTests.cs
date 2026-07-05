using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // D6 — `ValueInterpolator.TryParseColorToken` (shadow tokenizer per-token
    // probe) previously contained a `v is CssIdentifier id &&
    // CssColor.TryFromName(id.Name, ...)` fallback clause. The audit
    // hypothesis was that the parser still returns `CssIdentifier` for color
    // names in some legacy paths. Inspection of `CssValueParser.cs:80-82`
    // (ParseSingle / Ident token) and `:1364-1369` (ParseColorComponent
    // inside color-mix) shows every named-color identifier path constructs a
    // `CssColor` directly via `CssColor.TryFromName`. The fallback clause
    // was therefore unreachable and has been removed.
    //
    // These pins guard against the parser regressing back to a CssIdentifier
    // return for named colors — if that ever happens, the deleted clause
    // would need to come back (or, preferably, the parser regression would
    // need to be fixed). The third pin documents that non-color identifiers
    // (e.g. `auto`, `inherit`) still parse as CssKeyword / CssIdentifier as
    // expected and are NOT silently coerced to colors.
    public class ValueInterpolatorD6NamedColorParserPinTests {
        [Test]
        public void Named_color_red_parses_as_CssColor_not_CssIdentifier() {
            var v = CssValueParser.Parse("red");
            Assert.That(v, Is.InstanceOf<CssColor>(),
                "CssValueParser.Parse(\"red\") must return CssColor directly; " +
                "if this fails, the deleted CssIdentifier fallback in " +
                "ValueInterpolator.TryParseColorToken must be restored.");
            var c = (CssColor)v;
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Named_color_blue_parses_as_CssColor_not_CssIdentifier() {
            var v = CssValueParser.Parse("blue");
            Assert.That(v, Is.InstanceOf<CssColor>());
            var c = (CssColor)v;
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Non_color_identifier_auto_does_not_become_CssColor() {
            // Reverse pin: `auto` is a CSS-wide keyword, NOT a color. The
            // parser must not coerce it to a color via the named-color path.
            var v = CssValueParser.Parse("auto");
            Assert.That(v, Is.Not.InstanceOf<CssColor>(),
                "Non-color identifiers must not be coerced to CssColor.");
        }
    }
}
