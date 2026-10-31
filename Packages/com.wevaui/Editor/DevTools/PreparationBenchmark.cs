using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Weva.Native;

namespace Weva.EditorTools
{
    /// <summary>Opt-in batch diagnostic; no scene assets are opened or saved.</summary>
    public static class PreparationBenchmark
    {
        [Serializable] private sealed class Result
        {
            public string mode, unity_version, device, html, css;
            public int width = 1280, height = 720, initial_width, initial_height;
            public double activation_cpu_ms, loading_cpu_ms, loading_renderer_sync_cpu_ms, opening_cpu_ms, renderer_sync_cpu_ms, first_render_readback_ms;
            public long prepared_draw_serial, shown_draw_serial;
            public RenderStages first_render, retained_render;
        }

        [Serializable] private sealed class RenderStages
        {
            public double target_create_ms, record_ms, execute_ms, readback_ms, cleanup_ms;
        }

        // Mirrors the offscreen diagnostic sequence using the production Draw
        // implementation. CPU call durations are not GPU timestamp queries.
        private static Texture2D MeasureRender(WevaDocument ui, int width, int height, out RenderStages stages)
        {
            stages = new RenderStages();
            var timer = Stopwatch.StartNew();
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var cmd = new CommandBuffer { name = "Weva.Preparation.Stages" };
            Texture2D image = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (!target.Create()) throw new InvalidOperationException("offscreen target creation failed");
                stages.target_create_ms = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                cmd.SetRenderTarget(target);
                cmd.ClearRenderTarget(true, true, Color.black);
                ui.Renderer.Draw(cmd, width, height, SystemInfo.graphicsUVStartsAtTop ? 1 : -1, true, NativeRenderTarget.Of(target));
                stages.record_ms = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                Graphics.ExecuteCommandBuffer(cmd);
                stages.execute_ms = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                RenderTexture.active = target;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false, false);
                stages.readback_ms = timer.Elapsed.TotalMilliseconds;
                return image;
            }
            catch
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                throw;
            }
            finally
            {
                timer.Restart();
                RenderTexture.active = previous;
                cmd.Release();
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                stages.cleanup_ms = timer.Elapsed.TotalMilliseconds;
            }
        }

        // Unity -batchmode -executeMethod Weva.EditorTools.PreparationBenchmark.Run -quit
        // WEVA_PREPARATION_FIXTURE contains inventory.html and inventory.css;
        // WEVA_PREPARATION_OUTPUT is a new output directory; mode is
        // cold/prepared/renderer-prepared.
        public static void Run()
        {
            string fixtures = Environment.GetEnvironmentVariable("WEVA_PREPARATION_FIXTURE");
            string output = Environment.GetEnvironmentVariable("WEVA_PREPARATION_OUTPUT");
            string mode = Environment.GetEnvironmentVariable("WEVA_PREPARATION_MODE");
            if (mode != "cold" && mode != "prepared" && mode != "renderer-prepared")
                throw new ArgumentException("mode must be cold, prepared or renderer-prepared");
            if (string.IsNullOrEmpty(output) || Directory.Exists(output)) throw new ArgumentException("output must be a new directory");
            string html = Path.GetFullPath(Path.Combine(fixtures, "inventory.html"));
            string css = Path.GetFullPath(Path.Combine(fixtures, "inventory.css"));
            string markup = File.ReadAllText(html), styles = File.ReadAllText(css);
            var result = new Result { mode = mode, unity_version = Application.unityVersion,
                device = SystemInfo.graphicsDeviceName, html = html, css = css,
                initial_width = Screen.width, initial_height = Screen.height };
            var go = new GameObject("weva-preparation-benchmark");
            go.SetActive(false);
            Texture2D image = null;
            try
            {
                var ui = go.AddComponent<WevaDocument>();
                ui.AutoInput = false;
                ui.SystemFontFallback = false;
                ui.BasePath = Path.GetDirectoryName(html);
                ui.InlineHtml = markup;
                ui.InlineCss = styles;
                ui.Bind(new System.Collections.Generic.Dictionary<string, object> { ["Depth"] = 1 });
                ui.Visible = mode == "cold";
                var timer = Stopwatch.StartNew();
                go.SetActive(true);
                if (ui.Document == null) throw new InvalidOperationException(ui.LastError);
                // Explicit dimensions also cover batch editors whose initial
                // Game view differs from the requested command-line size.
                ui.Document.SetViewport(result.width, result.height);
                ui.Prepare();
                result.activation_cpu_ms = timer.Elapsed.TotalMilliseconds;
                if (ui.Query("#item-quantity").Text != "1")
                    throw new InvalidOperationException("inventory quantity binding was not applied");
                if (mode != "cold")
                {
                    result.loading_cpu_ms = result.activation_cpu_ms;
                    if (mode == "renderer-prepared")
                    {
                        long loadingSyncStart = Stopwatch.GetTimestamp();
                        ui.PrepareRenderer();
                        result.loading_renderer_sync_cpu_ms = (Stopwatch.GetTimestamp() - loadingSyncStart) * 1000.0 / Stopwatch.Frequency;
                        result.loading_cpu_ms += result.loading_renderer_sync_cpu_ms;
                    }
                    result.prepared_draw_serial = (long)ui.Document.DrawSerial;
                    if (ui.Visible) throw new InvalidOperationException("preparation showed the document");
                    timer.Restart();
                    ui.Visible = true;
                    ui.Prepare();
                }
                // CPU resource synchronization is included in opening; this
                // does not imply GPU execution has completed.
                long syncStart = Stopwatch.GetTimestamp();
                bool rebuilt = ui.Renderer.Sync(ui.Document);
                result.renderer_sync_cpu_ms = (Stopwatch.GetTimestamp() - syncStart) * 1000.0 / Stopwatch.Frequency;
                if (mode == "renderer-prepared" && rebuilt)
                    throw new InvalidOperationException("show rebuilt prepared renderer resources");
                result.opening_cpu_ms = timer.Elapsed.TotalMilliseconds;
                result.shown_draw_serial = (long)ui.Document.DrawSerial;
                if (mode != "cold" && result.prepared_draw_serial != result.shown_draw_serial)
                    throw new InvalidOperationException("show rebuilt unchanged prepared paint");
                timer.Restart();
                image = MeasureRender(ui, result.width, result.height, out result.first_render);
                result.first_render_readback_ms = timer.Elapsed.TotalMilliseconds;
                Directory.CreateDirectory(output);
                File.WriteAllBytes(Path.Combine(output, "shown.png"), image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
                image = MeasureRender(ui, result.width, result.height, out result.retained_render);
                File.WriteAllBytes(Path.Combine(output, "retained.png"), image.EncodeToPNG());
                File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(result, true));
            }
            finally
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
