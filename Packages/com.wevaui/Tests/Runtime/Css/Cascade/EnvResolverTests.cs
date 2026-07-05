using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Environment Variables Module Level 1 — env() function.
    //
    // env(<name> [, <fallback>]?) reads from a runtime registry rather
    // than the author-defined --custom-property table. Weva ships
    // safe-area-inset-{top,right,bottom,left} pre-registered to 0px so
    // pasted-from-web stylesheets resolve sensibly without notch math.
    // Game integrations register their own values via
    // EnvironmentVariables.Register.
    //
    // Tracker item H6.
    public class EnvResolverTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // Reset the global env table between tests so registrations in
        // one case don't bleed into another. The Reset call restores
        // the pre-registered safe-area-inset-* defaults.
        [SetUp]
        public void SetUp() {
            EnvironmentVariables.Reset();
        }

        [TearDown]
        public void TearDown() {
            EnvironmentVariables.Reset();
        }

        // (1) env(safe-area-inset-top) — name is pre-registered with
        //     default 0px. Used as a padding declaration to verify both
        //     parse + resolution land in the cascade output.
        [Test]
        public void Env_safe_area_inset_top_resolves_to_zero_default_H6() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(safe-area-inset-top); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("0px"));
        }

        // (2) env(unknown-name, 20px) — unregistered name, fallback honoured.
        [Test]
        public void Env_unknown_name_with_fallback_resolves_to_fallback_H6() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(notch-magic, 20px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("20px"));
        }

        // (3) env(unknown-name) with no fallback — invalid-at-
        //     computed-value-time per CSS Custom Properties L1 §3
        //     (which env() inherits via the env spec's reference to
        //     the same invalid-substitution machinery). padding-top is
        //     non-inherited, initial 0; declaration drops and the
        //     property reads the initial value.
        [Test]
        public void Env_unknown_name_no_fallback_becomes_invalid_at_computed_value_H6() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(does-not-exist); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // padding-top initial is "0" per CssProperties registration.
            Assert.That(cs.Get("padding-top"), Is.EqualTo(CssProperties.InitialValueOf("padding-top")));
        }

        // (4) After Register, the new value is what env() returns.
        //     Mirrors the iOS-shipping scenario where the host
        //     populates safe-area-inset-top at startup.
        [Test]
        public void Register_overrides_pre_registered_default_H6() {
            EnvironmentVariables.Register("safe-area-inset-top", "30px");
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(safe-area-inset-top); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("30px"));
        }

        // (5) env() with explicit fallback when the name IS registered
        //     uses the registered value, not the fallback — fallback
        //     only fires when the name is missing or invalid.
        [Test]
        public void Env_registered_name_ignores_fallback_H6() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { padding-top: env(safe-area-inset-top, 99px); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"), Is.EqualTo("0px"));
        }

        // (6) TryGetValue contract surface. The registry API needs to
        //     report registered values and signal missing ones — game
        //     code may need to introspect.
        [Test]
        public void TryGetValue_returns_true_for_preregistered_and_false_for_unknown_H6() {
            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-bottom", out var v1), Is.True);
            Assert.That(v1, Is.EqualTo("0px"));

            Assert.That(EnvironmentVariables.TryGetValue("does-not-exist", out var v2), Is.False);
            Assert.That(v2, Is.Null);

            EnvironmentVariables.Register("game-hud-inset-top", "12px");
            Assert.That(EnvironmentVariables.TryGetValue("game-hud-inset-top", out var v3), Is.True);
            Assert.That(v3, Is.EqualTo("12px"));
        }
    }
}
