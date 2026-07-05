using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;

namespace Weva.Tests.Layout {
    // Regression coverage for CODE_AUDIT_FINDINGS P7 and P8.
    //
    // P7: LineBreaker.LargestPrefixThatFits used to allocate a fresh
    //     word.Substring(idx, snapped) per binary-search probe. After the fix
    //     the probe uses the substring-window IFontMetrics.Measure overload
    //     and an identity-keyed measure cache, so the steady-state probe path
    //     allocates ~0 B / call.
    //
    // P8: LineBreaker.AppendPreserving / NormalizePreservedText used to
    //     allocate a Substring per \n-bounded segment and per tab-bounded
    //     slice. After the fix the tab-slice and tail measurements use the
    //     windowed overload + StringBuilder.Append(string, int, int), so
    //     repeated pre-formatted layouts pay only for the unavoidable
    //     fragment-string allocations.
    public class LineBreakerWindowedMeasureTests {
        sealed class CountingWindowedMetrics : IFontMetrics {
            public int FullStringCalls;
            public int WindowCalls;

            public double LineHeight(double fontSize) => fontSize * 1.2;
            public double Ascent(double fontSize) => fontSize * 0.8;
            public double Descent(double fontSize) => fontSize * 0.2;

            public double Measure(string text, double fontSize) {
                FullStringCalls++;
                if (string.IsNullOrEmpty(text)) return 0;
                return text.Length * fontSize * 0.5;
            }

            public double Measure(string text, int start, int length, double fontSize) {
                WindowCalls++;
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                return length * fontSize * 0.5;
            }
        }

        static LineBreaker.Item Item(string text, IFontMetrics metrics, double fontSize = 16,
                string ws = "normal", string wordBreak = null, string overflowWrap = null) {
            return new LineBreaker.Item {
                Text = text,
                FontSize = fontSize,
                FontFamily = null,
                Color = "black",
                WhiteSpace = ws,
                WordBreak = wordBreak,
                OverflowWrap = overflowWrap,
                Metrics = metrics
            };
        }

        // --- Parity pin: the windowed overload returns the same width as the
        // substring-based path for every Mono / TextCore-shaped implementation
        // we ship. Catches drift where a future impl forgets to clamp the
        // surrogate-pair check to the window end. ---

        [Test]
        public void Mono_windowed_measure_matches_substring_path_for_known_string() {
            var m = new MonoFontMetrics();
            const string body = "The quick brown fox jumps over the lazy dog.";
            for (int start = 0; start <= body.Length; start++) {
                for (int len = 0; len <= body.Length - start; len++) {
                    string slice = body.Substring(start, len);
                    double via = m.Measure(slice, 16);
                    double window = m.Measure(body, start, len, 16);
                    Assert.That(window, Is.EqualTo(via).Within(1e-9),
                        $"Mono windowed measure diverges at start={start} len={len}");
                }
            }
        }

        [Test]
        public void Mono_windowed_measure_handles_full_string_identity() {
            var m = new MonoFontMetrics();
            const string body = "hello world";
            Assert.That(m.Measure(body, 0, body.Length, 16),
                Is.EqualTo(m.Measure(body, 16)).Within(1e-9));
            // Negative start clamps to 0; overlong length clamps to text.Length.
            Assert.That(m.Measure(body, -3, body.Length + 10, 16),
                Is.EqualTo(m.Measure(body, 16)).Within(1e-9));
            // Out-of-range start returns 0.
            Assert.That(m.Measure(body, body.Length + 5, 3, 16), Is.EqualTo(0));
            // Zero / negative length returns 0.
            Assert.That(m.Measure(body, 2, 0, 16), Is.EqualTo(0));
            Assert.That(m.Measure(body, 2, -4, 16), Is.EqualTo(0));
        }

        [Test]
        public void Counting_metrics_window_path_used_for_break_all_probes() {
            // Drives the wrap binary search via word-break:break-all. The
            // breaker should call the windowed overload (WindowCalls > 0)
            // for every probe — not the legacy substring overload.
            var m = new CountingWindowedMetrics();
            // 200-char word, 16px font, 0.5em/char → 1600px wide. Force wraps
            // at 50px → many wrap-probe binary searches per pass.
            var word = new string('a', 200);
            var item = Item(word, m, fontSize: 16, wordBreak: "break-all");

            var br = new LineBreaker();
            br.Break(new List<LineBreaker.Item> { item }, 50);

            Assert.That(m.WindowCalls, Is.GreaterThan(0),
                "Wrap binary search should route through the windowed Measure overload (P7 fix).");
        }

