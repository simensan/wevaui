using System.Collections.Generic;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Text.Sdf {
    // CharacterFallback resolves "which face actually has this codepoint" for a
    // text run. The primary face is consulted first; if it cannot produce an
    // advance for the codepoint we walk a configured fallback chain in order.
    //
    // v1 chain default: [primary, "Arial", "Liberation Sans", DefaultFamily].
    // We deliberately don't probe glyph rasters during chain selection — that
    // would force atlas warm-up for every miss. Instead we rely on the backend's
    // TryGetGlyphAdvance (which loads only the glyph index, not the bitmap) to
    // signal whether the face has a glyph for the codepoint.
    //
    // Headless tests inject IGlyphProbe to avoid pulling the full TextCore stack
    // through to the test harness.
    public sealed class CharacterFallback {
        public interface IGlyphProbe {
            bool HasGlyph(FaceInfo face, uint codepoint);
        }

        readonly IGlyphProbe probe;
        readonly List<string> chain = new();

        public CharacterFallback(IGlyphProbe probe) {
            this.probe = probe;
            // Default chain; callers can clear and rebuild via WithChain(...).
            chain.Add("Arial");
            chain.Add("Liberation Sans");
            chain.Add("sans-serif");
            // Emoji fallback. Author CSS often embeds emoji codepoints
            // (🛡️ 🐲 🪄) directly as image-replacement glyphs — without a
            // dedicated emoji face in the chain they'd render as .notdef
            // tofu. We probe each platform's standard system emoji family in
            // turn; FontResolver.Resolve returns invalid for those that
            // aren't installed and the loop skips them silently.
            chain.Add("Segoe UI Emoji");      // Windows
            chain.Add("Apple Color Emoji");   // macOS / iOS
            chain.Add("Noto Color Emoji");    // Linux / Android
            chain.Add("Twemoji Mozilla");     // Firefox-shipped fallback
        }

        public CharacterFallback WithChain(IList<string> families) {
            chain.Clear();
            if (families != null) {
                foreach (var f in families) {
                    if (!string.IsNullOrEmpty(f)) chain.Add(f);
                }
            }
            return this;
        }

        public void AddToChain(string family) {
            if (string.IsNullOrEmpty(family)) return;
            if (chain.Contains(family)) return;
            chain.Insert(0, family);
        }

        public IReadOnlyList<string> Chain => chain;

        // Returns the FaceInfo that successfully resolves a glyph for the codepoint.
        // The primary face is tested first. If no face in the chain has the glyph,
        // returns the primary face anyway (caller renders .notdef from primary).
        public FaceInfo Resolve(FaceInfo primary, uint codepoint, int weight, FontStyle style) {
            if (probe == null) return primary;
            if (probe.HasGlyph(primary, codepoint)) return primary;
            for (int i = 0; i < chain.Count; i++) {
                var handle = new FontHandle(chain[i], 0, weight, style);
                var candidate = FontResolver.Resolve(handle);
                if (!candidate.IsValid) continue;
                if (candidate.Equals(primary)) continue;
                if (probe.HasGlyph(candidate, codepoint)) return candidate;
            }
            return primary;
        }
    }
}
