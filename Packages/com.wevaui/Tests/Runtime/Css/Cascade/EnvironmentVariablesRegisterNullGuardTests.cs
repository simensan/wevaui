using NUnit.Framework;
using Weva.Css.Cascade;

namespace Weva.Tests.Css.Cascade {
    // NG2 — EnvironmentVariables.Register coerces a null value to the empty
    // string. CSS env() with an empty value resolves to "" which downstream
    // property parsers reject, but the dict never holds a null — so TryGetValue
    // can never return a "true with null" result that would NRE downstream
    // (e.g. in EnvResolver's substitution into the value stream).
    public class EnvironmentVariablesRegisterNullGuardTests {
        [SetUp]
        public void SetUp() {
            EnvironmentVariables.Reset();
        }

        [TearDown]
        public void TearDown() {
            EnvironmentVariables.Reset();
        }

        [Test]
        public void Register_with_null_value_coerces_to_empty_string_NG2() {
            EnvironmentVariables.Register("game-hud-inset-top", null);
            Assert.That(EnvironmentVariables.TryGetValue("game-hud-inset-top", out var v), Is.True);
            Assert.That(v, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Register_with_null_value_overwrites_previous_value_NG2() {
            EnvironmentVariables.Register("game-hud-inset-top", "12px");
            EnvironmentVariables.Register("game-hud-inset-top", null);
            Assert.That(EnvironmentVariables.TryGetValue("game-hud-inset-top", out var v), Is.True);
            Assert.That(v, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Register_happy_path_still_stores_value_NG2() {
            EnvironmentVariables.Register("game-hud-inset-top", "12px");
            Assert.That(EnvironmentVariables.TryGetValue("game-hud-inset-top", out var v), Is.True);
            Assert.That(v, Is.EqualTo("12px"));
        }
    }
}
