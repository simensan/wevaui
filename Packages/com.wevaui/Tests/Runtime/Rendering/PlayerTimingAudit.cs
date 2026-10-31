#if WEVA_URP
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Rendering;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering
{
    // Explicitly invoked diagnostic, not a timing threshold in correctness CI.
    public class PlayerTimingAudit
    {
        [Serializable] private sealed class Sample
        {
            public ulong start_timestamp;
            public double cpu_frame_ms, cpu_main_ms, cpu_render_ms, present_wait_ms, gpu_frame_ms;
            public bool focused, gpu_available;
        }
        [Serializable] private sealed class Result
        {
            public string scenario, unity, device, graphics_api;
            public int round, width, height, target_fps, warmup = 120, polls, duplicates, missing, unavailable_gpu;
            public ulong gpu_timer_frequency, cpu_timer_frequency;
            public ulong expected_render_serial, synced_render_serial;
            public List<Sample> samples = new List<Sample>();
        }

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator CapturePresentedFrames()
        {
            string fixtures = Environment.GetEnvironmentVariable("WEVA_PLAYER_TIMING_FIXTURES");
            string output = Environment.GetEnvironmentVariable("WEVA_PLAYER_TIMING_OUTPUT");
            int targetFps = int.Parse(Environment.GetEnvironmentVariable("WEVA_PLAYER_TIMING_FPS") ?? "60");
            Assert.That(targetFps, Is.GreaterThanOrEqualTo(0));
            if (Application.isEditor || string.IsNullOrEmpty(output)) Assert.Ignore("opt-in standalone player diagnostic");
            Assert.That(UrpFeatureStatus.UrpActive, Is.True);
            Assert.That(FrameTimingManager.IsFeatureEnabled(), Is.True, "frame timing must be enabled");
            Assert.That(Directory.Exists(output), Is.False, "preserve previous evidence");
            Directory.CreateDirectory(output);
            int oldVsync = QualitySettings.vSyncCount, oldCap = Application.targetFrameRate;
            bool oldBackground = Application.runInBackground;
            var cameraObject = new GameObject("timing-camera") { tag = "MainCamera" };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            GameObject documentObject = null;
            try
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFps == 0 ? -1 : targetFps;
                Application.runInBackground = true;
                Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
                for (int i = 0; i < 20; ++i) yield return null;
                Assert.That(Screen.width, Is.EqualTo(1280));
                Assert.That(Screen.height, Is.EqualTo(720));
                var timings = new FrameTiming[1];
                for (int round = 0; round < 3; ++round)
                {
                    string[] cases = { "background", "idle-hud", "updating-hud", "inventory", "updating-inventory" };
                    if ((round & 1) != 0) Array.Reverse(cases);
                    foreach (string scenario in cases)
                    {
                        WevaDocument ui = null;
                        var model = new Dictionary<string, object> { ["Depth"] = 30 };
                        bool inventory = scenario.Contains("inventory"), updating = scenario.StartsWith("updating-");
                        if (scenario != "background")
                        {
                            string fixture = inventory ? "inventory" : "hud";
                            documentObject = new GameObject("timing-document");
                            documentObject.SetActive(false);
                            ui = documentObject.AddComponent<WevaDocument>();
                            ui.AutoInput = false;
                            ui.SystemFontFallback = false;
                            ui.BasePath = Path.GetFullPath(fixtures);
                            ui.InlineHtml = File.ReadAllText(Path.Combine(fixtures, fixture + ".html"));
                            ui.InlineCss = File.ReadAllText(Path.Combine(fixtures, fixture + ".css"));
                            ui.Bind(model);
                            documentObject.SetActive(true);
                            Assert.That(ui.Document, Is.Not.Null, ui.LastError);
                            ui.PrepareRenderer();
                        }
                        var result = new Result { scenario = scenario, round = round, target_fps = targetFps, unity = Application.unityVersion,
                            device = SystemInfo.graphicsDeviceName, graphics_api = SystemInfo.graphicsDeviceType.ToString(),
                            width = Screen.width, height = Screen.height, gpu_timer_frequency = FrameTimingManager.GetGpuTimerFrequency(),
                            cpu_timer_frequency = FrameTimingManager.GetCpuTimerFrequency() };
                        var label = ui == null ? WevaElement.None : ui.Query(inventory ? "#item-quantity" : "#stamina-value");
                        var bar = ui == null ? WevaElement.None : ui.Query("#stamina-fill");
                        var first = ui == null ? WevaElement.None : ui.Query("#item-0");
                        var second = ui == null ? WevaElement.None : ui.Query("#item-1");
                        ulong last = 0;
                        ulong expectedRenderSerial = 0;
                        int state = 0;
                        for (int frame = -result.warmup; result.samples.Count < 600 && frame < 5000; ++frame)
                        {
                            state = 30 + ((frame + result.warmup) % 71);
                            if (updating)
                            {
                                model["Depth"] = state;
                                ui.RequestRefresh();
                                if (!inventory) bar.SetAttribute("style", "width:" + state + "%");
                                else
                                {
                                    first.SetAttribute("class", (state & 1) == 0 ? "inventory-slot chosen" : "inventory-slot");
                                    second.SetAttribute("class", (state & 1) == 0 ? "inventory-slot" : "inventory-slot chosen");
                                }
                            }
                            FrameTimingManager.CaptureFrameTimings();
                            // The next Update can advance CPU paint before this
                            // coroutine resumes. Check the serial from the frame
                            // that actually had an opportunity to render.
                            expectedRenderSerial = ui == null ? 0 : ui.Document.DrawSerial;
                            yield return null;
                            uint count = FrameTimingManager.GetLatestTimings(1, timings);
                            if (frame < 0) { if (count > 0) last = timings[0].frameStartTimestamp; continue; }
                            ++result.polls;
                            if (count == 0) { ++result.missing; continue; }
                            FrameTiming t = timings[0];
                            if (t.frameStartTimestamp <= last) { ++result.duplicates; continue; }
                            last = t.frameStartTimestamp;
                            bool gpuAvailable = t.gpuFrameTime > 0 && !double.IsNaN(t.gpuFrameTime) && !double.IsInfinity(t.gpuFrameTime);
                            if (!gpuAvailable) ++result.unavailable_gpu;
                            result.samples.Add(new Sample { start_timestamp = last, cpu_frame_ms = t.cpuFrameTime,
                                cpu_main_ms = t.cpuMainThreadFrameTime, cpu_render_ms = t.cpuRenderThreadFrameTime,
                                present_wait_ms = t.cpuMainThreadPresentWaitTime, gpu_frame_ms = t.gpuFrameTime,
                                focused = Application.isFocused, gpu_available = gpuAvailable });
                        }
                        string name = scenario + "-" + round;
                        result.expected_render_serial = expectedRenderSerial;
                        result.synced_render_serial = ui == null ? 0 : ui.Renderer.Serial;
                        File.WriteAllText(Path.Combine(output, name + ".json"), JsonUtility.ToJson(result));
                        Assert.That(result.samples.Count, Is.EqualTo(600), "missing frame timings; retained partial evidence");
                        Assert.That(result.unavailable_gpu, Is.LessThan(result.samples.Count), "all GPU timings unavailable");
                        Assert.That(result.samples.TrueForAll(s => s.cpu_frame_ms > 0), Is.True, "CPU frame timing unavailable");
                        if (ui != null)
                        {
                            Assert.That(result.synced_render_serial, Is.GreaterThanOrEqualTo(result.expected_render_serial), "on-screen render pass synchronized timed UI");
                            ui.Prepare();
                            Assert.That(label.Text, Is.EqualTo(updating ? state.ToString() : "30"));
                        }
                        Capture(camera, output, name, ui != null);
                        Debug.Log("PlayerTimingAudit " + name + ": " + result.samples.Count + " frames; " + result.unavailable_gpu + " unavailable GPU timings");
                        if (documentObject != null) UnityEngine.Object.Destroy(documentObject);
                        documentObject = null;
                        yield return null;
                    }
                }
            }
            finally
            {
                if (documentObject != null) UnityEngine.Object.Destroy(documentObject);
                UnityEngine.Object.Destroy(cameraObject);
                QualitySettings.vSyncCount = oldVsync;
                Application.targetFrameRate = oldCap;
                Application.runInBackground = oldBackground;
            }
        }

        private static void Capture(Camera camera, string output, string name, bool hasUi)
        {
            var target = new RenderTexture(1280, 720, 24);
            var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
                int bright = 0;
                foreach (Color32 pixel in image.GetPixels32()) if (pixel.r > 64 || pixel.g > 64 || pixel.b > 64) ++bright;
                Assert.That(hasUi ? bright > 1000 : bright == 0, Is.True, "camera must render expected UI/control");
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(image);
            }
        }
    }
}
#endif
