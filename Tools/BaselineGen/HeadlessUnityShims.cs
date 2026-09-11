// Minimal stand-ins for the Unity APIs the headless build still reaches.
//
// BaselineGen compiles the engine's runtime outside Unity so it can act as the
// oracle for the C++ port. Almost all Unity-bound code is either excluded by
// the csproj or already behind `#if UNITY_*`, which a plain `dotnet build`
// compiles out. What remains is profiler instrumentation, which is scattered
// through the layout path and cannot be excluded without excluding layout.
//
// These are deliberately no-ops rather than reimplementations. Anything here
// that had behaviour would be behaviour the oracle has and Unity does not,
// which is the one thing a reference implementation must never do.

namespace UnityEngine.Profiling {
    internal static class Profiler {
        public static void BeginSample(string name) { }
        public static void EndSample() { }
    }
}
