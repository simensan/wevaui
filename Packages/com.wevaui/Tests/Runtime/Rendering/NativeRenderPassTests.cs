// The URP pass draws what the core paints: a WevaDocument in front of a
// camera renders its draw list into the camera's target through
// UIBatchedRendererFeature / UIRenderGraphPass, documents stack in
// SortingOrder, a disabled document leaves the frame. Automated, unlike the
// on-demand capture in RenderGoldens: this is what says the pass works.
//
// Pixels are read from a camera.Render() into a RenderTexture. That path can
// come out vertically flipped relative to the Game view (see the memory on
// offscreen capture orientation), so every assertion here is about LEFT and
// RIGHT halves, never top and bottom.
#if WEVA_URP
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    public class NativeRenderPassTests {
        const int Width = 320;
        const int Height = 200;
        const int SettleFrames = 3;

        GameObject camGo;
        Camera cam;
        RenderTexture rt;
        Texture2D tex;

        [SetUp]
        public void SetUp() {
            camGo = new GameObject("render-pass-camera") { tag = "MainCamera" };
            cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            rt = new RenderTexture(Width, Height, 24);
            tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        }

        [TearDown]
        public void TearDown() {
            Object.Destroy(tex);
            Object.Destroy(rt);
            Object.Destroy(camGo);
        }

        static WevaDocument NewDocument(string name, string html, string css, int order = 0) {
            var go = new GameObject(name);
            go.SetActive(false);
            var doc = go.AddComponent<WevaDocument>();
            doc.AutoInput = false;
            doc.InlineHtml = html;
            doc.InlineCss = css;
            doc.SortingOrder = order;
            go.SetActive(true);
            Assert.That(doc.Document, Is.Not.Null, name + ": " + doc.LastError);
            doc.PrepareForRenderViewport(Width, Height);
            return doc;
        }

        void Capture() {
            cam.targetTexture = rt;
            cam.aspect = (float)Width / Height;
            cam.Render();
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
        }

        // The dominant colour of a column band, averaged over the full height:
        // orientation-independent, and a text line or a stray edge cannot flip it.
        Color Band(int x0, int x1) {
            float r = 0, g = 0, b = 0;
            int n = 0;
            for (int x = x0; x < x1; x++) {
                for (int y = 0; y < Height; y++) {
                    Color c = tex.GetPixel(x, y);
                    r += c.r; g += c.g; b += c.b; n++;
                }
            }
            return new Color(r / n, g / n, b / n);
        }

        static void AssertColour(Color got, float r, float g, float b, string what) {
            Assert.That(got.r, Is.EqualTo(r).Within(0.12f), what + " red (" + got + ")");
            Assert.That(got.g, Is.EqualTo(g).Within(0.12f), what + " green (" + got + ")");
            Assert.That(got.b, Is.EqualTo(b).Within(0.12f), what + " blue (" + got + ")");
        }

        [UnityTest]
        public IEnumerator Pass_DrawsTheDocument_StacksBySortingOrder_AndDropsADisabledOne() {
            Assume.That(UrpFeatureStatus.UrpActive, "the project renders with URP");

            // Left half red, right half transparent: the camera's black shows through.
            var a = NewDocument("doc-a",
                "<body><div id=left></div></body>",
                "html,body{margin:0;background:transparent}#left{position:absolute;left:0;top:0;width:50%;height:100%;background:#ff0000}");
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            Assert.That(UrpFeatureStatus.BatchedFeatureRegistered, "after a render, the status says the feature is there");
            AssertColour(Band(20, 140), 1f, 0f, 0f, "the left half is the document's red");
            AssertColour(Band(180, 300), 0f, 0f, 0f, "the right half is the camera's clear colour");

            // A second document with a higher order paints over the first:
            // green over the left half, blue on the right.
            var b = NewDocument("doc-b",
                "<body><div id=l></div><div id=r></div></body>",
                "html,body{margin:0;background:transparent}#l{position:absolute;left:0;top:0;width:50%;height:100%;background:#00ff00}#r{position:absolute;left:50%;top:0;width:50%;height:100%;background:#0000ff}",
                order: 5);
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            AssertColour(Band(20, 140), 0f, 1f, 0f, "the higher order draws over the red");
            AssertColour(Band(180, 300), 0f, 0f, 1f, "and fills the right half");

            // A lower order goes beneath: put b under a and the left is red again.
            b.SortingOrder = -5;
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            AssertColour(Band(20, 140), 1f, 0f, 0f, "the lower order is beneath the red");
            AssertColour(Band(180, 300), 0f, 0f, 1f, "where the red is transparent the blue shows");

            // A disabled document leaves the frame; an enabled one returns.
            b.gameObject.SetActive(false);
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            AssertColour(Band(180, 300), 0f, 0f, 0f, "disabled: the blue is gone");
            b.gameObject.SetActive(true);
            b.PrepareForRenderViewport(Width, Height);
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            AssertColour(Band(180, 300), 0f, 0f, 1f, "enabled again: the blue is back");

            Object.Destroy(a.gameObject);
            Object.Destroy(b.gameObject);
            yield return null;
            Capture();
            AssertColour(Band(20, 140), 0f, 0f, 0f, "destroyed: nothing is drawn");
        }

        [UnityTest]
        public IEnumerator Pass_FollowsAChange_WithoutAReload() {
            var doc = NewDocument("doc-change",
                "<body><div id=box class=red></div></body>",
                "html,body{margin:0;background:transparent}#box{position:absolute;left:0;top:0;width:50%;height:100%}.red{background:#ff0000}.blue{background:#0000ff}");
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            AssertColour(Band(20, 140), 1f, 0f, 0f, "red at first");
            doc.Query("#box").SetAttribute("class", "blue");
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            AssertColour(Band(20, 140), 0f, 0f, 1f, "an attribute change repaints on the next frames");
            Object.Destroy(doc.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Pass_DrawsText() {
            // White text on the left, nothing on the right: glyph coverage makes
            // the left band brighter than black, the right band stays black.
            var doc = NewDocument("doc-text",
                "<body><p>MMMMMMMM<br>MMMMMMMM<br>MMMMMMMM<br>MMMMMMMM</p></body>",
                "html,body{margin:0;background:transparent}p{margin:0;width:50%;color:#fff;font-size:40px;line-height:1;font-weight:700;overflow:hidden;white-space:nowrap}");
            for (int i = 0; i < SettleFrames; i++) yield return null;
            Capture();
            Color left = Band(0, 150), right = Band(180, 300);
            Assert.That(left.r + left.g + left.b, Is.GreaterThan(0.15f), "glyphs were drawn on the left (" + left + ")");
            AssertColour(right, 0f, 0f, 0f, "nothing on the right");
            Object.Destroy(doc.gameObject);
            yield return null;
        }
    }
}
#endif
