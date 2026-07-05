using System;
using System.Collections.Generic;
using UnityEngine;
using Weva.Layout.Text;

namespace Weva.Text.Unity {
    // UnityGUIFontMetrics measures text using a Unity GUIStyle so layout numbers
    // line up with what IMGUI's GUI.Label actually paints. MonoFontMetrics gave
    // every char a fixed half-em width which IMGUI does not honor (proportional
    // sans-serif), producing visible gaps and overflow in the demo.
    //
    // Caveats (Unity quirks):
    //   - GUIStyle.CalcSize must run on the main thread and, on some Unity
    //     versions, only returns useful numbers from inside OnGUI / Repaint.
    //     If called too early it can return zero. The IMGUI fallback renderer
    //     calls Measure during paint, which is OnGUI, so this is usually fine.
    //     Outside OnGUI a Font.GetCharacterInfo path would be the next-step
    //     fallback — not implemented here, but the factory hook makes it
    //     straightforward to swap in.
    //   - GUI.skin is a no-op outside Unity Player / Editor contexts (e.g.
    //     pure NUnit). The default factory tolerates a null skin by building
    //     a fresh GUIStyle and inheriting nothing — CalcSize then returns 0,
    //     and Measure will likewise return 0. Headless tests should inject a
    //     stub factory rather than relying on GUI.skin.
    //   - Ascent / Descent are not directly exposed by GUIStyle. We
    //     approximate as 0.8 / 0.2 of LineHeight which matches the canonical
    //     ratios used elsewhere in the package; baseline-sensitive layout
    //     should treat this as best-effort until TextCore SDF metrics ship.
    public sealed class UnityGUIFontMetrics : IFontMetrics {
        readonly Func<string, double, FontStyle, GUIStyle> styleFor;
        readonly Dictionary<long, GUIStyle> cache = new Dictionary<long, GUIStyle>();

        public UnityGUIFontMetrics() : this(BuildDefaultStyle) { }

        public UnityGUIFontMetrics(Func<string, double, FontStyle, GUIStyle> factory) {
            styleFor = factory ?? BuildDefaultStyle;
        }

        public double LineHeight(double fontSize) {
            var s = StyleFor(null, fontSize, FontStyle.Normal);
            if (s == null) return fontSize * Weva.Layout.StyleResolver.DefaultLineHeightFactor;
            float lh = 0f;
            // Only trust GUIStyle.lineHeight when the style has an explicit font
            // bound. The default editor font's reported lineHeight is rounded
            // to integer pixels per fontSize, which makes the ratio non-linear
            // (16 -> 18 px, 32 -> 37 px instead of 36) and breaks tests that
            // assert proportional growth. With no explicit font, fall back to
            // the canonical 1.2em ratio so LineHeight(2x) == 2 * LineHeight(x)
            // exactly.
            if (s.font != null) {
                try {
                    lh = s.lineHeight;
                } catch {
                    // GUIStyle.lineHeight reads through to the active font; if
                    // the font is in an unloadable state it can throw on some
                    // Unity versions. Fall back to the conventional 1.2em ratio
                    // so layout still progresses.
                    lh = 0;
                }
            }
            if (lh > 0) return lh;
            return fontSize * Weva.Layout.StyleResolver.DefaultLineHeightFactor;
        }

        public double Measure(string text, double fontSize) {
            if (string.IsNullOrEmpty(text)) return 0;
            var s = StyleFor(null, fontSize, FontStyle.Normal);
            if (s == null) return 0;
            try {
                return s.CalcSize(new GUIContent(text)).x;
            } catch {
                // CalcSize throws UnityException when called outside an OnGUI
                // context on some Unity versions. Treat as "no measurement
                // available" so layout falls back to text-length-only sizing
                // upstream rather than blowing up the frame.
                return 0;
            }
        }

        // Substring-window overload. GUIStyle.CalcSize requires a real string
        // so we still materialise one when the window is a strict sub-slice.
        // For the (start == 0 && length == text.Length) full-string case we
        // hand the original ref through, which is also what the LineBreaker's
        // non-wrap path passes — zero extra alloc there. The wrap-probe path
        // (where length < text.Length) still pays one substring, but this is
        // a non-prod IMGUI fallback and the wrap binary search isn't hot.
        public double Measure(string text, int start, int length, double fontSize) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            if (start + length > text.Length) length = text.Length - start;
            string slice = (start == 0 && length == text.Length) ? text : text.Substring(start, length);
            return Measure(slice, fontSize);
        }

        public double Ascent(double fontSize) => LineHeight(fontSize) * 0.8;
        public double Descent(double fontSize) => LineHeight(fontSize) * 0.2;

        public int CachedStyleCount => cache.Count;

        public void InvalidateCaches() {
            cache.Clear();
        }

        GUIStyle StyleFor(string family, double fontSize, FontStyle style) {
            int sizePx = ClampSize(fontSize);
            long key = MakeKey(sizePx, style);
            if (cache.TryGetValue(key, out var existing)) return existing;
            var built = styleFor(family, fontSize, style);
            cache[key] = built;
            return built;
        }

        static int ClampSize(double fontSize) {
            int s = (int)System.Math.Round(fontSize);
            if (s < 1) s = 1;
            return s;
        }

        static long MakeKey(int sizePx, FontStyle style) {
            return ((long)sizePx << 8) | (long)(byte)style;
        }

        // Default factory: derive from GUI.skin.label so we inherit whatever
        // sans-serif font Unity's editor / player has installed. We copy
        // rather than mutate the shared skin's style to avoid leaking changes
        // across WevaDocument instances. Returns null when no skin is available
        // (e.g. headless NUnit) — the public API treats null as "measure to 0".
        static GUIStyle BuildDefaultStyle(string family, double fontSize, FontStyle style) {
            GUIStyle baseStyle = null;
            try {
                if (GUI.skin != null) baseStyle = GUI.skin.label;
            } catch {
                baseStyle = null;
            }
            GUIStyle s = baseStyle != null ? new GUIStyle(baseStyle) : new GUIStyle();
            s.fontSize = ClampSize(fontSize);
            s.fontStyle = style;
            s.wordWrap = false;
            s.richText = false;
            return s;
        }
    }
}
