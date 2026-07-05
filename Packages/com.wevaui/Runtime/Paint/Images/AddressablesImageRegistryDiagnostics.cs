using System;
using System.Collections.Generic;
using Weva.Diagnostics;

namespace Weva.Paint.Images {
    // Helper that centralises the async-failure log convention used by
    // AddressablesImageRegistry. Lives outside the WEVA_ADDRESSABLES gate
    // so it can be unit-tested without the Addressables package installed
    // (the registry itself is compiled out without the gate).
    //
    // Convention matches HtmlReloadCoordinator (HotReload/HtmlReloadCoordinator.cs:81):
    // a structured Log(...) call carrying the source + a short cause, routed
    // through UICssDiagnostics.Warn so the existing per-(source, detail)
    // dedupe in the diagnostics channel keeps the console quiet on repeated
    // failures of the same key.
    //
    // We also keep a local per-handle HashSet so that the diagnostics
    // dedupe (which keys on "source detail" string) cannot mask a fresh
    // failure for a *new* handle after a previous handle's message has
    // already been emitted. The HashSet guarantees: at most one log per
    // (handle) for the lifetime of the process, regardless of how many
    // times a paint pass calls TryResolve for the same broken key.
    internal static class AddressablesImageRegistryDiagnostics {
        const string Source = "addressables";

        static readonly object gate = new object();
        static readonly HashSet<string> loggedHandles = new HashSet<string>();

        // Logs a single warning for the given (handle, exception) pair.
        // Subsequent calls for the same handle are silently dropped so a
        // paint pass that probes a broken key thousands of times per
        // second does not flood the console. Exceptions are unwrapped
        // through GetBaseException so the message reflects the underlying
        // Addressables / IO failure instead of the AggregateException
        // wrapper produced by ContinueWith.
        public static void LogAsyncFailure(string handle, Exception ex) {
            if (string.IsNullOrEmpty(handle)) handle = "?";
            lock (gate) {
                if (!loggedHandles.Add(handle)) return;
            }
            string detail;
            if (ex == null) {
                detail = handle + ": failed";
            } else {
                var baseEx = ex is AggregateException agg ? agg.GetBaseException() : ex;
                detail = handle + ": " + baseEx.GetType().Name + ": " + baseEx.Message;
            }
            UICssDiagnostics.Warn(Source, detail);
        }

        // Test hook — wipes the per-handle dedupe set so a re-running test
        // can observe a warning that was already emitted by an earlier
        // assertion in the same session. Not part of the production
        // contract. Callers should also UICssDiagnostics.ResetForTests().
        internal static void ResetForTests() {
            lock (gate) loggedHandles.Clear();
        }

        internal static int LoggedHandleCountForTests() {
            lock (gate) return loggedHandles.Count;
        }
    }
}
