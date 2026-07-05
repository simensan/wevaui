#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define WEVA_DEVTOOLS_RECORDER
#endif

using System;
using System.Globalization;
using System.Text;
#if WEVA_DEVTOOLS_RECORDER
using Unity.Profiling;
using Weva.Profiling;
#endif

namespace Weva.DevTools {
    // Aggregates per-frame timings and formats the corner readout. In editor /
    // development builds it taps into UIProfilerMarkers via ProfilerRecorder so
    // we get real cascade / layout / paint nanoseconds without re-instrumenting
    // the engines. In release builds (where UIProfilerMarkers compile out) we
    // still expose the public API; samples just stay at 0.
    //
    // FPS and GC delta are sampled directly here so they work in both build
    // configurations. The smoothing window matches Chrome DevTools' "FPS
    // meter" — short enough to react, long enough to read.
    public sealed class PerfReadout : IDisposable {
        public int SmoothingFrames { get; set; } = 60;

        double cascadeMsAvg, layoutMsAvg, paintMsAvg;
        double frameMsAvg;
        long lastGcBytes;
        long gcDeltaBytes;
        int sampleCount;

#if WEVA_DEVTOOLS_RECORDER
        // Two cascade recorders so the steady-state incremental path AND the
        // cold ComputeAll path both light up. Whichever fired this frame
        // reports a non-zero LastValue; we sum them so the readout is the
        // total time the cascade stage spent.
        ProfilerRecorder cascadeFullRecorder;
        ProfilerRecorder cascadeIncRecorder;
        ProfilerRecorder layoutRecorder;
        ProfilerRecorder paintRecorder;
        bool recordersStarted;
#endif

        public double CascadeMs => cascadeMsAvg;
        public double LayoutMs => layoutMsAvg;
        public double PaintMs => paintMsAvg;
        public double FrameMs => frameMsAvg;
        public double Fps => frameMsAvg > 0 ? 1000.0 / frameMsAvg : 0;
        public long GcDeltaBytes => gcDeltaBytes;
        public int SampleCount => sampleCount;

        public void Start() {
#if WEVA_DEVTOOLS_RECORDER
            if (recordersStarted) return;
            cascadeFullRecorder = ProfilerRecorder.StartNew(UIProfilerMarkers.CascadeComputeAll.Inner, capacity: 1);
            cascadeIncRecorder = ProfilerRecorder.StartNew(UIProfilerMarkers.CascadeIncrementalApply.Inner, capacity: 1);
            layoutRecorder = ProfilerRecorder.StartNew(UIProfilerMarkers.LayoutBuild.Inner, capacity: 1);
            paintRecorder = ProfilerRecorder.StartNew(UIProfilerMarkers.PaintConvert.Inner, capacity: 1);
            recordersStarted = true;
#endif
            lastGcBytes = GC.GetTotalMemory(forceFullCollection: false);
        }

        public void Dispose() {
#if WEVA_DEVTOOLS_RECORDER
            if (!recordersStarted) return;
            cascadeFullRecorder.Dispose();
            cascadeIncRecorder.Dispose();
            layoutRecorder.Dispose();
            paintRecorder.Dispose();
            recordersStarted = false;
#endif
        }

        public void RecordFrame(double frameSeconds) {
            double frameMs = frameSeconds * 1000.0;
            // Exponential moving average — single-divide form, keeps a steady
            // state without keeping a circular buffer.
            int n = sampleCount < SmoothingFrames ? sampleCount + 1 : SmoothingFrames;
            double weight = 1.0 / n;
            frameMsAvg = frameMsAvg + (frameMs - frameMsAvg) * weight;

            double cMs = ReadMarkerMs(0);
            double lMs = ReadMarkerMs(1);
            double pMs = ReadMarkerMs(2);
            cascadeMsAvg = cascadeMsAvg + (cMs - cascadeMsAvg) * weight;
            layoutMsAvg = layoutMsAvg + (lMs - layoutMsAvg) * weight;
            paintMsAvg = paintMsAvg + (pMs - paintMsAvg) * weight;

            long now = GC.GetTotalMemory(forceFullCollection: false);
            long delta = now - lastGcBytes;
            // Negative deltas only happen on GC collection — clamp to 0 so the
            // readout reflects allocation pressure, not net heap motion.
            gcDeltaBytes = delta > 0 ? delta : 0;
            lastGcBytes = now;
            sampleCount++;
        }

        public void RecordPhaseMs(int phase, double ms) {
            int n = sampleCount < SmoothingFrames ? sampleCount + 1 : SmoothingFrames;
            double weight = 1.0 / n;
            switch (phase) {
                case 0: cascadeMsAvg = cascadeMsAvg + (ms - cascadeMsAvg) * weight; break;
                case 1: layoutMsAvg = layoutMsAvg + (ms - layoutMsAvg) * weight; break;
                case 2: paintMsAvg = paintMsAvg + (ms - paintMsAvg) * weight; break;
            }
        }

        public string Format() {
            var sb = new StringBuilder(96);
            sb.Append("FPS ").Append(Fps.ToString("F0", CultureInfo.InvariantCulture));
            sb.Append("  frame ").Append(frameMsAvg.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms\n");
            sb.Append("cascade ").Append(cascadeMsAvg.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms\n");
            sb.Append("layout  ").Append(layoutMsAvg.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms\n");
            sb.Append("paint   ").Append(paintMsAvg.ToString("F2", CultureInfo.InvariantCulture)).Append(" ms\n");
            sb.Append("alloc   ").Append(FormatBytes(gcDeltaBytes));
            return sb.ToString();
        }

        public void Reset() {
            cascadeMsAvg = layoutMsAvg = paintMsAvg = frameMsAvg = 0;
            gcDeltaBytes = 0;
            sampleCount = 0;
            lastGcBytes = GC.GetTotalMemory(forceFullCollection: false);
        }

        double ReadMarkerMs(int which) {
#if WEVA_DEVTOOLS_RECORDER
            if (!recordersStarted) return 0;
            // For cascade we sum both ComputeAll and IncrementalApply because
            // only one of them fires per frame depending on whether the gate
            // skipped or not. For layout/paint there's a single phase marker.
            switch (which) {
                case 0: return (cascadeFullRecorder.LastValue + cascadeIncRecorder.LastValue) / 1_000_000.0;
                case 1: return layoutRecorder.LastValue / 1_000_000.0;
                case 2: return paintRecorder.LastValue / 1_000_000.0;
                default: return 0;
            }
#else
            return 0;
#endif
        }

        static string FormatBytes(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) {
                double kb = bytes / 1024.0;
                return kb.ToString("F1", CultureInfo.InvariantCulture) + " KB";
            }
            double mb = bytes / (1024.0 * 1024.0);
            return mb.ToString("F2", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
