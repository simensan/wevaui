using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeAssetReloadTests
    {
        [UnityTest] public IEnumerator LinkedSheetUnderBasePath_Reloads() => ReloadAfter("theme.css", "imported");
        [UnityTest] public IEnumerator NestedImport_Reloads() => ReloadAfter("nested/part.css", "imported");
        [UnityTest] public IEnumerator DeletedImport_Reloads() => ReloadAfter("nested/part.css", "deleted");
        [UnityTest] public IEnumerator MovedImport_ReloadsFromItsOldPath() => ReloadAfter("nested/part.css", "moved");
        [UnityTest] public IEnumerator MissingImageCreation_Reloads() => ReloadAfter("icon.png", "imported");

        [Test]
        public void AssetReaderDecorator_CanRetainThePreviousReader()
        {
            using (var doc = new Weva.Native.NativeDocument(10, 10))
            {
                byte[] expected = { 1, 2, 3 };
                int reads = 0;
                doc.AssetReader = path => { ++reads; return expected; };
                var previous = doc.AssetReader;
                doc.AssetReader = path => previous(path);
                Assert.That(doc.AssetReader("asset.bin"), Is.SameAs(expected));
                Assert.That(reads, Is.EqualTo(1));
            }
        }

        [UnityTest]
        public IEnumerator ImageEdit_RefreshesTheRenderedPixels()
        {
            var go = new GameObject("image-reload-under-test");
            go.SetActive(false);
            var host = go.AddComponent<WevaDocument>();
            host.AutoInput = false;
            host.SystemFontFallback = false;
            host.BasePath = "Assets/WevaReloadReview";
            host.InlineHtml = "<img src=icon.png>";
            host.InlineCss = "html,body{margin:0}img{width:32px;height:32px}";
            var pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            pixel.SetPixel(0, 0, Color.red); pixel.Apply();
            byte[] red = pixel.EncodeToPNG();
            pixel.SetPixel(0, 0, Color.blue); pixel.Apply();
            byte[] blue = pixel.EncodeToPNG();
            byte[] current = red;
            host.AssetReader = path => path.EndsWith("/icon.png", StringComparison.Ordinal) ? current : null;
            using (var renderer = new Weva.Native.NativeDocumentRenderer())
            {
                Color32 Capture(string name)
                {
                    host.PrepareForRenderViewport(64, 64);
                    Texture2D image = renderer.RenderToTexture(host.Document, 64, 64, Color.black);
                    try
                    {
                        string dump = Environment.GetEnvironmentVariable("WEVA_ASSET_RELOAD_DUMP");
                        if (!string.IsNullOrEmpty(dump))
                        {
                            Directory.CreateDirectory(dump);
                            File.WriteAllBytes(Path.Combine(dump, name + ".png"), image.EncodeToPNG());
                        }
                        return image.GetPixel(10, 53);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(image); }
                }
                try
                {
                    go.SetActive(true);
                    yield return null;
                    Color32 before = Capture("before");
                    Assert.That(before.r, Is.GreaterThan(245));
                    Assert.That(before.b, Is.LessThan(10));
                    int generation = host.Generation;
                    current = blue;
                    Notify(new[] { host.BasePath + "/icon.png" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                    for (int i = 0; i < 10 && host.Generation == generation; ++i) yield return null;
                    Color32 after = Capture("after");
                    Assert.That(after.b, Is.GreaterThan(245));
                    Assert.That(after.r, Is.LessThan(10));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(pixel);
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        private const string FontDirectory = "Packages/com.wevaui/Runtime/Resources/Fonts/";

        private static WevaDocument FontHost(Func<string, byte[]> reader)
        {
            var go = new GameObject("font-reload-under-test");
            go.SetActive(false);
            var host = go.AddComponent<WevaDocument>();
            host.AutoInput = false;
            host.SystemFontFallback = false;
            host.BasePath = "Assets/WevaReloadReview";
            host.InlineHtml = "<span id=text>Hello AV</span>";
            host.InlineCss = "@font-face{font-family:ChangedFace;src:url(font.ttf)}#text{font:24px ChangedFace}";
            host.AssetReader = reader;
            go.SetActive(true);
            return host;
        }

        private static double TextWidth(WevaDocument host)
        {
            host.Document.Update(0);
            Assert.That(host.Document.TryGetBounds(host.Document.Query("#text"), out var bounds));
            return bounds.Width;
        }

        [UnityTest]
        public IEnumerator FontEditAndDeletion_RefreshMetricsAndFallback()
        {
            byte[] current = File.ReadAllBytes(FontDirectory + "Weva-Default.ttf");
            var host = FontHost(path => path.EndsWith("/font.ttf", StringComparison.Ordinal) ? current : null);
            try
            {
                yield return null;
                double before = TextWidth(host);
                current = File.ReadAllBytes(FontDirectory + "Weva-Default-Bold.ttf");
                int generation = host.Generation;
                string[] path = { host.BasePath + "/font.ttf" };
                Notify(path, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                for (int i = 0; i < 10 && host.Generation == generation; ++i) yield return null;
                Assert.That(TextWidth(host), Is.GreaterThan(before + 1), "changed bytes at the same URL use the new face");
                current = null;
                generation = host.Generation;
                Notify(Array.Empty<string>(), path, Array.Empty<string>(), Array.Empty<string>());
                for (int i = 0; i < 10 && host.Generation == generation; ++i) yield return null;
                Assert.That(TextWidth(host), Is.EqualTo(before).Within(0.01), "a deleted face returns to the default regular font");
            }
            finally { UnityEngine.Object.DestroyImmediate(host.gameObject); }
        }

        [Test]
        public void HostFontRegisteredAfterCss_SurvivesReload()
        {
            byte[] regular = File.ReadAllBytes(FontDirectory + "Weva-Default.ttf");
            var host = FontHost(path => regular);
            try
            {
                double initial = TextWidth(host);
                host.RegisterFontFamily("ChangedFace", Resources.Load<Font>("Fonts/Weva-Default-Bold"));
                double owned = TextWidth(host);
                Assert.That(owned, Is.GreaterThan(initial + 1));
                host.Reload();
                Assert.That(TextWidth(host), Is.EqualTo(owned).Within(0.01));
            }
            finally { UnityEngine.Object.DestroyImmediate(host.gameObject); }
        }

        private static IEnumerator ReloadAfter(string dependency, string change)
        {
            const string directory = "Assets/WevaReloadReview";
            var go = new GameObject("asset-reload-under-test");
            go.SetActive(false);
            var host = go.AddComponent<WevaDocument>();
            host.AutoInput = false;
            host.SystemFontFallback = false;
            host.BasePath = directory;
            host.InlineHtml = "<link rel=stylesheet href='styles/../theme.css'><div id=box></div><img src=icon.png style='width:10px;height:10px'>";
            int width = 73;
            host.AssetReader = path =>
            {
                string resolved = Path.GetFullPath(path);
                string css = resolved == Path.GetFullPath(directory + "/theme.css") ? "@import 'nested/part.css';" :
                    resolved == Path.GetFullPath(directory + "/nested/part.css") ? "#box{width:" + width + "px}" : null;
                return css == null ? null : System.Text.Encoding.UTF8.GetBytes(css);
            };
            try
            {
                go.SetActive(true);
                // ExecuteAlways can schedule an initial validation reload.
                yield return null;
                Assert.That(host.Document, Is.Not.Null, host.LastError);
                Assert.That(host.Document.TryGetBounds(host.Document.Query("#box"), out var before));
                Assert.That(before.Width, Is.EqualTo(73));
                host.Document.Draws();
                int generation = host.Generation;
                width = 117;
                Notify(new[] { directory + "/unrelated.css" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                yield return null;
                Assert.That(host.Generation, Is.EqualTo(generation), "an unrelated asset must not reload the document");

                string[] paths = { directory + "/" + dependency };
                Notify(change == "imported" ? paths : Array.Empty<string>(),
                    change == "deleted" ? paths : Array.Empty<string>(),
                    change == "moved" ? new[] { directory + "/new-name.css" } : Array.Empty<string>(),
                    change == "moved" ? paths : Array.Empty<string>());
                // Repeated notifications in the same refresh are coalesced.
                Notify(Array.Empty<string>(), paths, Array.Empty<string>(), Array.Empty<string>());
                Assert.That(host.Generation, Is.EqualTo(generation), "reload is deferred beyond the import callback");
                for (int i = 0; i < 10 && host.Generation == generation; ++i) yield return null;
                Assert.That(host.Generation, Is.EqualTo(generation + 1), "the dependency change reloads the document once");
                Assert.That(host.Document.TryGetBounds(host.Document.Query("#box"), out var after));
                Assert.That(after.Width, Is.EqualTo(117), "the refreshed stylesheet reaches layout");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void Notify(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var method = typeof(Weva.EditorTools.Documents.UIDocumentAssetWatcher).GetMethod("OnPostprocessAllAssets", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { imported, deleted, moved, movedFrom });
        }
    }
}
