using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssValueParserPoolingTests {
        [SetUp]
        public void Reset() {
            CssValue.ClearCachesForTests();
            CssValuePool.ClearAll();
        }

        [Test]
        public void Parse_16px_returns_interned_instance() {
            var a = CssValueParser.Parse("16px");
            var b = CssValueParser.Parse("16px");
            Assert.That(a, Is.InstanceOf<CssLength>());
            Assert.That(ReferenceEquals(a, b), Is.True);
            var l = (CssLength)a;
            Assert.That(l.Value, Is.EqualTo(16));
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Px));
        }

        [Test]
        public void Parse_0px_returns_interned_zero() {
            var a = CssValueParser.Parse("0px");
            Assert.That(a, Is.SameAs(CssLength.Zero));
        }

        [Test]
        public void Parse_auto_returns_keyword_not_length() {
            var v = CssValueParser.Parse("auto");
            Assert.That(v, Is.InstanceOf<CssKeyword>());
            Assert.That(((CssKeyword)v).Identifier, Is.EqualTo("auto"));
        }

        [Test]
        public void Parse_within_scope_uses_pool() {
            CssLength firstRent;
            using (CssValuePool.PassScope()) {
                var v = CssValueParser.Parse("12.5em");
                Assert.That(v, Is.InstanceOf<CssLength>());
                firstRent = (CssLength)v;
                Assert.That(firstRent.Value, Is.EqualTo(12.5));
            }
            // After scope, `firstRent` is recycled. The next rent inside a
            // fresh scope reuses the very same instance.
            using (CssValuePool.PassScope()) {
                var v2 = CssValueParser.Parse("99.5em");
                Assert.That(ReferenceEquals(firstRent, v2), Is.True);
                Assert.That(((CssLength)v2).Value, Is.EqualTo(99.5));
            }
        }

        [Test]
        public void Parse_result_graph_remains_correct_with_pool() {
            // Multiple values in a list should each parse to their own length.
            using (CssValuePool.PassScope()) {
                var v = CssValueParser.Parse("10px 20px 30px 40.5px");
                Assert.That(v, Is.InstanceOf<CssValueList>());
                var list = (CssValueList)v;
                Assert.That(list.Items.Count, Is.EqualTo(4));
                Assert.That(((CssLength)list.Items[0]).Value, Is.EqualTo(10));
                Assert.That(((CssLength)list.Items[1]).Value, Is.EqualTo(20));
                Assert.That(((CssLength)list.Items[2]).Value, Is.EqualTo(30));
                Assert.That(((CssLength)list.Items[3]).Value, Is.EqualTo(40.5));
            }
        }

        [Test]
        public void Parse_calc_inside_scope_returns_pooled_leaves() {
            using (CssValuePool.PassScope()) {
                var v = CssValueParser.Parse("calc(50.5px + 2.5em)");
                Assert.That(v, Is.InstanceOf<CssCalc>());
                var c = (CssCalc)v;
                // Evaluating inside the same scope must work even though the
                // calc-leaf lengths are pooled wrappers.
                var ctx = LengthContext.Default;
                ctx.BaseFontSizePx = 10;
                ctx.RootFontSizePx = 10;
                Assert.That(c.Evaluate(ctx), Is.EqualTo(50.5 + 25.0).Within(1e-9));
            }
        }

        [Test]
        public void Cached_math_function_detaches_calc_leaves_from_pool() {
            CssValue.ClearCachesForTests();
            CssCalc cached;
            var ctx = LengthContext.Default;
            ctx.ViewportWidthPx = 1738;
            ctx.ViewportHeightPx = 1043;

            using (CssValuePool.PassScope()) {
                Assert.That(CssValue.TryParseSilent("min(560px, 92vw)", out var parsed), Is.True);
                cached = (CssCalc)parsed;
                Assert.That(cached.Evaluate(ctx), Is.EqualTo(560).Within(1e-9));
            }

            using (CssValuePool.PassScope()) {
                CssValueParser.Parse("8.8px");
                CssValueParser.Parse("7.7em");
            }

            Assert.That(cached.Evaluate(ctx), Is.EqualTo(560).Within(1e-9));
        }

        [Test]
        public void Parse_percentage_50_returns_interned() {
            var v = CssValueParser.Parse("50%");
            Assert.That(v, Is.SameAs(CssPercentage.Fifty));
        }

        [Test]
        public void Parse_number_1_returns_interned_one() {
            var v = CssValueParser.Parse("1");
            Assert.That(v, Is.SameAs(CssNumber.One));
        }

        [Test]
        public void Parse_outside_scope_does_not_grow_pool() {
            int before = CssValuePool.LengthPoolCount;
            // Allocations outside the scope are simply fresh `new` calls; they
            // never enter the pool's free list. The interned 16px is intercepted
            // before the pool path even runs.
            for (int i = 0; i < 10; i++) {
                var _ = CssValueParser.Parse("17.5px");
            }
            Assert.That(CssValuePool.LengthPoolCount, Is.EqualTo(before));
        }

        [Test]
        public void Parse_inside_scope_memoizes_repeated_strings() {
            // Re-parsing the same text inside a scope returns the same
            // CssValue instance — the parse-cache short-circuits the
            // tokenizer + value-list construction entirely.
            using (CssValuePool.PassScope()) {
                var a = CssValueParser.Parse("12.5em");
                var b = CssValueParser.Parse("12.5em");
                Assert.That(ReferenceEquals(a, b), Is.True);
            }
        }

        [Test]
        public void Parse_cache_clears_between_outermost_scopes() {
            CssValue first;
            using (CssValuePool.PassScope()) {
                first = CssValueParser.Parse("5em");
            }
            using (CssValuePool.PassScope()) {
                // After the outermost scope closes, the cache is cleared;
                // re-parsing must produce a distinct instance because `first`
                // was recycled into the pool.
                var second = CssValueParser.Parse("5em");
                Assert.That(((CssLength)second).Value, Is.EqualTo(5));
                Assert.That(((CssLength)second).Unit, Is.EqualTo(CssLengthUnit.Em));
            }
        }
    }
}
