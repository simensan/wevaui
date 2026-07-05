using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Paint.Images;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Tests <img> natural-size resolution: HTML width/height attrs +
    // IImageRegistry intrinsic size. CSS values still win when set.
    public class ImgIntrinsicSizeTests {
        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        static (Box root, ComputedStyle imgStyle, Box imgBox) BuildImg(string html, IImageRegistry registry = null, string css = null, string tagName = "img") {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent) };
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var pool = new BoxPool();
            pool.BeginPass();
            var bb = new BoxBuilder(
                e => styles.TryGetValue(e, out var cs) ? cs : null,
                null,
                registry,
                pool,
                new LayoutScratch());
            var root = bb.BuildDocument(doc);
            ComputedStyle imgStyle = null;
            Box imgBox = null;
            foreach (var kv in styles) {
                if (kv.Key.TagName == tagName) { imgStyle = kv.Value; break; }
            }
            if (root != null) imgBox = FindBoxByTag(root, tagName);
            return (root, imgStyle, imgBox);
        }

        static Box FindBoxByTag(Box box, string tagName) {
            if (box.Element?.TagName == tagName) return box;
            for (int i = 0; i < box.Children.Count; i++) {
                var found = FindBoxByTag(box.Children[i], tagName);
                if (found != null) return found;
            }
            return null;
        }

        [Test]
        public void Html_width_attribute_resolves_as_css_pixel_length() {
            var (_, style, _) = BuildImg("<img src=\"x\" width=\"32\" height=\"24\" />");
            Assert.That(style.Get("width"), Is.EqualTo("32px"));
            Assert.That(style.Get("height"), Is.EqualTo("24px"));
        }

        [Test]
        public void Css_width_wins_over_html_width_attribute() {
            var (_, style, _) = BuildImg(
                "<img src=\"x\" width=\"32\" />",
                css: "img { width: 10px; height: 5px; }");
            Assert.That(style.Get("width"), Is.EqualTo("10px"));
            Assert.That(style.Get("height"), Is.EqualTo("5px"));
        }

        [Test]
        public void Registry_intrinsic_size_written_to_style_and_box() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/heart", new StubSource(64, 32));
            var (_, style, imgBox) = BuildImg(
                "<img src=\"ui/heart\" />",
                registry: reg);
            Assert.That(style.Get("width"), Is.EqualTo("64px"));
            Assert.That(style.Get("height"), Is.EqualTo("32px"));
            Assert.That(imgBox, Is.Not.Null);
            Assert.That(imgBox.IntrinsicWidth, Is.EqualTo(64));
            Assert.That(imgBox.IntrinsicHeight, Is.EqualTo(32));
        }

        [Test]
        public void Registry_intrinsic_size_skipped_when_html_attr_present() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/heart", new StubSource(64, 32));
            var (_, style, imgBox) = BuildImg(
                "<img src=\"ui/heart\" width=\"100\" height=\"50\" />",
                registry: reg);
            Assert.That(style.Get("width"), Is.EqualTo("100px"));
            Assert.That(style.Get("height"), Is.EqualTo("50px"));
            Assert.That(imgBox.IntrinsicWidth, Is.EqualTo(0));
            Assert.That(imgBox.IntrinsicHeight, Is.EqualTo(0));
        }

        [Test]
        public void Mixed_html_width_only_with_registry_fills_height_intrinsic() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/heart", new StubSource(64, 32));
            var (_, style, imgBox) = BuildImg(
                "<img src=\"ui/heart\" width=\"128\" />",
                registry: reg);
            Assert.That(style.Get("width"), Is.EqualTo("128px"));
            Assert.That(style.Get("height"), Is.EqualTo("32px"));
            Assert.That(imgBox.IntrinsicWidth, Is.EqualTo(0));
            Assert.That(imgBox.IntrinsicHeight, Is.EqualTo(32));
        }

        [Test]
        public void Missing_handle_leaves_width_height_auto() {
            var reg = new InMemoryImageRegistry();
            var (_, style, imgBox) = BuildImg(
                "<img src=\"missing\" />",
                registry: reg);
            Assert.That(style.Get("width"), Is.EqualTo("auto"));
            Assert.That(style.Get("height"), Is.EqualTo("auto"));
            Assert.That(imgBox.IntrinsicWidth, Is.EqualTo(0));
        }

        [Test]
        public void No_registry_falls_through_silently() {
            var (_, style, _) = BuildImg("<img src=\"x\" />");
            Assert.That(style.Get("width"), Is.EqualTo("auto"));
            Assert.That(style.Get("height"), Is.EqualTo("auto"));
        }

        [Test]
        public void Negative_html_width_attr_is_ignored() {
            var (_, style, _) = BuildImg("<img src=\"x\" width=\"-16\" />");
            Assert.That(style.Get("width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Non_numeric_html_width_attr_is_ignored() {
            var (_, style, _) = BuildImg("<img src=\"x\" width=\"abc\" />");
            Assert.That(style.Get("width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Non_img_element_unaffected_by_intrinsic_size_path() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/heart", new StubSource(64, 32));
            var (_, style, imgBox) = BuildImg(
                "<div src=\"ui/heart\"></div>",
                registry: reg,
                tagName: "div");
            Assert.That(style.Get("width"), Is.EqualTo("auto"));
            Assert.That(style.Get("height"), Is.EqualTo("auto"));
            Assert.That(imgBox.IntrinsicWidth, Is.EqualTo(0));
        }
    }
}
