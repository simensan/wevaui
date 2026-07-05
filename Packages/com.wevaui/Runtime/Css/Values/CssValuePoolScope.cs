using System;

namespace Weva.Css.Values {
    // RAII scope around CssValuePool. Captures the per-thread pool's
    // high-water mark on construction; on Dispose returns every value rented
    // since back to the free stacks. Nested scopes are supported (each one
    // returns only what *it* rented).
    //
    // LIFETIME CONTRACT: any CssLength/CssNumber/CssPercentage rented via the
    // pool while a scope is active becomes invalid for downstream use after
    // Dispose. Hold values only across the current pass; consumers that need
    // to retain a value (e.g. CssAnimationRunner.OnStyleChange) must clone the
    // backing data into a fresh instance allocated outside the scope.
    public readonly struct CssValuePoolScope : IDisposable {
        readonly CssValuePool.PoolHwm hwm;
        readonly bool active;

        internal CssValuePoolScope(CssValuePool.PoolHwm hwm) {
            this.hwm = hwm;
            this.active = true;
        }

        public void Dispose() {
            if (!active) return;
            CssValuePool.EndScope(hwm);
        }
    }
}
