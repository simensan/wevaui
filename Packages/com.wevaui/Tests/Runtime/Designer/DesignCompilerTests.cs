using NUnit.Framework;
using Weva.Designer;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Unit coverage for the M1 Design Document compiler: layout (Stack + Fill/Hug/
    /// Fixed), spacing, alignment, tokens, text and the emitted HTML structure.
    /// These assert the produced HTML/CSS strings directly (deterministic, no engine);
    /// the engine round-trip (compile → feed Weva → assert boxes) is a separate suite.
    /// </summary>
    public class DesignCompilerTests
    {
        static DesignCompileResult Compile(DesignNode root, System.Action<DesignTokens> tokens = null)
        {
            var doc = new DesignDocument(root);
            tokens?.Invoke(doc.Tokens);
            return doc.Compile();
        }

        // --- Base / structure ---

        [Test]
        public void Empty_document_emits_reset_and_no_html()
        {
            var result = new DesignDocument().Compile();
            Assert.That(result.Css, Does.Contain("box-sizing: border-box"));
            Assert.That(result.Html, Is.Empty);
        }

        [Test]
        public void Root_gets_class_w0_and_children_increment()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            root.Add(new DesignNode("a")).Add(new DesignNode("b"));
            var r = Compile(root);

            Assert.That(r.Html, Does.Contain("class=\"w0\""));
            Assert.That(r.Html, Does.Contain("class=\"w1\""));
            Assert.That(r.Html, Does.Contain("class=\"w2\""));
        }

        [Test]
        public void Name_is_emitted_as_data_name()
        {
            var r = Compile(new DesignNode("Hero Banner"));
            Assert.That(r.Html, Does.Contain("data-name=\"Hero Banner\""));
        }

        [Test]
        public void Compilation_is_deterministic()
        {
            DesignNode Build()
            {
                var root = new DesignNode("root") { Layout = LayoutMode.Row, Gap = 8 };
                root.Add(new DesignNode("child") { Fill = "#fff" }.SetSize(SizeMode.Fill, SizeMode.Hug));
                return root;
            }
            var a = Compile(Build());
            var b = Compile(Build());
            Assert.That(a.Css, Is.EqualTo(b.Css));
            Assert.That(a.Html, Is.EqualTo(b.Html));
        }

        // --- Layout ---

        [Test]
        public void Row_layout_emits_flex_row()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Row });
            Assert.That(r.Css, Does.Contain("display: flex"));
            Assert.That(r.Css, Does.Contain("flex-direction: row"));
        }

        [Test]
        public void Column_layout_emits_flex_column()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Column });
            Assert.That(r.Css, Does.Contain("flex-direction: column"));
        }

        [Test]
        public void None_layout_does_not_emit_display_flex()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.None });
            Assert.That(r.Css, Does.Not.Contain("display: flex"));
        }

        [Test]
        public void Gap_is_emitted_for_flex_container()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Row, Gap = 12 });
            Assert.That(r.Css, Does.Contain("gap: 12px"));
        }

        [Test]
        public void Space_between_suppresses_gap()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Row, Gap = 12, MainAlign = MainAlign.SpaceBetween });
            Assert.That(r.Css, Does.Contain("justify-content: space-between"));
            Assert.That(r.Css, Does.Not.Contain("gap:"));
        }

        [Test]
        public void Main_align_center_maps_to_justify_center()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Row, MainAlign = MainAlign.Center });
            Assert.That(r.Css, Does.Contain("justify-content: center"));
        }

        [Test]
        public void Cross_align_start_is_explicit_to_override_stretch()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Row, CrossAlign = CrossAlign.Start });
            Assert.That(r.Css, Does.Contain("align-items: flex-start"));
        }

        [Test]
        public void Cross_align_stretch_is_omitted_as_default()
        {
            var r = Compile(new DesignNode { Layout = LayoutMode.Row, CrossAlign = CrossAlign.Stretch });
            Assert.That(r.Css, Does.Not.Contain("align-items:"));
        }

        // --- Padding ---

        [Test]
        public void Uniform_padding_uses_shorthand()
        {
            var r = Compile(new DesignNode().SetPadding(16));
            Assert.That(r.Css, Does.Contain("padding: 16px;"));
        }

        [Test]
        public void Symmetric_padding_uses_two_value_shorthand()
        {
            var node = new DesignNode { PadTop = 8, PadBottom = 8, PadLeft = 16, PadRight = 16 };
            var r = Compile(node);
            Assert.That(r.Css, Does.Contain("padding: 8px 16px;"));
        }

        [Test]
        public void Asymmetric_padding_uses_four_value_shorthand()
        {
            var node = new DesignNode { PadTop = 1, PadRight = 2, PadBottom = 3, PadLeft = 4 };
            var r = Compile(node);
            Assert.That(r.Css, Does.Contain("padding: 1px 2px 3px 4px;"));
        }

        // --- Sizing (Fill / Hug / Fixed) ---

        [Test]
        public void Fill_on_main_axis_emits_flex_grow_and_zero_min()
        {
            var root = new DesignNode { Layout = LayoutMode.Row };
            root.Add(new DesignNode("kid").SetSize(SizeMode.Fill, SizeMode.Hug));
            var r = Compile(root);
            Assert.That(r.Css, Does.Contain("flex-grow: 1"));
            Assert.That(r.Css, Does.Contain("flex-basis: 0%"));
            Assert.That(r.Css, Does.Contain("min-width: 0"));
        }

        [Test]
        public void Fill_on_cross_axis_emits_align_self_stretch()
        {
            var root = new DesignNode { Layout = LayoutMode.Row };
            root.Add(new DesignNode("kid").SetSize(SizeMode.Hug, SizeMode.Fill));
            var r = Compile(root);
            Assert.That(r.Css, Does.Contain("align-self: stretch"));
            Assert.That(r.Css, Does.Not.Contain("flex-grow"));
        }

        [Test]
        public void Hug_emits_no_explicit_size()
        {
            var root = new DesignNode { Layout = LayoutMode.Row };
            root.Add(new DesignNode("kid").SetSize(SizeMode.Hug, SizeMode.Hug));
            var r = Compile(root);
            Assert.That(r.Css, Does.Not.Contain("width:"));
            Assert.That(r.Css, Does.Not.Contain("flex-grow"));
        }

        [Test]
        public void Fixed_child_emits_px_size()
        {
            var root = new DesignNode { Layout = LayoutMode.Row };
            var kid = new DesignNode("kid");
            kid.SetFixedSize(120, 40);
            root.Add(kid);
            var r = Compile(root);
            Assert.That(r.Css, Does.Contain("width: 120px"));
            Assert.That(r.Css, Does.Contain("height: 40px"));
        }

        [Test]
        public void Root_fixed_size_is_emitted()
        {
            var root = new DesignNode("root");
            root.SetFixedSize(800, 600);
            var r = Compile(root);
            Assert.That(r.Css, Does.Contain("width: 800px"));
            Assert.That(r.Css, Does.Contain("height: 600px"));
        }

        // --- Tokens ---

        [Test]
        public void Tokens_emit_root_custom_properties()
        {
            var r = Compile(new DesignNode(), t => t.Color("brand/primary", "#3a7bd5"));
            Assert.That(r.Css, Does.Contain(":root"));
            Assert.That(r.Css, Does.Contain("--color-brand-primary: #3a7bd5"));
        }

        [Test]
        public void Fill_token_reference_resolves_to_var()
        {
            var node = new DesignNode { Fill = "{brand/primary}" };
            var r = Compile(node, t => t.Color("brand/primary", "#3a7bd5"));
            Assert.That(r.Css, Does.Contain("background: var(--color-brand-primary)"));
        }

        [Test]
        public void Raw_fill_color_passes_through()
        {
            var r = Compile(new DesignNode { Fill = "#ff0000" });
            Assert.That(r.Css, Does.Contain("background: #ff0000"));
        }

        [Test]
        public void Unknown_token_falls_back_visibly_to_magenta()
        {
            var r = Compile(new DesignNode { Fill = "{does/not/exist}" });
            Assert.That(r.Css, Does.Contain("magenta"));
        }

        // --- Style ---

        [Test]
        public void Radius_and_opacity_are_emitted()
        {
            var r = Compile(new DesignNode { Radius = 8, Opacity = 0.5 });
            Assert.That(r.Css, Does.Contain("border-radius: 8px"));
            Assert.That(r.Css, Does.Contain("opacity: 0.5"));
        }

        [Test]
        public void Full_opacity_is_not_emitted()
        {
            var r = Compile(new DesignNode { Opacity = 1 });
            Assert.That(r.Css, Does.Not.Contain("opacity"));
        }

        // --- Text ---

        [Test]
        public void Text_node_emits_escaped_text_and_style()
        {
            var node = new DesignNode("label") { Text = "Hello <world>", TextColor = "#222", FontSize = 14 };
            var r = Compile(node);
            Assert.That(r.Html, Does.Contain("Hello &lt;world&gt;"));
            Assert.That(r.Css, Does.Contain("color: #222"));
            Assert.That(r.Css, Does.Contain("font-size: 14px"));
        }

        [Test]
        public void Text_node_color_supports_tokens()
        {
            var node = new DesignNode { Text = "hi", TextColor = "{text/primary}" };
            var r = Compile(node, t => t.Color("text/primary", "#111"));
            Assert.That(r.Css, Does.Contain("color: var(--color-text-primary)"));
        }
    }
}
