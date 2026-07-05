using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Parsing;

namespace Weva.Tests.Css.Animation {
    public class KeyframesResolverTests {
        static Stylesheet Css(string s) => CssParser.Parse(s);

        [Test]
        public void Find_by_name() {
            var sheet = Css("@keyframes spin { from { opacity: 0; } to { opacity: 1; } }");
            var resolver = new KeyframesResolver(new[] { sheet });
            var anim = resolver.ResolveByName("spin");
            Assert.That(anim, Is.Not.Null);
            Assert.That(anim.Keyframes.Count, Is.EqualTo(2));
            Assert.That(anim.Keyframes[0].Properties["opacity"], Is.EqualTo("0"));
        }

        [Test]
        public void Missing_name_returns_null() {
            var resolver = new KeyframesResolver(new[] { Css("@keyframes a { from {} to {} }") });
            Assert.That(resolver.ResolveByName("does-not-exist"), Is.Null);
        }

        [Test]
        public void Multiple_stylesheets_concatenate() {
            var s1 = Css("@keyframes a { from {} to {} }");
            var s2 = Css("@keyframes b { from {} to {} }");
            var resolver = new KeyframesResolver(new[] { s1, s2 });
            Assert.That(resolver.Contains("a"), Is.True);
            Assert.That(resolver.Contains("b"), Is.True);
            Assert.That(resolver.Count, Is.EqualTo(2));
        }

        [Test]
        public void Last_defined_wins() {
            var s1 = Css("@keyframes spin { from { opacity: 0; } to { opacity: 1; } }");
            var s2 = Css("@keyframes spin { from { opacity: 0.5; } to { opacity: 0.75; } }");
            var resolver = new KeyframesResolver(new[] { s1, s2 });
            var anim = resolver.ResolveByName("spin");
            Assert.That(anim, Is.Not.Null);
            Assert.That(anim.Keyframes[0].Properties["opacity"], Is.EqualTo("0.5"));
        }

        [Test]
        public void Percentage_selector_yields_position() {
            var sheet = Css("@keyframes a { 50% { opacity: 0.5; } }");
            var resolver = new KeyframesResolver(new[] { sheet });
            var anim = resolver.ResolveByName("a");
            Assert.That(anim, Is.Not.Null);
            // 0% (synthesized) and 50% explicit and 100% (synthesized) = 3 frames.
            Assert.That(anim.Keyframes.Count, Is.EqualTo(3));
        }

        [Test]
        public void Multiple_selectors_in_one_block_yield_multiple_positions() {
            var sheet = Css("@keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.5; } }");
            var resolver = new KeyframesResolver(new[] { sheet });
            var anim = resolver.ResolveByName("pulse");
            Assert.That(anim, Is.Not.Null);
            // Three explicit positions (0%, 50%, 100%); since 0 and 1 already present no synth.
            Assert.That(anim.Keyframes.Count, Is.EqualTo(3));
        }

        [Test]
        public void Empty_stylesheets_safe() {
            var resolver = new KeyframesResolver(new Stylesheet[] { });
            Assert.That(resolver.Count, Is.EqualTo(0));
            Assert.That(resolver.ResolveByName("anything"), Is.Null);
        }
    }
}
