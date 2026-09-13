#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Native;
using Weva.Rendering;

namespace Weva.Tests.RenderGoldens {
    // The core-hosted document through the REAL pipeline: a WevaDocument
    // in front of a camera, drawn by UIRenderPass (the in-pass path the
    // offscreen parity render does not exercise), captured to a PNG for a
    // person to look at. Run on demand:
    //   Unity -batchmode -runTests -testPlatform PlayMode
    //         -testFilter NativeGameViewCaptureTests
    // Output: .utmp/native-gameview/<name>.png. Like RenderGoldenCaptureTests,
    // capture is a tool: the assertions only say a frame was produced. The
    // same rig also captures the C# engine drawing the same page, so a black
    // frame can be told apart from a broken rig.
    //
    // Known: a camera.Render() capture can be vertically flipped relative to
    // the Game view (the backdrop path differs); colours and layout are what
    // to judge, not orientation.
    [Explicit("GPU capture of the native path — run on demand and look at the PNGs")]
    public class NativeGameViewCaptureTests {
        const int Width = 1280;
        const int Height = 720;
        const int SettleFrames = 6;
        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string OutDir => Path.Combine(RepoRoot, ".utmp", "native-gameview");

        [UnityTest]
        public IEnumerator Capture_native_check_page() {
            Directory.CreateDirectory(OutDir);
            var html = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/UI/native-check.html");
            var css = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/UI/native-check.css");
            Assert.That(html, Is.Not.Null, "Assets/UI/native-check.html");
            Assert.That(css, Is.Not.Null, "Assets/UI/native-check.css");

            var camGo = new GameObject("native-camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 1f);

            var rt = new RenderTexture(Width, Height, 24);
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);

            // The C# engine first, through the same rig, as the control.
            var csGo = new GameObject("csharp-document");
            var cs = csGo.AddComponent<Weva.WevaLegacyDocument>();
            var bf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            cs.GetType().GetField("documentAsset", bf).SetValue(cs, html);
            cs.GetType().GetField("stylesheetAssets", bf).SetValue(cs, new[] { css });
            cs.Rebuild();
            var regField = cs.GetType().GetField("registered", bf);
            if (regField != null && !(bool)regField.GetValue(cs)) {
                UIPaintSourceRegistry.Register(cs);
                regField.SetValue(cs, true);
            }
            var prepare = cs.GetType().GetMethod("PrepareForRenderViewport", bf);
            var update = cs.GetType().GetMethod("Update", bf);
            prepare.Invoke(cs, new object[] { Width, Height });
            for (int i = 0; i < SettleFrames; i++) { update.Invoke(cs, null); yield return null; }
            Capture(cam, rt, tex, "csharp-native-check");
            Debug.Log("[NativeGameView] sources registered with the C# document: " + UIPaintSourceRegistry.Snapshot().Count);
            Object.Destroy(csGo);
            yield return null;

            var docGo = new GameObject("native-document");
            docGo.SetActive(false);
            var doc = docGo.AddComponent<WevaDocument>();
            doc.DocumentAsset = html;
            doc.StylesheetAssets = new[] { css };
            doc.BasePath = Path.Combine(RepoRoot, "Assets", "UI");
            doc.AutoInput = false;
            docGo.SetActive(true);   // OnEnable creates the core document
            Assert.That(doc.Document, Is.Not.Null, "the native document was created: " + doc.LastError);

            try {
                doc.PrepareForRenderViewport(Width, Height);
                for (int i = 0; i < SettleFrames; i++) yield return null;
                Capture(cam, rt, tex, "native-check");
                Debug.Log("[NativeGameView] native: sources=" + UIPaintSourceRegistry.Snapshot().Count
                    + " batches=" + (doc.Renderer != null ? doc.Renderer.BatchCount : -1)
                    + " serial=" + (doc.Renderer != null ? doc.Renderer.Serial : 0)
                    + " drawSerial=" + doc.Document.DrawSerial
                    + " lastError=" + doc.LastError);

                // Block 1 with insets, as a notched device would supply them.
                doc.Document.SetSafeAreaInsets(44, 0, 0, 8);
                for (int i = 0; i < SettleFrames; i++) yield return null;
                Capture(cam, rt, tex, "native-check-insets");

                // Block 7 mid-way through a smooth scroll: the list eases, so a
                // frame taken shortly after the call shows it between rows.
                uint list = doc.Document.Query("#list");
                Assert.That(list, Is.Not.EqualTo(0u), "#list exists");
                doc.Document.SetElementScroll(list, 0, 100);
                yield return null;
                Assert.That(doc.Document.IsAnimating, Is.True, "a smooth scroll reports the document animating");
                yield return null;
                Capture(cam, rt, tex, "native-check-scrolling");
                yield return new WaitForSeconds(0.6f);
                doc.Document.TryGetElementScroll(list, out double sx, out double sy, out double mx, out double my);
                Assert.That(sy, Is.EqualTo(100).Within(0.01), "and settles on the target");
            } finally {
                Object.Destroy(tex);
                Object.Destroy(rt);
                Object.Destroy(docGo);
                Object.Destroy(camGo);
            }
        }

        static void Capture(Camera cam, RenderTexture rt, Texture2D tex, string name) {
            var prevTarget = cam.targetTexture;
            float prevAspect = cam.aspect;
            cam.targetTexture = rt;
            cam.aspect = (float)Width / Height;
            cam.Render();
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = prevTarget;
            cam.aspect = prevAspect;
            string outPath = Path.Combine(OutDir, name + ".png");
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log("[NativeGameView] captured " + outPath);
        }
    }
}
#endif
