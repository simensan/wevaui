using NUnit.Framework;
using Weva.Css.Cascade;

namespace Weva.Tests.Css.Cascade {
    // TG30 — EnvironmentVariables registration surface.
    //
    // EnvResolverTests covers env() *resolution* end-to-end (the consumer
    // of this registry). This file pins the registration *surface itself*:
    // Register / TryGetValue / Reset and the pre-registered defaults.
    //
    // Overlap note: the null-value coercion contract from NG2
    // (Register(name, null) -> stored as "") is already covered by
    // EnvironmentVariablesRegisterNullGuardTests and is intentionally
    // NOT duplicated here. The main-thread guard on Register / Reset
    // (RC8) is covered by EnvironmentVariablesThreadSafetyTests and is
    // likewise not duplicated.
    public class EnvironmentVariablesRegistrationTests {
        [SetUp]
        public void SetUp() {
            EnvironmentVariables.Reset();
        }

        [TearDown]
        public void TearDown() {
            EnvironmentVariables.Reset();
        }

        // ---- pre-registered defaults --------------------------------------

        [Test]
        public void Pre_registered_safe_area_insets_resolve_to_zero_after_Reset_TG30() {
            // The four safe-area-inset-* defaults must be present out of
            // the box (and after Reset()) so pasted-from-web stylesheets
            // resolve env(safe-area-inset-top) etc. without notch math
            // on platforms where the full screen is safe.
            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-top",    out var top),    Is.True);
            Assert.That(top,    Is.EqualTo("0px"));

            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-right",  out var right),  Is.True);
            Assert.That(right,  Is.EqualTo("0px"));

            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-bottom", out var bottom), Is.True);
            Assert.That(bottom, Is.EqualTo("0px"));

            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-left",   out var left),   Is.True);
            Assert.That(left,   Is.EqualTo("0px"));
        }

        // ---- Register: happy paths ----------------------------------------

        [Test]
        public void Register_new_variable_is_visible_via_TryGetValue_TG30() {
            EnvironmentVariables.Register("custom-var", "42px");
            Assert.That(EnvironmentVariables.TryGetValue("custom-var", out var v), Is.True);
            Assert.That(v, Is.EqualTo("42px"));
        }

        [Test]
        public void Register_overwrites_previous_value_for_same_name_TG30() {
            EnvironmentVariables.Register("custom-var", "1px");
            EnvironmentVariables.Register("custom-var", "2px");
            Assert.That(EnvironmentVariables.TryGetValue("custom-var", out var v), Is.True);
            Assert.That(v, Is.EqualTo("2px"));
        }

        // ---- TryGetValue: miss --------------------------------------------

        [Test]
        public void TryGetValue_for_unknown_name_returns_false_with_null_value_TG30() {
            // EnvResolver relies on TryGetValue's bool result to decide
            // whether to fall back to the env() fallback argument; a
            // never-registered name must be a clean miss with value=null
            // (not an empty string, which would mean "registered as "").
            Assert.That(EnvironmentVariables.TryGetValue("never-registered", out var v), Is.False);
            Assert.That(v, Is.Null);
        }

        // ---- Reset --------------------------------------------------------

        [Test]
        public void Reset_clears_author_registrations_and_restores_defaults_TG30() {
            // Author registration must be discardable, and Reset must put
            // the pre-registered defaults back so tests using SetUp/TearDown
            // on this registry get a clean known-good baseline.
            EnvironmentVariables.Register("game-hud-inset-top", "37px");
            Assert.That(EnvironmentVariables.TryGetValue("game-hud-inset-top", out _), Is.True,
                "precondition: author registration was accepted");

            EnvironmentVariables.Reset();

            Assert.That(EnvironmentVariables.TryGetValue("game-hud-inset-top", out var dead), Is.False,
                "Reset must drop author registrations");
            Assert.That(dead, Is.Null);

            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-top", out var top), Is.True,
                "Reset must restore pre-registered defaults");
            Assert.That(top, Is.EqualTo("0px"));
        }

        // ---- Register: name-side null/empty guard -------------------------

        [Test]
        public void Register_with_null_or_empty_name_is_a_silent_no_op_TG30() {
            // The early-out in Register prevents a null/empty key from ever
            // reaching the backing Dictionary<string,string> (Dictionary
            // would throw ArgumentNullException on a null key). The pre-
            // registered defaults must be unperturbed and no new entries
            // must show up under the empty-string key.
            Assert.DoesNotThrow(() => EnvironmentVariables.Register(null, "5px"));
            Assert.DoesNotThrow(() => EnvironmentVariables.Register("",   "5px"));

            // Empty-string name must not become a queryable entry.
            Assert.That(EnvironmentVariables.TryGetValue("", out var emptyVal), Is.False);
            Assert.That(emptyVal, Is.Null);

            // Defaults survive — proving Register didn't blow up the table.
            Assert.That(EnvironmentVariables.TryGetValue("safe-area-inset-top", out var top), Is.True);
            Assert.That(top, Is.EqualTo("0px"));
        }
    }
}
