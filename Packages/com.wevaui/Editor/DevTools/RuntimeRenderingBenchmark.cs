using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Weva.Native;

namespace Weva.EditorTools
{
    /// <summary>Opt-in CPU stage audit. No scene assets are opened or saved.</summary>
    public static class RuntimeRenderingBenchmark
    {
        [Serializable] private sealed class Sample
        {
            public double update_ms, sync_ms, record_ms;
            public int batches, reused, uploaded_triangles;
            public bool rebuilt;
        }
        [Serializable] private sealed class Result
        {
            public string scenario, unity, device;
            public int warmup = 128, width = 1280, height = 720;
            public Sample[] samples = new Sample[2048];
        }

        public static void Run()
        {
            string fixtures = Environment.GetEnvironmentVariable("WEVA_PREPARATION_FIXTURE");
            string output = Environment.GetEnvironmentVariable("WEVA_PREPARATION_OUTPUT");
            if (string.IsNullOrEmpty(output) || Directory.Exists(output))
                throw new ArgumentException("output must be a new directory");
            Directory.CreateDirectory(output);
            foreach (string scenario in new[] { "idle-hud", "hud", "inventory" })
                RunScenario(fixtures, output, scenario);
        }

        private static void RunScenario(string fixtures, string output, string scenario)
        {
            string fixture = scenario == "inventory" ? "inventory" : "hud";
            bool idle = scenario == "idle-hud";
            var result = new Result { scenario = scenario, unity = Application.unityVersion, device = SystemInfo.graphicsDeviceName };
            var go = new GameObject("weva-runtime-benchmark");
            go.SetActive(false);
            var command = new CommandBuffer { name = "Weva.Runtime.CPUOnly" };
            try
            {
                var ui = go.AddComponent<WevaDocument>();
                ui.AutoInput = false;
                ui.SystemFontFallback = false;
                ui.Visible = false;
                ui.BasePath = Path.GetFullPath(fixtures);
                ui.InlineHtml = File.ReadAllText(Path.Combine(fixtures, fixture + ".html"));
                ui.InlineCss = File.ReadAllText(Path.Combine(fixtures, fixture + ".css"));
                var model = new Dictionary<string, object> { ["Depth"] = 30 };
                ui.Bind(model);
                go.SetActive(true);
                if (ui.Document == null) throw new InvalidOperationException(ui.LastError);
                ui.Document.SetViewport(1280, 720);
                ui.PrepareRenderer();
                var bar = ui.Query("#stamina-fill");
                var first = ui.Query("#item-0");
                var second = ui.Query("#item-1");
                var label = ui.Query(scenario == "inventory" ? "#item-quantity" : "#stamina-value");
                if (!label.IsValid || (scenario == "inventory" ? !first.IsValid || !second.IsValid : !bar.IsValid))
                    throw new InvalidOperationException("missing fixture targets");
                var values = new object[71];
                var widths = new string[71];
                for (int i = 0; i < values.Length; ++i) { values[i] = 30 + i; widths[i] = "width:" + (30 + i) + "%"; }
                var timer = new Stopwatch();
                for (int i = -result.warmup; i < result.samples.Length; ++i)
                {
                    int state = (i + result.warmup) % values.Length;
                    ulong serial = ui.Document.DrawSerial;
                    var sample = new Sample();
                    timer.Restart();
                    if (!idle)
                    {
                        model["Depth"] = values[state];
                        ui.RequestRefresh();
                        if (scenario == "hud") bar.SetAttribute("style", widths[state]);
                        else
                        {
                            first.SetAttribute("class", (state & 1) == 0 ? "inventory-slot chosen" : "inventory-slot");
                            second.SetAttribute("class", (state & 1) == 0 ? "inventory-slot" : "inventory-slot chosen");
                        }
                    }
                    ui.Prepare();
                    sample.update_ms = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    sample.rebuilt = ui.Renderer.Sync(ui.Document);
                    sample.sync_ms = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    command.Clear();
                    // Record only: no GPU submission, readback or frame-rate claim.
                    ui.Renderer.Draw(command, 1280, 720, 1, true);
                    sample.record_ms = timer.Elapsed.TotalMilliseconds;
                    sample.batches = ui.Renderer.BatchCount;
                    sample.reused = sample.rebuilt ? ui.Renderer.RunsReused : sample.batches;
                    sample.uploaded_triangles = sample.rebuilt ? ui.Renderer.TrianglesUploaded : 0;
                    if (idle && (sample.rebuilt || ui.Document.DrawSerial != serial))
                        throw new InvalidOperationException("idle document rebuilt");
                    if (!idle && i >= 0 && (!sample.rebuilt || label.Text != values[state].ToString()))
                        throw new InvalidOperationException("mutation did not update bound paint");
                    if (i >= 0) result.samples[i] = sample;
                }
                Texture2D cached = null, fresh = null;
                using var reference = new NativeDocumentRenderer();
                try
                {
                    cached = ui.Renderer.RenderToTexture(ui.Document, 1280, 720, Color.black);
                    fresh = reference.RenderToTexture(ui.Document, 1280, 720, Color.black);
                    Color32[] a = cached.GetPixels32(), b = fresh.GetPixels32();
                    for (int i = 0; i < a.Length; ++i)
                        if (!a[i].Equals(b[i])) throw new InvalidOperationException("cached render differs from fresh renderer");
                    File.WriteAllBytes(Path.Combine(output, scenario + ".png"), cached.EncodeToPNG());
                }
                finally
                {
                    if (cached != null) UnityEngine.Object.DestroyImmediate(cached);
                    if (fresh != null) UnityEngine.Object.DestroyImmediate(fresh);
                }
                File.WriteAllText(Path.Combine(output, scenario + ".json"), JsonUtility.ToJson(result));
            }
            finally { command.Release(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