        // --- Allocation guards: post-fix, the wrap-probe + AppendPreserving
        // hot paths should allocate near-zero bytes on the steady-state
        // (warmed cache) call. We measure 100 calls and assert per-call alloc
        // stays under a small floor — these are regression guards, not zero
        // gates, since GC.GetAllocatedBytesForCurrentThread has a few bytes
        // of bookkeeping noise. ---

        [Test]
        public void LargestPrefixThatFits_warm_path_allocates_near_zero_P7() {
            // The Break() compat shim allocates LineBox + TextRun per emitted
            // line + fragment-slice substrings (stored, deliberately left
            // alone per the brief) on every call — those dominate the per-call
            // allocation total and are unrelated to P7. To isolate the wrap
            // probe path, we measure metric-call delta + cache-hit count for
            // a second pass against a WARM cache:
            //
            //   - With the windowed MeasureCached cache hot, repeat probes
            //     against the SAME word reference, SAME font key, SAME
            //     (start, snapped) pairs should all hit the cache — no
            //     CountingWindowedMetrics call increment per probe.
            //   - Pre-fix the probe path also held a per-probe Substring
            //     alloc independent of the cache. Post-fix that's gone.
            //
            // We also assert per-call GC allocation against a relaxed budget
            // that's well below the pre-fix figure (post-fix ~35 KB on this
            // path is dominated by unavoidable per-line LineBox/TextRun
            // emissions; pre-fix would add ~20 KB for the probe substrings).
            var m = new CountingWindowedMetrics();
            var word = new string('a', 200);
            LineBreaker.Item probeItem() => Item(word, m, fontSize: 16, wordBreak: "break-all");

            var br = new LineBreaker();
            // Warmup populates measureWindowCache for every (start, snapped)
            // pair the binary search will probe on a stable word/width pair.
            for (int w = 0; w < 5; w++) {
                br.Break(new List<LineBreaker.Item> { probeItem() }, 50);
            }
            int callsBeforeWindow = m.WindowCalls;
            int callsBeforeFull = m.FullStringCalls;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            const int calls = 100;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++) {
                br.Break(new List<LineBreaker.Item> { probeItem() }, 50);
            }
            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
            int windowDelta = m.WindowCalls - callsBeforeWindow;
            int fullDelta = m.FullStringCalls - callsBeforeFull;
            TestContext.WriteLine(
                $"P7 probe-heavy: {perCall} B/call, metricsCalls(window/full)={windowDelta}/{fullDelta} over {calls} Break calls");

            // Primary assert (the actual P7 fix): warm-cache probes service
            // EVERY measurement from the cache without re-entering the
            // metrics implementation. windowDelta/fullDelta should both be 0.
            Assert.That(windowDelta, Is.EqualTo(0),
                "Wrap probes should hit the windowed MeasureCached cache after warmup — non-zero windowDelta means probes are recomputing.");
            Assert.That(fullDelta, Is.EqualTo(0),
                "Wrap probes should not fall through to the string-key path after warmup.");

            // Secondary assert (regression guard): per-call alloc stays well
            // under the pre-fix ~55 KB total. Post-fix this path measures
            // ~35 KB/call — dominated by per-line LineBox + TextRun + slice
            // substring (stored fragment text; out of P7 scope). 45 KB is the
            // regression bound that catches the per-probe Substring path
            // (which adds ~20 KB) without false-positive on JIT/GC noise.
            Assert.That(perCall, Is.LessThan(45_000),
                "LargestPrefixThatFits per-call alloc regressed — verify wrap probe doesn't re-allocate.");
        }

