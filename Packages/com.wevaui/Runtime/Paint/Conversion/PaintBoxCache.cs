using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Layout.Boxes;

namespace Weva.Paint.Conversion {
    // Per-Box cache of the paint commands that the converter emits for that box's
    // OWN decoration sequence. Descendants are NOT inlined — each child carries its
    // own PaintBoxCache. This keeps the cache stable across parent re-positions:
    // a box that itself didn't change but whose ancestor moved still hits, and the
    // converter just translates the cached commands by the new absolute origin.
    //
    // Box-local-coords contract:
    // - Every command stored in `PreChildren`/`PostChildren` has its Rect bounds
    //   expressed RELATIVE to the box's own (X, Y) at cache time. On replay the
    //   converter rents a translated copy from PaintCommandPool with bounds shifted
    //   by the current absolute origin of the box. This is what allows a parent's
    //   layout move to skip re-emitting any descendant's cache.
    // - Pop singletons (PopClipCommand etc.) are stateless and stored as-is; they
    //   carry no bounds and need no translation.
    //
    // Wrapper/decoration split (PLAN §12.1): the cache holds the box's
    // DECORATIONS (shadow / background / image / border / outline / inset-
    // shadow / overflow-clip) plus the matching overflow-clip pop. The
    // FILTER / TRANSFORM / OPACITY push+pop wrappers are NEVER cached —
    // VisitBox resolves and emits them fresh every frame using the live
    // ComputedStyle. This means a transform-only or opacity-only animation
    // (which bumps Version per Tick but leaves DecorationVersion stable)
    // skips the EmitDecorations rebuild entirely: it's a cache HIT, and
    // only the 3 cheap wrapper pool-rents pay per-tile per-frame cost.
    //
    // Lifecycle:
    // - Created and populated by BoxToPaintConverter on a cache miss for a Box.
    // - Validated against (LayoutVersion, DecorationStyleVersion, ContextVersion)
    //   every Convert; mismatches trigger a decoration rebuild.
    // - Cleared by Box.ResetForPool when the layout engine recycles the Box.
    public sealed class PaintBoxCache {
        public long LayoutVersion;
        // Decoration-only style version sourced from ComputedStyle.Decoration-
        // Version. Bumps on every property change EXCEPT wrapper properties
        // (transform / opacity / filter). A transform-only animation tick
        // leaves this stable so the cached decoration commands stay valid.
        public long DecorationStyleVersion;
        public long ContextVersion;

        // Back-compat alias: tests and external readers that previously
        // compared against the combined style version now read the decoration
        // version. Wrapper-only changes deliberately don't bump this.
        public long StyleVersion => DecorationStyleVersion;

        public readonly List<PaintCommand> PreChildren = new();
        public readonly List<PaintCommand> PostChildren = new();

        public bool IsValid(Box box, ComputedStyle style, long contextVersion) {
            if (box == null) return false;
            if (ContextVersion != contextVersion) return false;
            if (LayoutVersion != box.Version) return false;
            long dv = style != null ? style.DecorationVersion : 0;
            if (DecorationStyleVersion != dv) return false;
            return true;
        }

        public void Reset(long layoutVersion, long decorationStyleVersion, long contextVersion) {
            LayoutVersion = layoutVersion;
            DecorationStyleVersion = decorationStyleVersion;
            ContextVersion = contextVersion;
            PreChildren.Clear();
            PostChildren.Clear();
        }
    }
}
