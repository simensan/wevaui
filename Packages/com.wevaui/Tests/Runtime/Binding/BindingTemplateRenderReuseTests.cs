using System;
using NUnit.Framework;
using Weva.Binding;
using Weva.Binding.Generated;
using Weva.Dom;

namespace Weva.Tests.Binding {
    // Covers the unchanged-result fast path of BindingTemplate.Render(context,
    // ifUnchanged): the per-frame binding poll renders into a reused buffer and
    // must return the SAME string reference (allocating nothing) when the
    // output matches what is already in the DOM.
    public class BindingTemplateRenderReuseTests {
        class Ctx {
            public string Name = "Alice";
            public int Count = 5;
            public int NegInt = -42;
            public long MinLong = long.MinValue;
            public ulong MaxUlong = ulong.MaxValue;
            public byte Byte = 200;
            public sbyte Sbyte = -100;
            public short Short = -30000;
            public ushort Ushort = 60000;
            public uint Uint = 4000000000;
            public char Char = 'x';
            public bool FlagOff = false;
            public double Pi = 3.14;
            public float Half = 0.5f;
            public decimal Money = 12.50m;
        }

        [Test]
        public void Unchanged_render_returns_the_ifUnchanged_reference() {
            var t = BindingTemplate.Parse("Hi {{ Name }}, {{ Count }} coins");
            var ctx = new Ctx();
            string current = t.Render(ctx);
            string again = t.Render(ctx, current);
            Assert.That(ReferenceEquals(again, current), Is.True,
                "unchanged render must return the same reference, not a new string");
        }

        [Test]
        public void Changed_render_returns_a_new_string() {
            var t = BindingTemplate.Parse("N={{ Count }}");
            var ctx = new Ctx();
            string current = t.Render(ctx);
            ctx.Count = 6;
            string next = t.Render(ctx, current);
            Assert.That(ReferenceEquals(next, current), Is.False);
            Assert.That(next, Is.EqualTo("N=6"));
        }

        [Test]
        public void Null_ifUnchanged_renders_normally() {
            var t = BindingTemplate.Parse("Hi {{ Name }}");
            Assert.That(t.Render(new Ctx(), null), Is.EqualTo("Hi Alice"));
        }

        [Test]
        public void Same_length_different_content_is_detected_as_changed() {
            var t = BindingTemplate.Parse("{{ Name }}");
            var ctx = new Ctx { Name = "Bobby" };
            string rendered = t.Render(ctx, "Cathy");
            Assert.That(rendered, Is.EqualTo("Bobby"));
        }

        [Test]
        public void Pure_literal_template_still_short_circuits() {
            var t = BindingTemplate.Parse("static");
            Assert.That(t.Render(new Ctx(), "other"), Is.EqualTo("static"));
        }

        [Test]
        public void Empty_template_renders_empty_with_ifUnchanged() {
            var t = BindingTemplate.Parse("");
            Assert.That(t.Render(new Ctx(), "x"), Is.EqualTo(""));
        }

        // --- invariant-culture formatting of the direct-append fast paths ---

