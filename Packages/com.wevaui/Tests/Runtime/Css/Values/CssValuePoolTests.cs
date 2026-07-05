using System.Threading;
using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssValuePoolTests {
        [SetUp]
        public void ResetPoolBetweenTests() {
            CssValuePool.ClearAll();
        }

        [Test]
        public void RentLength_outside_scope_returns_fresh_instance() {
            var a = CssValuePool.RentLength(7.5, CssLengthUnit.Em, "7.5em");
            var b = CssValuePool.RentLength(7.5, CssLengthUnit.Em, "7.5em");
            // Without an active scope, neither is pool-managed; they should be
            // distinct fresh instances unless they happen to be interned.
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Value, Is.EqualTo(7.5));
            Assert.That(a.Unit, Is.EqualTo(CssLengthUnit.Em));
        }

        [Test]
        public void Scope_returns_rented_lengths_to_pool() {
            CssLength rented;
            using (var scope = CssValuePool.PassScope()) {
                rented = CssValuePool.RentLength(7.5, CssLengthUnit.Em, "7.5em");
                Assert.That(rented.Value, Is.EqualTo(7.5));
            }
            Assert.That(CssValuePool.LengthPoolCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Returning_then_renting_yields_same_instance() {
            CssLength first;
            using (CssValuePool.PassScope()) {
                first = CssValuePool.RentLength(3.25, CssLengthUnit.Rem, "3.25rem");
            }
            // After scope, `first` is on the pool. Open a new scope: the next
            // rent for an arbitrary value should pop our same instance.
            using (CssValuePool.PassScope()) {
                var second = CssValuePool.RentLength(99.0, CssLengthUnit.Em, "99em");
                Assert.That(ReferenceEquals(first, second), Is.True);
                Assert.That(second.Value, Is.EqualTo(99.0));
                Assert.That(second.Unit, Is.EqualTo(CssLengthUnit.Em));
            }
        }

        [Test]
        public void Hwm_scope_returns_only_what_it_rented() {
            using (CssValuePool.PassScope()) {
                var outerA = CssValuePool.RentLength(11.5, CssLengthUnit.Px, "11.5px");
                Assert.That(outerA.Value, Is.EqualTo(11.5));
                int beforeNested = CssValuePool.LengthPoolCount;
                using (CssValuePool.PassScope()) {
                    var inner = CssValuePool.RentLength(13.5, CssLengthUnit.Px, "13.5px");
                    Assert.That(inner.Value, Is.EqualTo(13.5));
                }
                // Nested scope returned its rentals; the outer still holds outerA
                // (it stays "live" until the outer scope disposes).
                Assert.That(CssValuePool.LengthPoolCount, Is.GreaterThanOrEqualTo(beforeNested + 1));
                Assert.That(outerA.Value, Is.EqualTo(11.5)); // outer rental untouched
            }
        }

        [Test]
        public void Common_integer_px_values_return_interned_instance() {
            var a = CssValuePool.RentLength(0, CssLengthUnit.Px, "0px");
            var b = CssValuePool.RentLength(0, CssLengthUnit.Px, "0px");
            var c = CssValuePool.RentLength(16, CssLengthUnit.Px, "16px");
            var d = CssValuePool.RentLength(16, CssLengthUnit.Px, "16px");
            Assert.That(ReferenceEquals(a, b), Is.True, "0px should be interned");
            Assert.That(ReferenceEquals(c, d), Is.True, "16px should be interned");
            Assert.That(ReferenceEquals(a, c), Is.False);
        }

        [Test]
        public void Px_helper_returns_interned_instance_for_integer_values() {
            var a = CssLength.Px(64);
            var b = CssLength.Px(64);
            Assert.That(ReferenceEquals(a, b), Is.True);
            Assert.That(a.Value, Is.EqualTo(64));
            Assert.That(a.Unit, Is.EqualTo(CssLengthUnit.Px));
        }

        [Test]
        public void Px_helper_allocates_for_non_integer_or_oversize() {
            var fractional = CssLength.Px(2.5);
            var overflow = CssLength.Px(1024);
            Assert.That(fractional.Value, Is.EqualTo(2.5));
            Assert.That(overflow.Value, Is.EqualTo(1024));
        }

        [Test]
        public void CssLength_Zero_is_interned_constant() {
            Assert.That(CssLength.Zero, Is.SameAs(CssLength.Zero));
            Assert.That(CssLength.Zero.Value, Is.EqualTo(0));
            Assert.That(CssLength.Zero.Unit, Is.EqualTo(CssLengthUnit.Px));
        }

        [Test]
        public void CssNumber_Zero_and_One_interned() {
            var a = CssValuePool.RentNumber(0, "0");
            var b = CssValuePool.RentNumber(1, "1");
            Assert.That(ReferenceEquals(a, CssNumber.Zero), Is.True);
            Assert.That(ReferenceEquals(b, CssNumber.One), Is.True);
        }

        [Test]
        public void CssPercentage_50_100_0_interned() {
            var a = CssValuePool.RentPercentage(0, "0%");
            var b = CssValuePool.RentPercentage(100, "100%");
            var c = CssValuePool.RentPercentage(50, "50%");
            Assert.That(ReferenceEquals(a, CssPercentage.Zero), Is.True);
            Assert.That(ReferenceEquals(b, CssPercentage.Hundred), Is.True);
            Assert.That(ReferenceEquals(c, CssPercentage.Fifty), Is.True);
        }

        [Test]
        public void Reset_updates_value_unit_raw() {
            CssLength rented;
            using (CssValuePool.PassScope()) {
                rented = CssValuePool.RentLength(2.5, CssLengthUnit.Em, "2.5em");
                Assert.That(rented.Value, Is.EqualTo(2.5));
                Assert.That(rented.Unit, Is.EqualTo(CssLengthUnit.Em));
                Assert.That(rented.Raw, Is.EqualTo("2.5em"));
            }
            // After scope, `rented` is back on pool. Opening a new scope and
            // renting again should recycle it with the new state.
            using (CssValuePool.PassScope()) {
                var recycled = CssValuePool.RentLength(80, CssLengthUnit.Vh, "80vh");
                Assert.That(ReferenceEquals(rented, recycled), Is.True);
                Assert.That(recycled.Value, Is.EqualTo(80));
                Assert.That(recycled.Unit, Is.EqualTo(CssLengthUnit.Vh));
                Assert.That(recycled.Raw, Is.EqualTo("80vh"));
            }
        }

        [Test]
        public void Concurrent_threads_have_independent_pools() {
            // ThreadStatic pools mean two threads renting concurrently must not
            // see the same instance.
            CssLength fromThread1 = null;
            CssLength fromThread2 = null;
            var t1 = new Thread(() => {
                using (CssValuePool.PassScope()) {
                    fromThread1 = CssValuePool.RentLength(7, CssLengthUnit.Em, "7em");
                }
            });
            var t2 = new Thread(() => {
                using (CssValuePool.PassScope()) {
                    fromThread2 = CssValuePool.RentLength(7, CssLengthUnit.Em, "7em");
                }
            });
            t1.Start(); t2.Start();
            t1.Join(); t2.Join();
            Assert.That(fromThread1, Is.Not.Null);
            Assert.That(fromThread2, Is.Not.Null);
            Assert.That(ReferenceEquals(fromThread1, fromThread2), Is.False);
        }

        [Test]
        public void Pool_does_not_grow_unboundedly() {
            // Rent + return 8000 distinct values inside a scope. Pool must not
            // exceed the documented 4096 cap.
            using (CssValuePool.PassScope()) {
                for (int i = 0; i < 8000; i++) {
                    CssValuePool.RentLength(0.5 + i, CssLengthUnit.Em, "x");
                }
            }
            Assert.That(CssValuePool.LengthPoolCount, Is.LessThanOrEqualTo(4096));
        }

        [Test]
        public void Manual_return_recycles_value() {
            CssValuePool.ClearAll();
            var fresh = new CssLength(123.5, CssLengthUnit.Em, "123.5em");
            CssValuePool.ReturnLength(fresh);
            Assert.That(CssValuePool.LengthPoolCount, Is.EqualTo(1));
            using (CssValuePool.PassScope()) {
                var rented = CssValuePool.RentLength(45.5, CssLengthUnit.Px, "45.5px");
                Assert.That(ReferenceEquals(rented, fresh), Is.True);
                Assert.That(rented.Value, Is.EqualTo(45.5));
            }
        }

        [Test]
        public void ReturnLength_skips_interned_instances() {
            CssValuePool.ClearAll();
            CssValuePool.ReturnLength(CssLength.Zero);
            CssValuePool.ReturnLength(CssLength.Px(16));
            Assert.That(CssValuePool.LengthPoolCount, Is.EqualTo(0));
        }
    }
}
