#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define WEVA_PROFILE
#endif

using System;
#if WEVA_PROFILE
using Unity.Profiling;
#endif

namespace Weva.Profiling {
    // `using` helper to wrap a UIProfilerMarker around a pipeline phase. In release
    // builds the entire scope compiles to a no-op struct construction.
    //
    // Usage:
    //   using (PerfMarkerScope.Auto(UIProfilerMarkers.CascadeComputeAll)) { ... }
    //
    // The shape mirrors ProfilerMarker.Auto() so the call site is one swappable line.
    // Struct (not class) so the using statement disposes without IDisposable boxing.
    public readonly struct PerfMarkerScope : IDisposable {
#if WEVA_PROFILE
        readonly ProfilerMarker.AutoScope inner;

        PerfMarkerScope(ProfilerMarker.AutoScope inner) {
            this.inner = inner;
        }

        public static PerfMarkerScope Auto(UIProfilerMarker marker) {
            return new PerfMarkerScope(marker.Inner.Auto());
        }

        public void Dispose() {
            inner.Dispose();
        }
#else
        public static PerfMarkerScope Auto(UIProfilerMarker marker) {
            return default;
        }

        public void Dispose() { }
#endif
    }
}
