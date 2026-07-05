using NUnit.Framework;
using Weva.Binding;

namespace Weva.Tests.Binding {
    public class BindingTemplateTests {
        class Ctx {
            public string Name = "Alice";
            public int Count = 5;
            public double Pi = 3.14;
            public bool Flag = true;
            public object NullField = null;
        }

        [Test]
        public void Pure_literal_passes_through() {
            var t = BindingTemplate.Parse("hello world");
            Assert.That(t.HasBinding, Is.False);
            Assert.That(t.Render(new Ctx()), Is.EqualTo("hello world"));
        }

        [Test]
        public void Empty_template_renders_empty() {
            var t = BindingTemplate.Parse("");
            Assert.That(t.Render(new Ctx()), Is.EqualTo(""));
        }

        [Test]
        public void Single_binding_substitutes() {
            var t = BindingTemplate.Parse("{{ Name }}");
            Assert.That(t.HasBinding, Is.True);
            Assert.That(t.Render(new Ctx()), Is.EqualTo("Alice"));
        }

        [Test]
        public void Binding_with_surrounding_literal_text() {
            var t = BindingTemplate.Parse("Hi, {{ Name }}!");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("Hi, Alice!"));
        }

        [Test]
        public void Multiple_bindings_in_one_template() {
            var t = BindingTemplate.Parse("Hello {{ Name }}, you have {{ Count }} coins");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("Hello Alice, you have 5 coins"));
        }

        [Test]
        public void Whitespace_inside_braces_is_tolerated() {
            var t1 = BindingTemplate.Parse("{{Name}}");
            var t2 = BindingTemplate.Parse("{{   Name   }}");
            Assert.That(t1.Render(new Ctx()), Is.EqualTo("Alice"));
            Assert.That(t2.Render(new Ctx()), Is.EqualTo("Alice"));
        }

        [Test]
        public void Empty_braces_are_a_parse_error() {
            Assert.Throws<BindingException>(() => BindingTemplate.Parse("{{ }}"));
            Assert.Throws<BindingException>(() => BindingTemplate.Parse("{{}}"));
        }

        [Test]
        public void Unmatched_open_brace_is_a_parse_error() {
            Assert.Throws<BindingException>(() => BindingTemplate.Parse("hi {{ Name"));
        }

        [Test]
        public void Unmatched_close_brace_is_a_parse_error() {
            Assert.Throws<BindingException>(() => BindingTemplate.Parse("hi }} bye"));
        }

        [Test]
        public void Escape_open_brace_produces_literal() {
            var t = BindingTemplate.Parse("a\\{{b");
            Assert.That(t.HasBinding, Is.False);
            Assert.That(t.Render(new Ctx()), Is.EqualTo("a{{b"));
        }

        [Test]
        public void Escape_close_brace_produces_literal() {
            var t = BindingTemplate.Parse("a\\}}b");
            Assert.That(t.HasBinding, Is.False);
            Assert.That(t.Render(new Ctx()), Is.EqualTo("a}}b"));
        }

        [Test]
        public void Escaped_braces_pair_in_one_template() {
            var t = BindingTemplate.Parse("see \\{{ raw \\}} done");
            Assert.That(t.HasBinding, Is.False);
            Assert.That(t.Render(new Ctx()), Is.EqualTo("see {{ raw }} done"));
        }

        [Test]
        public void Null_context_renders_bindings_as_empty() {
            var t = BindingTemplate.Parse("Hello {{ Name }}");
            Assert.That(t.Render(null), Is.EqualTo("Hello "));
        }

        [Test]
        public void Null_field_renders_as_empty() {
            var t = BindingTemplate.Parse(">{{ NullField }}<");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("><"));
        }

        [Test]
        public void Numbers_use_invariant_culture() {
            var t = BindingTemplate.Parse("pi={{ Pi }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("pi=3.14"));
        }

        [Test]
        public void Bool_tostring() {
            var t = BindingTemplate.Parse("{{ Flag }}");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("True"));
        }

        [Test]
        public void Missing_member_renders_as_empty() {
            var t = BindingTemplate.Parse("hi {{ DoesNotExist }} bye");
            Assert.That(t.Render(new Ctx()), Is.EqualTo("hi  bye"));
        }

        [Test]
        public void Parse_error_includes_line_and_column() {
            try {
                BindingTemplate.Parse("hello {{ Name");
                Assert.Fail("should have thrown");
            } catch (BindingException ex) {
                Assert.That(ex.Line, Is.GreaterThan(0));
                Assert.That(ex.Column, Is.GreaterThan(0));
            }
        }

        [Test]
        public void Parse_error_in_multiline_template_carries_correct_line() {
            try {
                BindingTemplate.Parse("a\nb\n{{ }}");
                Assert.Fail("should have thrown");
            } catch (BindingException ex) {
                Assert.That(ex.Line, Is.EqualTo(3));
            }
        }
    }
}
