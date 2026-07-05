using System.Threading;

namespace Weva.Layout.Incremental {
    internal static class BoxVersion {
        static long counter;

        public static long Next() {
            return Interlocked.Increment(ref counter);
        }
    }
}
