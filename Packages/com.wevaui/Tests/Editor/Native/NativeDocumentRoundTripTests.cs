// The Unity host's first gate over the shared core: the weva_core plugin
// loads in the editor, reports the ABI its header declares, its struct
// layouts match the generated C# mirrors byte for byte, and a document runs
// end to end (create, HTML, CSS, update, focus, bounds, tag name, draws,
// destroy) through P/Invoke. The same round trip runs from C++ in the
// plugin's load test; the two must agree.
using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeDocumentRoundTripTests
    {
        private const string Html = "<body><button id=go>Go</button><div id=box>box</div></body>";
        private const string Css = "#box{width:120px;height:40px;background:#3a5f8a;margin:10px}button{margin:10px}";

        [Test]
        public void Plugin_ReportsHeaderAbiVersion()
        {
            var (major, minor) = NativeDocument.AbiVersion();
            Assert.That(major, Is.EqualTo(WevaNative.WEVA_ABI_VERSION_MAJOR));
            Assert.That(minor, Is.EqualTo(WevaNative.WEVA_ABI_VERSION_MINOR));
        }

        [TestCase(typeof(weva_config))]
        [TestCase(typeof(weva_vertex))]
        [TestCase(typeof(weva_rounded_rect))]
        [TestCase(typeof(weva_backdrop_effect))]
        [TestCase(typeof(weva_draw))]
        [TestCase(typeof(weva_texture))]
        [TestCase(typeof(weva_render_backend))]
        [TestCase(typeof(weva_glyph_bitmap))]
        [TestCase(typeof(weva_font_backend))]
        [TestCase(typeof(weva_shaped_glyph))]
        [TestCase(typeof(weva_event))]
        [TestCase(typeof(weva_binding_source))]
        public void GeneratedStruct_MatchesNativeSize(Type mirror)
        {
            int native = NativeDocument.NativeSizeOf(mirror.Name);
            Assert.That(native, Is.GreaterThan(0), mirror.Name + " is unknown to the plugin's layout probe");
            Assert.That(Marshal.SizeOf(mirror), Is.EqualTo(native), mirror.Name + " differs from the C layout");
        }

        [Test]
        public void LayoutProbe_RejectsUnknownNames()
        {
            Assert.That(NativeDocument.NativeSizeOf("weva_nothing"), Is.EqualTo(0));
            Assert.That(NativeDocument.NativeSizeOf(""), Is.EqualTo(0));
        }

        [Test]
        public void Document_RoundTrips()
        {
            using (var doc = new NativeDocument(640, 480))
            {
                Assert.That(doc.IsAlive);
                doc.LoadHtml(Html);
                doc.SetCss(Css);
                doc.Update(0);

                uint button = doc.FocusNext();
                Assert.That(button, Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "focus finds the button");
                Assert.That(doc.TagName(button), Is.EqualTo("button"));

                Assert.That(doc.TryGetBounds(button, out NativeBounds bounds), "the button has bounds");
                Assert.That(bounds.X, Is.EqualTo(10.0).Within(0.001), "button margin");
                Assert.That(bounds.Y, Is.EqualTo(10.0).Within(0.001), "button margin");
                Assert.That(bounds.Width, Is.GreaterThan(0));
                Assert.That(bounds.Height, Is.GreaterThan(0));

                ReadOnlySpan<weva_draw> draws = doc.Draws();
                Assert.That(draws.Length, Is.GreaterThan(0), "the document produces draws");
                bool sawGeometry = false;
                for (int i = 0; i < draws.Length; i++)
                {
                    if (draws[i].vertex_count > 0 && draws[i].index_count > 0)
                    {
                        sawGeometry = true;
                        break;
                    }
                }
                Assert.That(sawGeometry, "at least one draw carries triangles");
            }
        }

        [Test]
        public void Bounds_OfNoElement_Fail()
        {
            using (var doc = new NativeDocument(320, 240))
            {
                doc.LoadHtml("<body><p>x</p></body>");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(WevaNative.WEVA_ELEMENT_NONE, out _), Is.False);
                Assert.That(doc.TagName(WevaNative.WEVA_ELEMENT_NONE), Is.EqualTo(string.Empty));
            }
        }

        [Test]
        public void Update_AfterDispose_Throws()
        {
            var doc = new NativeDocument(320, 240);
            doc.Dispose();
            Assert.That(doc.IsAlive, Is.False);
            Assert.Throws<ObjectDisposedException>(() => doc.Update(0));
            doc.Dispose();  // idempotent
        }

        [Test]
        public void ManyDocuments_CreateAndDestroy()
        {
            // A leak or a double free here shows up as a crash, not an assertion.
            for (int i = 0; i < 50; i++)
            {
                using (var doc = new NativeDocument(200, 100))
                {
                    doc.LoadHtml("<body><div id=a>" + i + "</div></body>");
                    doc.Update(0.016);
                    Assert.That(doc.Draws().Length, Is.GreaterThanOrEqualTo(0));
                }
            }
        }
    }
}
