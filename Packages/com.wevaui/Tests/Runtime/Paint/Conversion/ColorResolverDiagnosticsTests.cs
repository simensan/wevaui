using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Regression coverage for CODE_AUDIT_FINDINGS.md DD2 / DD3:
    //   ColorResolver returns LinearColor.Black on null style (DD2) and falls
    //   back to currentColor on a malformed color token (DD3). Both fallbacks
    //   are kept (production-defensive), but now emit a one-time Debug.LogWarning
    //   so the underlying bug is visible to the developer. The warning is
    //   deduped via a process-static HashSet<string>:
    //     DD2 — keyed on caller file:line
    //     DD3 — keyed on the offending raw color string
    //
    // The dedupe set persists across tests in the same test run, so each test
    // calls ResetWarnings_TestOnly() in [SetUp] to get a clean slate.
    public class ColorResolverDiagnosticsTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        [SetUp]
        public void ResetWarningDedupe() {
            ColorResolver.ResetWarnings_TestOnly();
        }

        [Test]
        public void DD2_null_style_logs_warning_and_returns_black() {
            // The warning message names DD2, mentions null style, and notes the
            // Black fallback so the developer can grep for it. Use a regex so
            // the test is resilient to minor message tweaks while pinning the
            // load-bearing facts (the DD2 tracker code and "null style").
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ColorResolver.*DD2.*null style.*Black"));

            var color = ColorResolver.ResolveCurrentColor(null);

            // Production behavior is unchanged: still returns Black.
            Assert.That(color.R, Is.EqualTo(LinearColor.Black.R).Within(1e-6));
            Assert.That(color.G, Is.EqualTo(LinearColor.Black.G).Within(1e-6));
            Assert.That(color.B, Is.EqualTo(LinearColor.Black.B).Within(1e-6));
            Assert.That(color.A, Is.EqualTo(LinearColor.Black.A).Within(1e-6));
        }

        [Test]
        public void DD2_repeat_call_from_same_site_logs_once_only() {
            // Same call-site (one line repeated in a loop) → same file:line
            // dedupe key → exactly one warning even across many invocations.
            // This is the anti-spam contract for hot paths like per-frame
            // paint resolvers, which can hit ColorResolver thousands of times
            // a frame from the same call site.
            LogAssert.Expect(LogType.Warning, new Regex(@"ColorResolver.*DD2"));
            for (int i = 0; i < 50; i++) ColorResolver.ResolveCurrentColor(null);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DD3_garbled_color_logs_warning_and_falls_back_to_currentColor() {
            // Set up a style with a known currentColor so we can verify the
            // fallback actually returns that color, not Black or Transparent.
            var s = Style();
            s.Set("color", "red");

            // The DD3 warning quotes the offending raw token verbatim so the
            // developer can grep their CSS for the typo.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ColorResolver.*DD3.*not-a-real-color.*currentColor"));

            var result = ColorResolver.Resolve("not-a-real-color", s);

            // Production behavior unchanged: malformed token → currentColor (red).
            Assert.That(result.R, Is.GreaterThan(0.5f), "expected currentColor (red) fallback");
            Assert.That(result.G, Is.LessThan(0.1f));
            Assert.That(result.B, Is.LessThan(0.1f));
        }

        [Test]
        public void DD3_same_garbled_token_logs_once_across_repeated_resolves() {
            // Two different element styles, same malformed token → one warning.
            // The dedup key is the raw string, not the call site, so a single
            // bad value in a stylesheet doesn't spam once per consuming box.
            var s1 = Style(); s1.Set("color", "red");
            var s2 = Style(); s2.Set("color", "blue");

            LogAssert.Expect(LogType.Warning, new Regex(@"DD3.*nonsense-color"));
            ColorResolver.Resolve("nonsense-color", s1);
            ColorResolver.Resolve("nonsense-color", s2);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DD3_distinct_garbled_tokens_each_log_once() {
            // Different bad tokens → different dedup keys → one warning each.
            // Confirms the dedupe is keyed on the value, not a class-level "warned" flag.
            var s = Style(); s.Set("color", "black");

            LogAssert.Expect(LogType.Warning, new Regex(@"DD3.*not-color-one"));
            LogAssert.Expect(LogType.Warning, new Regex(@"DD3.*not-color-two"));
            ColorResolver.Resolve("not-color-one", s);
            ColorResolver.Resolve("not-color-two", s);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_named_color_does_not_log_any_warning() {
            // The whole point of the fix: warnings only fire when the defensive
            // fallback is actually taken. A successful "red" resolve must be
            // silent.
            var s = Style(); s.Set("color", "black");
            var result = ColorResolver.Resolve("red", s);

            // Red resolved successfully.
            Assert.That(result.R, Is.GreaterThan(0.5f));
            Assert.That(result.G, Is.LessThan(0.1f));
            Assert.That(result.B, Is.LessThan(0.1f));

            // No warning emitted.
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_currentcolor_keyword_does_not_log_any_warning() {
            // `currentcolor` is a fast-path early-return in TryResolve — it
            // never reaches the parse branch and must not warn.
            var s = Style(); s.Set("color", "red");
            var result = ColorResolver.Resolve("currentcolor", s);
            Assert.That(result.R, Is.GreaterThan(0.5f));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Empty_raw_string_does_not_log_DD3_warning() {
            // Empty / null raw is treated as "unset" by callers (background-color
            // not specified, etc.) and is NOT a parse failure. The DD3 warning
            // path explicitly skips empty input so unspecified properties don't
            // spam the console.
            var s = Style(); s.Set("color", "red");
            ColorResolver.Resolve("", s);
            ColorResolver.Resolve(null, s);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
