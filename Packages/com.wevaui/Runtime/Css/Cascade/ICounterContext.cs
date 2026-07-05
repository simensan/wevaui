namespace Weva.Css.Cascade {
    // Context object that supplies CSS counter values to
    // CascadeEngine.ResolveContentString for `counter()` and `counters()`
    // function resolution in generated content (CSS Generated Content L3 §2,
    // CSS Lists L3 §2).
    //
    // Also tracks quote depth for open-quote / close-quote / no-open-quote /
    // no-close-quote content keywords (CSS Generated Content L3 §3.1).
    //
    // Implementations are expected to be provided by the layout layer (e.g.
    // BoxBuilder) which has walked the document tree and accumulated the
    // counter-reset / counter-increment scope chain. A null context produces
    // empty strings for all counter lookups (graceful degradation).
    //
    // CSS Generated Content L3 §2.1 — counter():
    //   counter(name)       → the nearest enclosing scope value for `name`
    //   counter(name,style) → formatted per the style keyword
    //
    // CSS Generated Content L3 §2.2 — counters():
    //   counters(name, sep)        → all ancestor scope values joined by sep
    //   counters(name, sep, style) → same, with each value formatted per style
    //
    // CSS Generated Content L3 §3.1 — quote depth:
    //   open-quote    → insert level-clamped open string, increment depth
    //   close-quote   → decrement depth (≥0), insert level-clamped close string
    //   no-open-quote → increment depth only, no insertion
    //   no-close-quote → decrement depth only, no insertion
    public interface ICounterContext {
        // Sentinel returned by GetCounterValue when the counter `name` has
        // no defined scope in the current context.
        const int NotFound = int.MinValue;

        // Returns the innermost value of counter `name` in the current
        // element's scope chain, or NotFound if the counter is not defined.
        // The caller formats the integer value according to the style keyword.
        int GetCounterValue(string name);

        // Returns all values of counter `name` in the scope chain, from the
        // outermost ancestor down to the innermost scope that establishes
        // `name`, in the order they appear in the document. Returns null or
        // an empty array when the counter is not defined.
        // Used by counters() to build the concatenated ancestor chain.
        int[] GetCounterValues(string name);

        // Returns the current quote nesting depth (0-based) accumulated by
        // all open-quote / no-open-quote / close-quote / no-close-quote
        // keywords processed before the target element. Used by
        // ResolveContentString to pick the correct pair from the `quotes`
        // property value.
        int QuoteDepth { get; }

        // Called by ResolveContentString when an open-quote keyword is
        // resolved: increments the depth. The caller must make a separate call
        // to get the returned depth before calling IncrementQuoteDepth so
        // the insertion uses the CURRENT depth, and the depth only increases
        // after. Because BuildFor captures a snapshot at the target element,
        // ResolveContentString must mutate depth during content resolution.
        void IncrementQuoteDepth();

        // Called when a close-quote is resolved: decrements depth (floor 0).
        void DecrementQuoteDepth();
    }
}