        [Test]
        public void Negative_int_formats_invariant() {
            var t = BindingTemplate.Parse("{{ NegInt }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("-42"));
        }

        [Test]
        public void Long_min_value_formats_without_overflow() {
            var t = BindingTemplate.Parse("{{ MinLong }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("-9223372036854775808"));
        }

        [Test]
        public void Ulong_max_value_formats() {
            var t = BindingTemplate.Parse("{{ MaxUlong }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("18446744073709551615"));
        }

        [Test]
        public void Small_integer_types_format() {
            var t = BindingTemplate.Parse("{{ Byte }}|{{ Sbyte }}|{{ Short }}|{{ Ushort }}|{{ Uint }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("200|-100|-30000|60000|4000000000"));
        }

        [Test]
        public void Zero_formats() {
            var t = BindingTemplate.Parse("{{ Count }}");
            Assert.That(t.Render(new Ctx { Count = 0 }), Is.EqualTo("0"));
        }

        [Test]
        public void Char_and_false_bool_format() {
            var t = BindingTemplate.Parse("{{ Char }}{{ FlagOff }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("xFalse"));
        }

        [Test]
        public void Float_double_decimal_keep_invariant_formatting() {
            var t = BindingTemplate.Parse("{{ Pi }} {{ Half }} {{ Money }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("3.14 0.5 12.50"));
        }

        // --- buffer reuse safety ---

        [Test]
        public void Interleaved_templates_do_not_corrupt_each_other() {
            var a = BindingTemplate.Parse("A:{{ Name }}");
            var b = BindingTemplate.Parse("B:{{ Count }}");
            var ctx = new Ctx();
            for (int i = 0; i < 3; i++) {
                Assert.That(a.Render(ctx), Is.EqualTo("A:Alice"));
                Assert.That(b.Render(ctx), Is.EqualTo("B:5"));
            }
        }

        class ReentrantCtx {
            public BindingTemplate Inner;
            public object InnerCtx;
            public string Nested => Inner.Render(InnerCtx);
        }

        [Test]
        public void Reentrant_render_from_property_getter_is_correct() {
            // A resolved getter that itself renders a template must not corrupt
            // the outer render's reused buffer.
            var inner = BindingTemplate.Parse("inner-{{ Count }}");
            var outer = BindingTemplate.Parse("[{{ Nested }}]");
            var ctx = new ReentrantCtx { Inner = inner, InnerCtx = new Ctx() };
            Assert.That(outer.Render(ctx), Is.EqualTo("[inner-5]"));
            Assert.That(outer.Render(ctx), Is.EqualTo("[inner-5]"));
        }

        // --- end-to-end: idle binding polls do not reassign DOM strings ---

        [Test]
        public void Idle_text_binding_keeps_the_same_data_reference() {
            var doc = new Document();
            var el = new Element("span");
            doc.AppendChild(el);
            var tn = new TextNode("placeholder");
            el.AppendChild(tn);
            var b = new TextBinding(tn, BindingTemplate.Parse("Hi {{ Name }} ({{ Count }})"));
            var ctx = new Ctx();
            b.Update(ctx);
            string data = tn.Data;
            for (int i = 0; i < 5; i++) {
                Assert.That(b.Update(ctx), Is.False);
            }
            Assert.That(ReferenceEquals(tn.Data, data), Is.True,
                "idle updates must not reassign TextNode.Data");
        }

        [Test]
        public void Idle_attribute_binding_keeps_the_same_attribute_reference() {
            var el = new Element("div");
            var b = new AttributeBinding(el, "title", BindingTemplate.Parse("T {{ Count }}"));
            var ctx = new Ctx();
            b.Update(ctx);
            string val = el.GetAttribute("title");
            for (int i = 0; i < 5; i++) {
                Assert.That(b.Update(ctx), Is.False);
            }
            Assert.That(ReferenceEquals(el.GetAttribute("title"), val), Is.True,
                "idle updates must not rewrite the attribute");
        }

#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        // Mirrors the shape the [UIBind] generator emits: TryGet via switch.
        // The reflection fallback boxes value types (FieldInfo.GetValue), so
        // the strict zero-alloc guarantee applies to the accessor fast path â€”
        // which is what production controllers use.
        sealed class AccessorCtx : IBindingAccessor {
            public string Name = "Alice";
            public int Count = 5;
            readonly object countBoxed;

            public AccessorCtx() { countBoxed = Count; }

            static readonly string[] members = { "Name", "Count" };
            System.Collections.Generic.IReadOnlyList<string> IBindingAccessor.BoundMemberNames => members;
            System.Collections.Generic.IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings =>
                Array.Empty<ElementBindingDescriptor>();

            bool IBindingAccessor.TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "Name": value = Name; return true;
                    case "Count": value = countBoxed; return true;
                    default: value = null; return false;
                }
            }

            bool IBindingAccessor.TrySet(string memberName, object value) => false;
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        [Test]
        public void Idle_renders_allocate_nothing_on_accessor_fast_path() {
            var t = BindingTemplate.Parse("Hello {{ Name }}, {{ Count }} coins");
            var ctx = new AccessorCtx();
            string current = t.Render(ctx);
            Assert.That(current, Is.EqualTo("Hello Alice, 5 coins"));

            // Warm the thread-static buffer and any resolver caches.
            for (int i = 0; i < 10; i++) t.Render(ctx, current);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) {
                t.Render(ctx, current);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(delta, Is.EqualTo(0),
                $"1000 unchanged renders allocated {delta} bytes; expected 0");
        }
#endif
    }
}
