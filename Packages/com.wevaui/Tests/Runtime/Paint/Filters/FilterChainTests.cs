using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class FilterChainTests {
        [Test]
        public void Empty_chain_isEmpty_true() {
            Assert.That(FilterChain.Empty.IsEmpty, Is.True);
            Assert.That(FilterChain.Empty.Functions.Count, Is.EqualTo(0));
        }

        [Test]
        public void Single_filter_isEmpty_false() {
            var chain = new FilterChain(new List<FilterFunction> { new BlurFilter(5) });
            Assert.That(chain.IsEmpty, Is.False);
            Assert.That(chain.Functions.Count, Is.EqualTo(1));
        }

        [Test]
        public void Multi_chain_preserves_order() {
            var chain = new FilterChain(new List<FilterFunction> {
                new BlurFilter(5),
                new BrightnessFilter(0.8),
                new ContrastFilter(1.2)
            });
            Assert.That(chain.Functions.Count, Is.EqualTo(3));
            Assert.That(chain.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(chain.Functions[1], Is.InstanceOf<BrightnessFilter>());
            Assert.That(chain.Functions[2], Is.InstanceOf<ContrastFilter>());
        }

        [Test]
        public void ToText_empty_returns_none() {
            Assert.That(FilterChain.Empty.ToText(), Is.EqualTo("none"));
        }

        [Test]
        public void ToText_single_renders_function_call() {
            var chain = new FilterChain(new List<FilterFunction> { new BlurFilter(5) });
            Assert.That(chain.ToText(), Is.EqualTo("blur(5px)"));
        }

        [Test]
        public void ToText_multi_space_separated() {
            var chain = new FilterChain(new List<FilterFunction> {
                new BlurFilter(5),
                new BrightnessFilter(0.5)
            });
            Assert.That(chain.ToText(), Is.EqualTo("blur(5px) brightness(0.5)"));
        }

        [Test]
        public void Equality_same_filters_equal() {
            var a = new FilterChain(new List<FilterFunction> { new BlurFilter(5), new BrightnessFilter(1.2) });
            var b = new FilterChain(new List<FilterFunction> { new BlurFilter(5), new BrightnessFilter(1.2) });
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Equality_different_order_not_equal() {
            var a = new FilterChain(new List<FilterFunction> { new BlurFilter(5), new BrightnessFilter(1.2) });
            var b = new FilterChain(new List<FilterFunction> { new BrightnessFilter(1.2), new BlurFilter(5) });
            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void Null_input_treated_as_empty() {
            var chain = new FilterChain((IEnumerable<FilterFunction>)null);
            Assert.That(chain.IsEmpty, Is.True);
        }
    }
}
