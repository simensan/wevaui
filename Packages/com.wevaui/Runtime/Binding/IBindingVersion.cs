namespace Weva.Binding {
    // Opt-in change signal for binding controllers.
    //
    // The binding layer is poll-based: without a change signal, BindingSet
    // re-renders every template and re-resolves every path each frame just to
    // discover nothing changed. A controller that implements this interface
    // promises to increment BindingVersion whenever any data reachable from a
    // binding expression changes (fields, properties, repeat item lists, AND
    // the items' own members). In exchange, BindingSet.Update returns
    // immediately â€” no resolution, no rendering, no allocation â€” while the
    // version and the controller reference are unchanged.
    //
    // Typical implementation:
    //
    //     public class ShopController : IBindingVersion {
    //         public int BindingVersion { get; private set; }
    //         int gold;
    //         public int Gold {
    //             get => gold;
    //             set { if (gold != value) { gold = value; BindingVersion++; } }
    //         }
    //     }
    //
    // Contract notes:
    // - Forgetting to bump after a mutation means the UI will not update
    //   until the next bump; over-bumping merely costs one normal poll.
    // - The gate also releases when the context instance itself changes and
    //   when bindings are added/removed (live DOM mutation, repeat churn),
    //   so structural changes never need a manual bump.
    // - Controllers that do not implement this interface keep the existing
    //   poll-every-frame behaviour.
    public interface IBindingVersion {
        int BindingVersion { get; }
    }
}
