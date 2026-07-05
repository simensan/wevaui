using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Components;
using Weva.Diagnostics;

namespace Weva.Tests.Components {
    // EC13 — ComponentTemplateImporter.ResolvePath had a silent
    // `catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)`
    // returning null on malformed paths. The caller then emitted a generic
    // "Could not resolve template import" diagnostic that conflated "bad path
    // syntax" with "file does not exist". The fix adds a distinct
    // UICssDiagnostics.Warn so an author with a malformed src= attribute sees
    // the actual exception type. By-design behavior (null return → drop the
    // import) is preserved.
    //
    // Dedupe key shape: "EC13:" + src — one warning per offending src no
    // matter how many <template> elements share it.
    //
    // The catch is exercised through the `SimulateResolvePathFailureForTests`
    // internal seam. Driving via real Path.Combine NUL etc. is not portable
    // across .NET Core (which removed several invalid-char checks) and Mono
    // (which still throws); the seam lets the dedupe + diagnostic contract
    // be tested deterministically.
    public class ComponentTemplateImporterEC13DiagnosticTests {
        [SetUp]
        public void Reset() {
            ComponentTemplateImporter.ResetWarnings_TestOnly();
        }

        [Test]
        public void Single_failure_emits_EC13_warning_and_returns_null() {
            LogAssert.Expect(LogType.Warning,
                new Regex(@"template-import.*EC13.*malformed import path 'bad/src\.html'.*ArgumentException"));

            var ex = new ArgumentException("simulated path failure");
            var result = ComponentTemplateImporter.SimulateResolvePathFailureForTests(
                "bad/src.html", ex);

            // By-design fallback: null return drops the import downstream.
            Assert.That(result, Is.Null);
            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "template-import",
                    "EC13: malformed import path 'bad/src.html' (ArgumentException); template import will be dropped."),
                Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Same_src_50_times_logs_once() {
            // Process-static dedupe: 50 simulated failures with the same src
            // → exactly one EC13 warning emitted across the run.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"template-import.*EC13.*loop/src\.html"));

            var ex = new ArgumentException("loop");
            for (int i = 0; i < 50; i++) {
                ComponentTemplateImporter.SimulateResolvePathFailureForTests(
                    "loop/src.html", ex);
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Distinct_srcs_each_log_once() {
            // Different src strings → different dedupe keys → one warning each.
            // Confirms the dedupe key is the offending src, not a class-level
            // "warned" flag.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC13.*distinct-a\.html"));
            LogAssert.Expect(LogType.Warning, new Regex(@"EC13.*distinct-b\.html"));

            var ex = new ArgumentException("dist");
            ComponentTemplateImporter.SimulateResolvePathFailureForTests("distinct-a.html", ex);
            ComponentTemplateImporter.SimulateResolvePathFailureForTests("distinct-b.html", ex);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Exception_type_name_is_included_in_warning() {
            // The warning includes the actual exception type name so the
            // author can distinguish ArgumentException from
            // NotSupportedException etc. — the two types the production
            // catch filter accepts.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC13.*NotSupportedException"));

            var ex = new NotSupportedException("nsex");
            ComponentTemplateImporter.SimulateResolvePathFailureForTests("nse-src.html", ex);

            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "template-import",
                    "EC13: malformed import path 'nse-src.html' (NotSupportedException); template import will be dropped."),
                Is.True);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
