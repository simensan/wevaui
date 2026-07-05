#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define WEVA_PROFILE
#endif

#if WEVA_PROFILE
using Unity.Profiling;
#endif

namespace Weva.Profiling {
    // Centralised ProfilerMarker definitions for every pipeline stage. Markers are
    // declared in editor + development builds only; release-build call sites compile
    // to a no-op via PerfMarkerScope.Auto when WEVA_PROFILE is undefined.
    //
    // Naming: "Weva.<Stage>.<Phase>" so the Profiler window groups them under the
    // Scripting category by their dotted prefix. Keep names stable — external dashboards
    // pivot off them.
    //
    // The "Marker" struct here is a thin pointer to a real ProfilerMarker in editor/
    // development builds, and an empty struct in release. Call sites use
    // `using (PerfMarkerScope.Auto(UIProfilerMarkers.X)) { ... }`; in release the auto
    // scope returns a default-initialised disposable that does nothing.
    public readonly struct UIProfilerMarker {
#if WEVA_PROFILE
        public readonly ProfilerMarker Inner;
        public UIProfilerMarker(string name) {
            Inner = new ProfilerMarker(name);
        }
#else
        public UIProfilerMarker(string name) { }
#endif
    }

    public static class UIProfilerMarkers {
        public static readonly UIProfilerMarker CascadeComputeAll = new("Weva.Cascade.ComputeAll");
        public static readonly UIProfilerMarker CascadeIncrementalApply = new("Weva.Cascade.IncrementalApply");

        public static readonly UIProfilerMarker LayoutBuild = new("Weva.Layout.Build");
        public static readonly UIProfilerMarker LayoutBlock = new("Weva.Layout.Block");
        public static readonly UIProfilerMarker LayoutInline = new("Weva.Layout.Inline");
        public static readonly UIProfilerMarker LayoutFlex = new("Weva.Layout.Flex");
        public static readonly UIProfilerMarker LayoutGrid = new("Weva.Layout.Grid");
        public static readonly UIProfilerMarker LayoutPositioning = new("Weva.Layout.Positioning");

        public static readonly UIProfilerMarker PaintConvert = new("Weva.Paint.Convert");
        // Phase-level markers inside paint Convert. Useful for the typical
        // "how much time / alloc lives in the cache-miss decoration emit vs
        // the cache-hit replay path" question without enabling deep profile.
        public static readonly UIProfilerMarker PaintEmitDecorations = new("Weva.Paint.EmitDecorations");
        public static readonly UIProfilerMarker PaintReplayTranslated = new("Weva.Paint.ReplayTranslated");
        public static readonly UIProfilerMarker PaintVisitBox = new("Weva.Paint.VisitBox");

        public static readonly UIProfilerMarker SnapshotBuild = new("Weva.Snapshot.Build");
        public static readonly UIProfilerMarker SnapshotSelectorMatch = new("Weva.Snapshot.SelectorMatch");

        public static readonly UIProfilerMarker EventDispatch = new("Weva.Event.Dispatch");
        public static readonly UIProfilerMarker HitTest = new("Weva.Event.HitTest");
        public static readonly UIProfilerMarker ScrollTick = new("Weva.Scroll.Tick");
    }
}
