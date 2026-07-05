using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityEngine {
    internal static class Application {
        // Unity's Application.dataPath returns the absolute path to the project's
        // Assets folder. In the headless runner the project root is two directory
        // levels above Tools/TestVerifyAll (the typical cwd for `dotnet run`), so
        // we climb up from the current working directory to find the repo root and
        // then append "Assets". This makes Path.GetDirectoryName(Application.dataPath)
        // equal to the repo root — matching what fixture-loading tests expect.
        public static string dataPath {
            get {
                // Primary: climb two levels from cwd (Tools/TestVerifyAll → Tools → repo root).
                string cwd = Environment.CurrentDirectory;
                string candidate = Path.GetFullPath(Path.Combine(cwd, "..", "..", "Assets"));
                if (Directory.Exists(candidate)) return candidate;
                // Fallback: keep the old behaviour (cwd/Assets) so nothing else breaks.
                return Path.Combine(cwd, "Assets");
            }
        }
    }

    // Stub-out the SubsystemRegistration bootstrap so UIMainThreadGuard
    // compiles in the headless test runner. Real Unity invokes the marked
    // method via reflection; the headless runner just leaves it inert
    // (UIMainThreadGuard's s_MainThreadId stays -1 → assertion no-ops).
    public enum RuntimeInitializeLoadType {
        SubsystemRegistration,
        AfterAssembliesLoaded,
        BeforeSplashScreen,
        BeforeSceneLoad,
        AfterSceneLoad
    }

    // WevaDocument's inspector-facing fields use [SerializeField] /
    // [HideInInspector] / [Tooltip]; headless compilation only needs the
    // attribute types to exist.
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class SerializeFieldAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class HideInInspectorAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class TooltipAttribute : Attribute {
        public readonly string tooltip;
        public TooltipAttribute(string tooltip) { this.tooltip = tooltip; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute {
        public RuntimeInitializeLoadType loadType { get; }
        public RuntimeInitializeOnLoadMethodAttribute() {
            loadType = RuntimeInitializeLoadType.AfterSceneLoad;
        }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) {
            this.loadType = loadType;
        }
    }

    namespace TestTools {
        // Minimal LogAssert stub so tests that assert on Unity console messages
        // compile under the headless runner. Headless runs don't surface
        // engine logs, so these become no-ops — tests that depend on this
        // path must be skipped manually if exercised here.
        public static class LogAssert {
            public static bool ignoreFailingMessages { get; set; }
            public static void Expect(LogType type, string message) { }
            public static void Expect(LogType type, Regex message) { }
            public static void NoUnexpectedReceived() { }
        }
    }

    public enum LogType {
        Error,
        Assert,
        Warning,
        Log,
        Exception
    }

    // Minimal Debug stub — under the headless runner we route messages to
    // Console so failures still surface.
    internal static class Debug {
        public static void Log(object message) => Console.WriteLine(message);
        public static void LogWarning(object message) => Console.WriteLine("WARN: " + message);
        public static void LogError(object message) => Console.WriteLine("ERROR: " + message);
        public static void LogAssertion(object message) => Console.WriteLine("ASSERT: " + message);
        public static void LogException(Exception ex) => Console.WriteLine("EXC: " + ex);
    }
}

namespace UnityEngine.Profiling {
    // Headless no-op stub for the runtime's Profiler.BeginSample/EndSample
    // instrumentation (UIDocumentLifecycle phase markers). The real Profiler
    // lives in UnityEngine.CoreModule; the runner only needs the calls to
    // compile and do nothing.
    internal static class Profiler {
        public static void BeginSample(string name) { }
        public static void EndSample() { }
    }
}