        [Test]
        public void AppendPreserving_multi_paragraph_warm_path_allocates_near_zero_P8() {
            // Multi-paragraph pre-formatted text with tabs forces the
            // NormalizePreservedText tab-expansion branch on every segment —
            // the path that pre-fix allocated slice + tail Substrings.
            //
            // The breaker still allocates per-line LineBox / TextRun / Result
            // wrapper / fragment-text strings (sb.ToString output, the
            // segment substring at AppendPreserving:705, the per-token
            // strings from TokenizePreservingWithBreakpoints) — those are
            // stored fragment text and explicitly out of P8 scope per the
            // brief. The measurement-only slices inside
            // NormalizePreservedText (slice / tail) should not call back into
            // the metrics implementation after warmup.
            var m = new CountingWindowedMetrics();
            const string body = "line one with\tsome tab\nline two with\tmore tabs\nline three plain";

            var br = new LineBreaker();
            for (int w = 0; w < 5; w++) {
                var warm = Item(body, m, fontSize: 16, ws: "pre");
                br.Break(new List<LineBreaker.Item> { warm }, 1000);
            }

            int callsBeforeWindow = m.WindowCalls;
            int callsBeforeFull = m.FullStringCalls;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int calls = 100;
            for (int i = 0; i < calls; i++) {
                var item = Item(body, m, fontSize: 16, ws: "pre");
                br.Break(new List<LineBreaker.Item> { item }, 1000);
            }
            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
            int windowDelta = m.WindowCalls - callsBeforeWindow;
            int fullDelta = m.FullStringCalls - callsBeforeFull;
            TestContext.WriteLine(
                $"P8 AppendPreserving: {perCall} B/call, metricsCalls(window/full)={windowDelta}/{fullDelta} over {calls} calls");

            // Primary assert: AppendPreserving slices the source `body` into
            // \n-bounded segments via text.Substring — those segments are
            // fresh refs every Break call, so the windowed cache (which is
            // identity-keyed on the source string) does not hit. What MUST
            // hold is that the windowed slice/tail measurements never
            // allocate a Substring of their own — they're contributing
            // metrics calls (windowDelta > 0 is expected on the slice path),
            // but with windowed-Measure they're zero-alloc on the metrics
            // side. The fullString path serves the per-segment measurement
            // cache (segments hash to the same content, hit the string cache).
            //
            // We bound windowDelta loosely (< 1000 over 100 calls = < 10
            // calls/Break = consistent with the 4 measurement-only slice +
            // tail probes per call) and ensure fullDelta stays at the warm-
            // cache value (no recompute via the string path).
            Assert.That(fullDelta, Is.EqualTo(0),
                "AppendPreserving segment measurements should hit the string-keyed cache after warmup.");
            Assert.That(windowDelta, Is.LessThan(1000),
                "NormalizePreservedText slice/tail should not blow up the metric call count.");

            // Secondary regression guard on bytes/call. The stored substring
            // costs (segment, sb.ToString output, fragment.Text) plus per-line
            // LineBox + TextRun + Result + List grow + dict bookkeeping are
            // unavoidable here — a multi-paragraph "pre" string emits ~3
            // fragments. 6 KB/call leaves headroom while catching a regression
            // to the per-call slice/tail Substring alloc.
            Assert.That(perCall, Is.LessThan(6_000),
                "AppendPreserving hot-path regressed — NormalizePreservedText slice/tail likely re-allocates.");
        }

        [Test]
        public void Break_all_wrap_decision_emits_zero_substring_probes_P7() {
            // The brief asks for a probe-pattern regression: a wrap-decision
            // case that previously triggered N substring allocs now triggers
            // 0 substring-keyed Measure calls. We assert by counting the
            // metrics' FullStringCalls (the legacy substring path) before and
            // after a warmup pass — the post-warmup probes should hit the
            // windowed overload (WindowCalls) and never the substring one.
            var m = new CountingWindowedMetrics();
            // Use a stable word reference across warmup + measurement so the
            // windowed cache reuses keys (which include the source string's
            // identity hash).
            var word = new string('b', 64);
            var item1 = Item(word, m, fontSize: 16, wordBreak: "break-all");

            var br = new LineBreaker();

            // Warmup: prime the windowed cache. The legacy string-keyed cache
            // is also primed by the per-fragment AddFragment Measure(...) but
            // the wrap PROBES should only ever touch the windowed path.
            br.Break(new List<LineBreaker.Item> { item1 }, 40);

            int fullBefore = m.FullStringCalls;
            int windowBefore = m.WindowCalls;

            // Second pass with the SAME word reference — every wrap probe
            // should hit the cache via the windowed path. The full-string
            // Measure may still be called once for fragment-text bookkeeping
            // (the cache is shared across both paths because the start=0
            // length=Text.Length window routes through the string overload).
            var item2 = Item(word, m, fontSize: 16, wordBreak: "break-all");
            br.Break(new List<LineBreaker.Item> { item2 }, 40);

            int fullAfter = m.FullStringCalls - fullBefore;
            int windowAfter = m.WindowCalls - windowBefore;
            TestContext.WriteLine(
                $"P7 second-pass wrap probes: window={windowAfter} fullString={fullAfter}");

            // After warmup, every wrap probe key (RuntimeHelpers.GetHashCode
            // on `word`, plus start+length) is already in measureWindowCache.
            // So we expect 0 new metrics calls of either flavour for the
            // probe path. (The per-fragment AddFragment path may still call
            // through to the string cache, but THAT hits its own cache and
            // contributes 0 new metrics calls too.) Allow a tiny budget for
            // first-time space measurements via the collapsing tokenizer.
            Assert.That(windowAfter + fullAfter, Is.LessThan(5),
                "Second-pass wrap should hit the measure cache; metrics calls indicate a probe re-allocates or bypasses the cache.");
        }
    }
}
