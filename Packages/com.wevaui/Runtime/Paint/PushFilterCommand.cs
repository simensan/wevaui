using System;
using Weva.Paint.Filters;

namespace Weva.Paint {
    public sealed class PushFilterCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public FilterChain Filters { get; private set; }
        // Transform applied to the element OWNING this filter scope (the box's
        // own `transform` CSS property). Per CSS Filter Effects L1, filter is
        // applied BEFORE transform — meaning we should rasterize the scope's
        // content WITHOUT this transform applied, run the filter on that
        // local-space content, and then transform the result during composite.
        // Without this split, animating a filtered element's transform
        // invalidates the per-scope blur cache every frame (the rasterization
        // moves with the transform). With this split, the rasterized + blurred
        // RT survives transform-only animations and only the cheap composite
        // step pays per-frame cost. Identity = no extra composite-time transform.
        public Transform2D ScopeBoxTransform { get; private set; }

        public PushFilterCommand() : base(PaintCommandKind.PushFilter) { }

        public PushFilterCommand(Rect bounds, FilterChain filters) : this(bounds, filters, Transform2D.Identity) { }

        public PushFilterCommand(Rect bounds, FilterChain filters, Transform2D scopeBoxTransform) : base(PaintCommandKind.PushFilter) {
            Bounds = bounds;
            Filters = filters ?? throw new ArgumentNullException(nameof(filters));
            ScopeBoxTransform = scopeBoxTransform;
        }

        public void Set(Rect bounds, FilterChain filters) {
            Set(bounds, filters, Transform2D.Identity);
        }

        public void Set(Rect bounds, FilterChain filters, Transform2D scopeBoxTransform) {
            Bounds = bounds;
            Filters = filters ?? throw new ArgumentNullException(nameof(filters));
            ScopeBoxTransform = scopeBoxTransform;
        }

        public void Reset() {
            Bounds = default;
            Filters = null;
            ScopeBoxTransform = Transform2D.Identity;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
