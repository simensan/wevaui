using Weva.Dom;
using Weva.Events;

namespace Weva.Documents {
    // EventDispatcher takes its IHitTester via constructor and stores it
    // readonly. Layout produces a fresh BoxTreeHitTester on every pass, so
    // we wrap the real tester in a swappable adapter the dispatcher holds
    // for life and the lifecycle helper updates after each layout. Until
    // the first layout pass populates Inner, HitTest returns null — which
    // lets the dispatcher drop unhittable events without throwing.
    public sealed class SwappableHitTester : IHitTester {
        public IHitTester Inner { get; set; }
        public Element HitTest(double x, double y) {
            return Inner != null ? Inner.HitTest(x, y) : null;
        }
    }
}
