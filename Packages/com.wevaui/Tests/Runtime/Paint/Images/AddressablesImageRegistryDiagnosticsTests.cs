using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Diagnostics;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Images {
    // AS1 / IF3 — AddressablesImageRegistry used to silently swallow async
    // failures: `_ = LoadOneAsync(handle)` discarded synchronous throws and
    // two `catch { }` blocks inside the load body dropped Addressables
    // exceptions on the floor. The fix routes every failure through the
    // AddressablesImageRegistryDiagnostics.LogAsyncFailure helper, which
    // mirrors the HtmlReloadCoordinator convention:
    //   - catch (Exception ex) — broad, because Addressables surfaces a wide
    //     variety of late-bound exception types through reflection;
    //   - structured Log via UICssDiagnostics.Warn so authors see what
    //     failed;
    //   - per-handle dedupe so a paint pass that retries a broken key
    //     thousands of times per second cannot flood the console.
    //
    // The registry itself is gated on WEVA_ADDRESSABLES, so we pin the
    // contract at the helper boundary (the test seam per the brief). The
    // helper has no Addressables dependency and is reachable from tests
    // even when the package isn't installed.
    public class AddressablesImageRegistryDiagnosticsTests {
        [SetUp]
        public void Reset() {
            UICssDiagnostics.ResetForTests();
            AddressablesImageRegistryDiagnostics.ResetForTests();
        }

        [Test]
        public void LogAsyncFailure_routes_through_UICssDiagnostics_warn() {
            // Scenario 1 (brief): a failing LoadOneAsync (mocked here as a
            // direct invocation of the test seam with a representative
            // exception) emits exactly one warning through the centralised
            // diagnostics channel. The exception type name and message both
            // appear in the detail so an author can tell what failed.
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Weva/CSS\] addressables: ui/heart: InvalidOperationException: addressables-load-broke"));

            AddressablesImageRegistryDiagnostics.LogAsyncFailure(
                "ui/heart",
                new InvalidOperationException("addressables-load-broke"));

            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "addressables",
                    "ui/heart: InvalidOperationException: addressables-load-broke"),
                Is.True);
        }

        [Test]
        public void LogAsyncFailure_unwraps_AggregateException_to_base_cause() {
            // Scenario 2 (brief): the StartLoad ContinueWith hands us a
            // Task.Exception which is an AggregateException wrapping the
            // actual cause (the same shape we'd see from a synchronous
            // throw promoted into a faulted task). The helper unwraps so
            // the log reflects the underlying failure, not the wrapper.
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Weva/CSS\] addressables: ui/missing: NullReferenceException: late-bound-null"));

            var inner = new NullReferenceException("late-bound-null");
            var agg = new AggregateException("wrapper", inner);
            AddressablesImageRegistryDiagnostics.LogAsyncFailure("ui/missing", agg);

            // Detail key reflects the unwrapped base cause, not the
            // AggregateException wrapper.
            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "addressables",
                    "ui/missing: NullReferenceException: late-bound-null"),
                Is.True);
            Assert.That(
                AddressablesImageRegistryDiagnostics.LoggedHandleCountForTests(),
                Is.EqualTo(1));
        }

        [Test]
        public void Same_handle_logged_50_times_emits_one_warning() {
            // Scenario 3 (brief): 50 failed loads of the same handle emit at
            // most one log. Real-world trigger: a paint pass that calls
            // TryResolve on a broken `<img src>` every frame — without the
            // dedupe the console would fill in seconds.
            LogAssert.Expect(LogType.Warning, new Regex(@"\[Weva/CSS\] addressables: ui/spam:"));

            for (int i = 0; i < 50; i++) {
                AddressablesImageRegistryDiagnostics.LogAsyncFailure(
                    "ui/spam",
                    new InvalidOperationException("attempt " + i));
            }

            LogAssert.NoUnexpectedReceived();
            Assert.That(
                AddressablesImageRegistryDiagnostics.LoggedHandleCountForTests(),
                Is.EqualTo(1));
        }

        [Test]
        public void Distinct_handles_each_log_once() {
            // Sanity: the per-handle dedupe doesn't conflate distinct keys.
            // Three different handles each produce their own log.
            LogAssert.Expect(LogType.Warning, new Regex(@"addressables: a:"));
            LogAssert.Expect(LogType.Warning, new Regex(@"addressables: b:"));
            LogAssert.Expect(LogType.Warning, new Regex(@"addressables: c:"));

            AddressablesImageRegistryDiagnostics.LogAsyncFailure("a", new Exception("x"));
            AddressablesImageRegistryDiagnostics.LogAsyncFailure("b", new Exception("y"));
            AddressablesImageRegistryDiagnostics.LogAsyncFailure("c", new Exception("z"));

            Assert.That(
                AddressablesImageRegistryDiagnostics.LoggedHandleCountForTests(),
                Is.EqualTo(3));
        }

        [Test]
        public void Null_exception_still_logs_a_safe_message() {
            // ContinueWith hands us t.Exception which is non-null on a
            // faulted task, but defensive coverage: a null exception must
            // not throw and must still produce a single log per handle.
            LogAssert.Expect(LogType.Warning, new Regex(@"addressables: ui/nullex: failed"));

            AddressablesImageRegistryDiagnostics.LogAsyncFailure("ui/nullex", null);

            Assert.That(
                UICssDiagnostics.HasEmittedForTests("addressables", "ui/nullex: failed"),
                Is.True);
        }

        [Test]
        public void Empty_handle_logs_under_placeholder_key() {
            // Defensive: a null/empty handle is treated as "?". One log,
            // not a crash. Subsequent null/empty calls dedupe under the
            // same placeholder.
            LogAssert.Expect(LogType.Warning, new Regex(@"addressables: \?: "));

            AddressablesImageRegistryDiagnostics.LogAsyncFailure(null, new Exception("a"));
            AddressablesImageRegistryDiagnostics.LogAsyncFailure("", new Exception("b"));

            Assert.That(
                AddressablesImageRegistryDiagnostics.LoggedHandleCountForTests(),
                Is.EqualTo(1));
        }
    }
}
