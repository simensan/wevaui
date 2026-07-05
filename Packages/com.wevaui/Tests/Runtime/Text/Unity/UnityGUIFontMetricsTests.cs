using NUnit.Framework;
using UnityEngine;
using Weva.Text.Unity;

namespace Weva.Tests.Text.Unity {
    public class UnityGUIFontMetricsTests {
        // Builds a GUIStyle with a known lineHeight tied to the requested font
        // size. CalcSize is exercised in Play Mode tests; the headless suite
        // only cares about the deterministic plumbing (size mapping, line
        // height calculation, ascent/descent ratios, caching).
        static GUIStyle MakeStyle(string family, double fontSize, FontStyle style) {
            var s = new GUIStyle();
            s.fontSize = (int)System.Math.Round(fontSize);
            s.fontStyle = style;
            // Mirror the typical TrueType ratio so tests assert real shape.
            // GUIStyle.lineHeight is read-only in some Unity versions, so set
            // it via the font metrics if available, otherwise rely on the
            // metric class's fallback (1.2 * fontSize).
            return s;
        }

        // A factory that records each call and returns deterministic styles.
        sealed class RecordingFactory {
            public int Calls;
            public double LastFontSize;
            public FontStyle LastFontStyle;

            public GUIStyle Build(string family, double fontSize, FontStyle style) {
                Calls++;
                LastFontSize = fontSize;
                LastFontStyle = style;
                return MakeStyle(family, fontSize, style);
            }
        }

        [Test]
        public void LineHeight_returns_positive_for_default_size() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            Assert.That(m.LineHeight(16), Is.GreaterThan(0));
        }

        [Test]
        public void LineHeight_for_large_size_is_positive_and_finite() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            double lh = m.LineHeight(72);
            Assert.That(lh, Is.GreaterThan(0));
            Assert.That(double.IsFinite(lh), Is.True);
        }

        [Test]
        public void Measure_empty_string_returns_zero() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            Assert.That(m.Measure("", 16), Is.EqualTo(0));
            Assert.That(m.Measure(null, 16), Is.EqualTo(0));
        }

        [Test]
        public void Measure_empty_does_not_invoke_factory() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.Measure("", 16);
            m.Measure(null, 16);
            Assert.That(f.Calls, Is.EqualTo(0));
        }

        [Test]
        public void Ascent_descent_relationship_within_line_height() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            double lh = m.LineHeight(16);
            double asc = m.Ascent(16);
            double desc = m.Descent(16);
            Assert.That(asc + desc, Is.LessThanOrEqualTo(lh + 1e-6));
            Assert.That(asc, Is.GreaterThan(0));
            Assert.That(desc, Is.GreaterThan(0));
        }

        [Test]
        public void Different_font_size_changes_line_height_linearly() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            double a = m.LineHeight(16);
            double b = m.LineHeight(32);
            Assert.That(b, Is.EqualTo(a * 2).Within(1e-6));
        }

        [Test]
        public void Style_cache_reuses_per_size_entries() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.LineHeight(16);
            m.Ascent(16);
            m.Descent(16);
            int callsAfterFirst = f.Calls;
            m.LineHeight(16);
            m.Ascent(16);
            m.Descent(16);
            Assert.That(f.Calls, Is.EqualTo(callsAfterFirst));
            Assert.That(m.CachedStyleCount, Is.EqualTo(1));
        }

        [Test]
        public void Style_cache_holds_distinct_sizes_separately() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.LineHeight(16);
            m.LineHeight(20);
            m.LineHeight(24);
            Assert.That(m.CachedStyleCount, Is.EqualTo(3));
        }

        [Test]
        public void InvalidateCaches_clears_cache() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.LineHeight(16);
            m.LineHeight(20);
            Assert.That(m.CachedStyleCount, Is.EqualTo(2));
            m.InvalidateCaches();
            Assert.That(m.CachedStyleCount, Is.EqualTo(0));
        }

        [Test]
        public void Null_factory_uses_default_without_throwing() {
            // Default factory pulls GUI.skin.label which is null in headless
            // NUnit; the metric class must tolerate that and return zero /
            // 1.2*em without throwing so the bootstrap fallback can engage.
            var m = new UnityGUIFontMetrics(null);
            Assert.DoesNotThrow(() => m.LineHeight(16));
            Assert.DoesNotThrow(() => m.Measure("hello", 16));
            Assert.DoesNotThrow(() => m.Ascent(16));
            Assert.DoesNotThrow(() => m.Descent(16));
        }

        [Test]
        public void Factory_receives_normal_font_style_for_metric_queries() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.LineHeight(16);
            Assert.That(f.LastFontStyle, Is.EqualTo(FontStyle.Normal));
        }

        [Test]
        public void Factory_receives_requested_font_size() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.LineHeight(22);
            Assert.That(f.LastFontSize, Is.EqualTo(22).Within(1e-9));
        }

        [Test]
        public void Sub_pixel_sizes_round_to_same_cache_slot() {
            var f = new RecordingFactory();
            var m = new UnityGUIFontMetrics(f.Build);
            m.LineHeight(16.0);
            m.LineHeight(16.2);
            m.LineHeight(15.8);
            // All round to 16 px and share a cache entry; factory called once.
            Assert.That(m.CachedStyleCount, Is.EqualTo(1));
            Assert.That(f.Calls, Is.EqualTo(1));
        }
    }
}
