// The render adapter for the shared core: the draw list the core publishes
// becomes meshes and textures, and drawing them offscreen puts the page's
// pixels where the core laid them out. These tests need a graphics device
// (run the editor without -nographics).
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeDocumentRenderTests
    {
        private static bool HasGraphics => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static Color32 PageTopDown(Texture2D t, int x, int yFromTop) => t.GetPixel(x, t.height - 1 - yFromTop);

        private static bool Near(Color32 a, Color32 b, int tolerance = 6)
        {
            return Mathf.Abs(a.r - b.r) <= tolerance && Mathf.Abs(a.g - b.g) <= tolerance && Mathf.Abs(a.b - b.b) <= tolerance;
        }

        [Test]
        public void Shader_IsAvailable()
        {
            using (var renderer = new NativeDocumentRenderer())
            {
                Assert.That(renderer.IsReady, "Hidden/Weva/NativeMesh loads from the package's Resources");
            }
        }

        [Test]
        public void Sync_BuildsBatchesOnlyWhenTheDrawListChanges()
        {
            using (var doc = new NativeDocument(200, 100))
            using (var renderer = new NativeDocumentRenderer())
            {
                doc.LoadHtml("<body><div id=a></div><div id=b></div></body>");
                doc.SetCss("body{margin:0}#a{width:50px;height:20px;background:#f00}#b{width:50px;height:20px;background:#00f}");
                doc.Update(0);
                Assert.That(renderer.Sync(doc), "first sync builds");
                Assert.That(renderer.BatchCount, Is.GreaterThan(0));
                Assert.That(renderer.TrianglesUploaded, Is.GreaterThanOrEqualTo(4), "two boxes are at least four triangles");
                Assert.That(renderer.Sync(doc), Is.False, "nothing changed");
                doc.SetCss("body{margin:0}#a{width:50px;height:20px;background:#0f0}#b{width:50px;height:20px;background:#00f}");
                doc.Update(0);
                Assert.That(renderer.Sync(doc), "a new draw list rebuilds");
            }
        }

        [Test]
        public void Render_PutsABoxWhereTheCoreLaidItOut()
        {
            Assume.That(HasGraphics, "a graphics device is required");
            using (var doc = new NativeDocument(200, 100))
            using (var renderer = new NativeDocumentRenderer())
            {
                doc.LoadHtml("<body><div id=a></div></body>");
                doc.SetCss("body{margin:0}#a{width:80px;height:40px;background:#ff0000;margin:10px}");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#a"), out NativeBounds b));
                Assert.That(b.X, Is.EqualTo(10).Within(0.01));
                Assert.That(b.Y, Is.EqualTo(10).Within(0.01));

                Texture2D image = renderer.RenderToTexture(doc, 200, 100, Color.white);
                try
                {
                    Assert.That(image.width, Is.EqualTo(200));
                    Assert.That(image.height, Is.EqualTo(100));
                    var red = new Color32(255, 0, 0, 255);
                    var white = new Color32(255, 255, 255, 255);
                    Assert.That(Near(PageTopDown(image, 20, 20), red), "inside the box (20,20) is red: " + PageTopDown(image, 20, 20));
                    Assert.That(Near(PageTopDown(image, 85, 45), red), "inside the box near its far corner is red: " + PageTopDown(image, 85, 45));
                    Assert.That(Near(PageTopDown(image, 5, 5), white), "the margin is the page colour: " + PageTopDown(image, 5, 5));
                    Assert.That(Near(PageTopDown(image, 150, 80), white), "outside the box is the page colour: " + PageTopDown(image, 150, 80));
                    Assert.That(Near(PageTopDown(image, 20, 60), white), "below the box is the page colour (the page is the right way up): " + PageTopDown(image, 20, 60));
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }
            }
        }

        // backdrop-filter reads the target: the core hands the host the shape and
        // the composed colour matrix, the host copies the target and draws the
        // shape over the copy. Orientation is exact on the offscreen path, so
        // the top half of the page is what the top-half element filters.
        [Test]
        public void Render_AppliesABackdropFilter_ToWhatIsBeneathIt()
        {
            Assume.That(HasGraphics, "a graphics device is required");
            using (var doc = new NativeDocument(200, 100))
            using (var renderer = new NativeDocumentRenderer())
            {
                doc.LoadHtml("<body><div id=top></div><div id=bottom></div><div id=glass></div></body>");
                doc.SetCss("body{margin:0}#top,#bottom,#glass{position:absolute;left:0;width:100%;height:50%}" +
                           "#top{top:0;background:#ff0000}#bottom{top:50%;background:#0000ff}#glass{top:0;backdrop-filter:invert(1)}");
                doc.Update(0);
                renderer.Sync(doc);
                Assert.That(renderer.BackdropDraws, Is.EqualTo(1), "the core published one backdrop-filter draw");
                Texture2D image = renderer.RenderToTexture(doc, 200, 100, Color.white);
                try
                {
                    Assert.That(Near(PageTopDown(image, 100, 25), new Color32(0, 255, 255, 255)), "the top half is the red beneath, inverted: " + PageTopDown(image, 100, 25));
                    Assert.That(Near(PageTopDown(image, 100, 75), new Color32(0, 0, 255, 255)), "the bottom half is untouched: " + PageTopDown(image, 100, 75));
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }
            }
        }

        [Test]
        public void Render_BlursTheBackdrop_AcrossAnEdge()
        {
            Assume.That(HasGraphics, "a graphics device is required");
            using (var doc = new NativeDocument(200, 100))
            using (var renderer = new NativeDocumentRenderer())
            {
                doc.LoadHtml("<body><div id=left></div><div id=right></div><div id=glass></div></body>");
                doc.SetCss("body{margin:0}#left,#right,#glass{position:absolute;top:0;height:100%}" +
                           "#left{left:0;width:50%;background:#ff0000}#right{left:50%;width:50%;background:#0000ff}#glass{left:0;width:100%;backdrop-filter:blur(10px)}");
                doc.Update(0);
                Texture2D image = renderer.RenderToTexture(doc, 200, 100, Color.white);
                try
                {
                    Color32 nearLeft = PageTopDown(image, 96, 50), nearRight = PageTopDown(image, 104, 50), far = PageTopDown(image, 10, 50);
                    Assert.That(nearLeft.b, Is.GreaterThan(30), "blue bled into the red side of the edge: " + nearLeft);
                    Assert.That(nearRight.r, Is.GreaterThan(30), "red bled into the blue side: " + nearRight);
                    Assert.That(Near(far, new Color32(255, 0, 0, 255)), "far from the edge the red is untouched: " + far);
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }
            }
        }

        [Test]
        public void Render_DrawsTextThroughTheAtlas()
        {
            Assume.That(HasGraphics, "a graphics device is required");
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            Assume.That(UnityFontBackend.RasterizerAvailable);
            using (var doc = new NativeDocument(240, 60))
            using (var fonts = new UnityFontBackend())
            using (var renderer = new NativeDocumentRenderer())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.LoadHtml("<body><p id=t>MMMM</p></body>");
                doc.SetCss("body{margin:0;background:#fff}p{margin:0;font-size:40px;color:#000}");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds b));
                Assert.That(b.Width, Is.GreaterThan(100));

                Texture2D image = renderer.RenderToTexture(doc, 240, 60, Color.white);
                try
                {
                    Assert.That(renderer.TextureCount, Is.GreaterThan(0), "the glyph atlas was uploaded");
                    int dark = 0;
                    for (int y = 0; y < (int)b.Height; y++)
                    {
                        for (int x = 0; x < (int)b.Width; x++)
                        {
                            Color32 c = PageTopDown(image, x, y);
                            if (c.r < 128 && c.g < 128 && c.b < 128) dark++;
                        }
                    }
                    Assert.That(dark, Is.GreaterThan(200), "the text's ink is on the page");
                    Assert.That(Near(PageTopDown(image, 230, 55), new Color32(255, 255, 255, 255)), "the corner stays the page colour");
                }
                finally
                {
                    Object.DestroyImmediate(image);
                }
            }
        }

        [Test]
        public void FontFace_LoadsThroughTheAssetReaderAndRegistersTheFamily()
        {
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            using (var doc = new NativeDocument(400, 100))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.SetBasePath(Path.GetFullPath("Packages/com.wevaui/Runtime/Resources/Fonts"));
                doc.LoadHtml("<body><span id=t>Heavy words</span></body>");
                doc.SetCss("@font-face{font-family:Heavy;src:url(Weva-Default-Bold.ttf)}@font-face{font-family:Heavy;src:url(Weva-Default-Italic.ttf);font-style:italic}body{margin:0}#t{font-size:24px}");
                Assert.That(doc.FontFaces().Count, Is.EqualTo(2), "the core lists both rules");
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(1), "one family served");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds regular));

                doc.SetCss("@font-face{font-family:Heavy;src:url(Weva-Default-Bold.ttf)}@font-face{font-family:Heavy;src:url(Weva-Default-Italic.ttf);font-style:italic}body{margin:0}#t{font-size:24px;font-family:Heavy}");
                fonts.SyncCssFontFaces(doc);
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds heavy));
                Assert.That(heavy.Width, Is.GreaterThan(regular.Width), "the bold @font-face file is wider than the UI face: " + heavy.Width + " vs " + regular.Width);
                Assert.That(fonts.FaceCount, Is.EqualTo(3), "the two files were adopted once each");
            }
        }

        // Renders each page named by WEVA_NATIVE_PARITY (";"-separated directories
        // holding <name>.html and <name>.css) at 1280x720 with the package's UI
        // face, writing unity.ppm and unity.png beside them for the render
        // comparison against the Godot host (compare_render.py's metric).
        [Test]
        public void Parity_RendersPagesForComparison()
        {
            string dirs = System.Environment.GetEnvironmentVariable("WEVA_NATIVE_PARITY");
            Assume.That(!string.IsNullOrEmpty(dirs), "WEVA_NATIVE_PARITY names the pages to render");
            Assume.That(HasGraphics, "a graphics device is required");
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Font symbols = Resources.Load<Font>("Fonts/NotoSansSymbols2-Regular");
            foreach (string dir in dirs.Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
                string html = Path.Combine(dir, name + ".html");
                string css = Path.Combine(dir, name + ".css");
                Assert.That(File.Exists(html), html);
                using (var doc = new NativeDocument(1280, 720))
                using (var fonts = new UnityFontBackend())
                using (var renderer = new NativeDocumentRenderer())
                {
                    ulong face = fonts.Adopt(font);
                    if (symbols != null) fonts.SetFallbacks(face, fonts.Adopt(symbols));
                    fonts.Install(doc, face);
                    doc.SetBasePath(Path.GetFullPath(dir));
                    doc.LoadHtml(File.ReadAllText(html));
                    doc.SetCss(File.Exists(css) ? File.ReadAllText(css) : "");
                    int families = fonts.SyncCssFontFaces(doc);
                    doc.Update(0);
                    Texture2D image = renderer.RenderToTexture(doc, 1280, 720, Color.white);
                    try
                    {
                        WritePpm(image, Path.Combine(dir, "unity.ppm"));
                        File.WriteAllBytes(Path.Combine(dir, "unity.png"), image.EncodeToPNG());
                    }
                    finally
                    {
                        Object.DestroyImmediate(image);
                    }
                    TestContext.WriteLine(name + ": " + renderer.BatchCount + " batches, " + renderer.TrianglesUploaded + " triangles, " +
                                          renderer.TextureCount + " textures, " + renderer.DrawsSkipped + " draws skipped, " + families + " @font-face families" +
                                          (fonts.LastError != null ? ", last font error: " + fonts.LastError : ""));
                }
            }
        }

        /// <summary>Writes a PPM (P6) of a readback texture, top row first, for the render comparison tooling.</summary>
        public static void WritePpm(Texture2D image, string path)
        {
            var bytes = new byte[image.width * image.height * 3];
            int i = 0;
            for (int y = image.height - 1; y >= 0; y--)
            {
                for (int x = 0; x < image.width; x++)
                {
                    Color32 c = image.GetPixel(x, y);
                    bytes[i++] = c.r;
                    bytes[i++] = c.g;
                    bytes[i++] = c.b;
                }
            }
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] header = System.Text.Encoding.ASCII.GetBytes("P6\n" + image.width + " " + image.height + "\n255\n");
                stream.Write(header, 0, header.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
