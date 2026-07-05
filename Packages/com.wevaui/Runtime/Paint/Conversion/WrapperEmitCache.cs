using Weva.Paint.Filters;

namespace Weva.Paint.Conversion {
    // Per-Box cache of the inputs/outputs of BoxToPaintConverter.EmitWrappersFresh.
    // Companion to PaintBoxCache: where PaintBoxCache holds the box's DECORATION
    // commands keyed on DecorationStyleVersion (stable under transform-only ticks),
    // WrapperEmitCache holds the resolved wrapper STATE (filter / transform / mask /
    // opacity / mix-blend-mode) keyed on the full style + layout fingerprint.
    //
    // Why this exists:
    //   EmitWrappersFresh was running unconditionally per decoratable box per frame,
    //   calling FilterResolver.Resolve + TransformResolver.ResolveTransform +
    //   MaskResolver.Resolve + OpacityResolver.ResolveOpacity +
    //   MixBlendModeResolver.Resolve every time. Even with the per-resolver static
    //   caches (FilterResolver hashes raw text → FilterChain), each call still
    //   string-fetches the property, hashes, and probes a Dictionary — net CPU on
    //   a deep tree. P9 from the code audit.
    //
    // Hit condition (all must match the captured frame):
    //   - style.Version: any property change bumps this, including wrapper props
    //     (transform / opacity / filter / mask / mix-blend-mode / transform-origin)
    //     and non-wrapper props that might shift e.g. currentColor → drop-shadow
    //     color through FilterResolver.
    //   - boxVersion (mirror of Box.Version): layout writes bump this, which
    //     covers Width/Height changes that re-bake the transform-origin pivot
    //     and re-bound the filter scope.
    //   - absX, absY: parent re-positioning shifts these without bumping
    //     Box.Version (PaintBoxCache's whole point is to skip on ancestor moves).
    //     The wrapper push commands bake absX/absY directly into the pivot
    //     translation and the filter-scope rect, so we MUST re-emit on shift.
    //   - contextVersion: viewport resize / theme swap bumps this; resolvers
    //     consume LengthContext (vw/vh) so it's part of the input set.
    //
    // Miss path: EmitWrappersFresh runs the 5 resolvers, emits the push commands,
    // and stamps the cache with the new fingerprint + resolved outputs. Hit path:
    // pool-rents the push commands using the cached outputs, skipping every
    // resolver call.
    //
    // Wrappers are NOT actual commands (those are pool-rented per-frame and live
    // in the output PaintList). This cache only stores the resolved INPUTS to
    // RentPushXxx so the next frame's Rent calls can short-circuit the resolution.
    public sealed class WrapperEmitCache {
        public long StyleVersion;
        public long BoxVersion;
        public double AbsX;
        public double AbsY;
        public double Width;
        public double Height;
        public long ContextVersion;
        public bool Valid;

        // Resolved outputs replayed on a cache hit. Match the locals computed
        // inside EmitWrappersFresh one-for-one.
        public FilterChain Filters;
        public Transform2D Xf;
        public bool HasFilter;
        public bool HasTransform;
        public bool FoldFilterIntoPaintColors;
        public double FoldedBrightness;
        public Rect FilterBounds;
        public Rect BorderBounds;
        public MaskDefinition Mask;
        public float Opacity;
        public MixBlendMode Blend;

        public bool Matches(long styleVersion, long boxVersion, double absX, double absY,
                            double width, double height, long contextVersion) {
            return Valid
                   && StyleVersion == styleVersion
                   && BoxVersion == boxVersion
                   && AbsX == absX
                   && AbsY == absY
                   && Width == width
                   && Height == height
                   && ContextVersion == contextVersion;
        }

        public void Stamp(long styleVersion, long boxVersion, double absX, double absY,
                          double width, double height, long contextVersion) {
            StyleVersion = styleVersion;
            BoxVersion = boxVersion;
            AbsX = absX;
            AbsY = absY;
            Width = width;
            Height = height;
            ContextVersion = contextVersion;
            Valid = true;
        }

        public void Invalidate() {
            Valid = false;
            Filters = null;
            Mask = null;
        }
    }
}
