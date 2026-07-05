using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    // Part A of per-element font-family: LayoutContext.GetMetrics consults a
    // per-family resolver (installed by the TMP/SDF backend) so a run is
    // MEASURED with the same face it's painted with. The resolver wins for a
    // registered family; unresolved families fall through to DefaultFontMetrics.
    public class FamilyMetricsResolverTests {
        [Test]
        public void GetMetrics_uses_resolver_for_registered_family_else_default() {
            var def = new MonoFontMetrics();
            var special = new MonoFontMetrics();
            var ctx = new LayoutContext(def) {
                FamilyMetricsResolver = fam =>
                    string.Equals(fam, "special", System.StringComparison.OrdinalIgnoreCase) ? special : null
            };

            Assert.That(ctx.GetMetrics("special"), Is.SameAs(special),
                "resolver hit for the registered family");
            Assert.That(ctx.GetMetrics("\"Special\", sans-serif"), Is.SameAs(special),
                "quoted primary in a stack still resolves via the resolver");
            Assert.That(ctx.GetMetrics("Other, sans-serif"), Is.SameAs(def),
                "no resolver hit anywhere in the stack falls through to default");
            Assert.That(ctx.GetMetrics(""), Is.SameAs(def),
                "empty family is the default");
        }

        [Test]
        public void GetMetrics_without_resolver_is_unchanged() {
            var def = new MonoFontMetrics();
            var ctx = new LayoutContext(def);   // FamilyMetricsResolver null
            Assert.That(ctx.GetMetrics("\"Anything\", sans-serif"), Is.SameAs(def));
            Assert.That(ctx.GetMetrics("monospace"), Is.SameAs(def));
        }
    }
}
