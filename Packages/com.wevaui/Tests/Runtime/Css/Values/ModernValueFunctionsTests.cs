using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Values {
    // Additional coverage for CSS Color 5 / Values & Units L4 value functions
    // not directly exercised by the per-function test files:
    //   - color-mix() across multiple interpolation spaces, including the
    //     transparent-shrinks-alpha case (CSS Color 5 §3).
    //   - light-dark() picking against MediaContext.ColorScheme (CSS Color 5
    //     §11.2).
    //   - attr() flowed into ::before content via the host-aware resolver
    //     (CSS Values 5 §11).
    //   - vw/vh/vmin/vmax viewport units (CSS Values & Units L4 §6.1).
    //
    // These tests use the same helper convention as ModernColorTests /
    // AttrResolverTests / LightDarkResolverTests / ViewportUnitsTests:
    //   - ParseColor for color values
    //   - Html/Author + CascadeEngine for cascaded attr() / light-dark()
    //   - LengthContext directly for viewport units
    public class ModernValueFunctionsTests {
        // ---- color-mix() ----

        static CssColor ParseColor(string s) => (CssColor)CssValueParser.Parse(s);

        [Test]
        public void Color_mix_srgb_50_50_red_blue_is_purple() {
            // sRGB midpoint of red and blue is rgb(127/128, 0, 127/128).
            var c = ParseColor("color-mix(in srgb, red 50%, blue 50%)");
            Assert.That((int)c.R, Is.EqualTo(127).Within(2));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(127).Within(2));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Color_mix_srgb_75_25_red_blue() {
            // 75% red bias: R channel dominates B, G stays 0.
            var c = ParseColor("color-mix(in srgb, red 75%, blue 25%)");
            Assert.That(c.R, Is.GreaterThan(c.B));
            Assert.That((int)c.R, Is.EqualTo(191).Within(2));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(64).Within(2));
        }

        [Test]
        public void Color_mix_oklab_red_blue_perceptual() {
            // Perceptual midpoint in OKLab is not the sRGB midpoint — verify
            // the result differs measurably from in-srgb mixing for the same
            // endpoints.
            var srgb = ParseColor("color-mix(in srgb, red, blue)");
            var oklab = ParseColor("color-mix(in oklab, red, blue)");
            // The two midpoints diverge in at least one channel by more than
            // the rounding tolerance.
            int rDiff = System.Math.Abs((int)oklab.R - (int)srgb.R);
            int gDiff = System.Math.Abs((int)oklab.G - (int)srgb.G);
            int bDiff = System.Math.Abs((int)oklab.B - (int)srgb.B);
            Assert.That(rDiff + gDiff + bDiff, Is.GreaterThan(5),
                "oklab and srgb midpoints of red+blue should differ perceptually");
        }

        [Test]
        public void Color_mix_oklch_red_blue_perceptual() {
            // OKLCh interpolates along the shorter hue path, producing a
            // different traversal than OKLab (which lerps a/b cartesian).
            var oklab = ParseColor("color-mix(in oklab, red, blue)");
            var oklch = ParseColor("color-mix(in oklch, red, blue)");
            int rDiff = System.Math.Abs((int)oklch.R - (int)oklab.R);
            int gDiff = System.Math.Abs((int)oklch.G - (int)oklab.G);
            int bDiff = System.Math.Abs((int)oklch.B - (int)oklab.B);
            Assert.That(rDiff + gDiff + bDiff, Is.GreaterThan(5),
                "oklch hue-path mix should differ from oklab cartesian mix");
            Assert.That(oklch.A, Is.EqualTo(1f));
        }

        [Test]
        public void Color_mix_hsl_red_blue() {
            // HSL interpolation of red (H=0, S=100%, L=50%) and blue
            // (H=240, S=100%, L=50%) walks the shorter hue path — through
            // magenta at H=300 (60deg the short way), not via green.
            var c = ParseColor("color-mix(in hsl, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
            // Magenta has high R and high B, zero G.
            Assert.That(c.R, Is.GreaterThan((byte)200));
            Assert.That(c.B, Is.GreaterThan((byte)200));
            Assert.That(c.G, Is.LessThan((byte)32));
        }

        [Test]
        public void Color_mix_implicit_50_50_when_no_percents() {
            // CSS Color 5 §3.1: omitted percentages default to 50/50.
            var implicit50 = ParseColor("color-mix(in srgb, red, blue)");
            var explicit50 = ParseColor("color-mix(in srgb, red 50%, blue 50%)");
            Assert.That((int)implicit50.R, Is.EqualTo((int)explicit50.R));
            Assert.That((int)implicit50.G, Is.EqualTo((int)explicit50.G));
            Assert.That((int)implicit50.B, Is.EqualTo((int)explicit50.B));
        }

        [Test]
        public void Color_mix_with_transparent_reduces_alpha() {
            // CSS Color 5 §3: alpha = (a.A * wA) + (b.A * wB). Mixing an opaque
            // color with `transparent` at 50/50 halves the result alpha.
            var c = ParseColor("color-mix(in srgb, red 50%, transparent 50%)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-3));
        }

        // ---- light-dark() ----

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static CascadeEngine BuildEngine(string css, ColorScheme scheme) {
            var sheets = new[] { Author(css) };
            var media = MediaContext.Default(800, 600).WithColorScheme(scheme);
            return new CascadeEngine(sheets, media);
        }

        [Test]
        public void Light_dark_picks_light_when_color_scheme_light() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { color: light-dark(white, black); }", ColorScheme.Light);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("white"));
        }

        [Test]
        public void Light_dark_picks_dark_when_color_scheme_dark() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine("#x { color: light-dark(white, black); }", ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Light_dark_falls_back_to_light_when_unset() {
            // MediaContext.Default(...) seeds ColorScheme.Light; with no
            // explicit color-scheme override, the light arg of light-dark()
            // wins. This is the v1 behavior — we treat "unset" as Light per
            // the CSS UA-default convention.
            var doc = Html("<div id=\"x\"></div>");
            var sheets = new[] { Author("#x { color: light-dark(beige, navy); }") };
            // Construct engine without WithColorScheme — leaves Default's Light.
            var engine = new CascadeEngine(sheets, MediaContext.Default(800, 600));
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("beige"));
        }

        // ---- attr() in content via ::before ----

        [Test]
        public void Attr_function_pulls_attribute_into_content_property() {
            // CSS Values 5 §11 + CSS Pseudo 4 §3.1: attr() inside `content`
            // on a ::before pulls the attribute value from the originating
            // (host) element. Here the host is the inner <span class="x">,
            // not the outer <div data-x="...">, because the ::before is
            // rooted on the span. Set data-x directly on the span so the
            // attr() lookup resolves.
            var doc = Html("<div data-x=\"outer\"><span class=\"x\" data-x=\"hello\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x::before { content: attr(data-x); }")
            });
            Element span = null;
            foreach (var el in doc.GetElementsByClassName("x")) { span = el; break; }
            Assert.That(span, Is.Not.Null, "test fixture must contain span.x");
            var bs = engine.ComputeBefore(span);
            Assert.That(bs, Is.Not.Null);
            // Use the host-aware ResolveContentString overload so attr()
            // resolves against the span (the originating element).
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content"), span), Is.EqualTo("hello"));
        }

        // ---- viewport units ----

        static LengthContext Viewport(double w, double h) {
            var c = LengthContext.Default;
            c.BaseFontSizePx = 16;
            c.RootFontSizePx = 16;
            c.ViewportWidthPx = w;
            c.ViewportHeightPx = h;
            c.DpiPixelsPerInch = 96;
            return c;
        }

        [Test]
        public void Vw_resolves_against_viewport_width() {
            // 1vw = 1% of viewport width — 100vw in a 1000-wide viewport is
            // 1000px, independent of height.
            var l = new CssLength(100, CssLengthUnit.Vw);
            Assert.That(l.ToPixels(Viewport(1000, 500)), Is.EqualTo(1000).Within(1e-9));
        }

        [Test]
        public void Vh_resolves_against_viewport_height() {
            // 1vh = 1% of viewport height — 100vh in an 800-tall viewport is
            // 800px, independent of width.
            var l = new CssLength(100, CssLengthUnit.Vh);
            Assert.That(l.ToPixels(Viewport(1200, 800)), Is.EqualTo(800).Within(1e-9));
        }

        [Test]
        public void Vmin_picks_smaller_dimension() {
            // 1vmin = 1% of min(w, h). 1000x600 -> min=600 -> 6px per vmin.
            var l = new CssLength(1, CssLengthUnit.Vmin);
            Assert.That(l.ToPixels(Viewport(1000, 600)), Is.EqualTo(6).Within(1e-9));
        }

        [Test]
        public void Vmax_picks_larger_dimension() {
            // 1vmax = 1% of max(w, h). 1000x600 -> max=1000 -> 10px per vmax.
            var l = new CssLength(1, CssLengthUnit.Vmax);
            Assert.That(l.ToPixels(Viewport(1000, 600)), Is.EqualTo(10).Within(1e-9));
        }
    }
}
