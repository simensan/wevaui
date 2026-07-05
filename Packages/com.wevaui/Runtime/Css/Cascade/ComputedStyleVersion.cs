using System.Threading;

namespace Weva.Css.Cascade {
    internal static class ComputedStyleVersion {
        static long counter;

        public static long Next() {
            return Interlocked.Increment(ref counter);
        }
    }
}
