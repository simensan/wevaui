using System;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    // NG1 — EventDispatcher.SetPointerCapture(null) used to silently overwrite
    // the captured target with null, which is indistinguishable from a sibling
    // controller's capture being released out from under it. The contract is
    // now "ArgumentNullException — call ReleasePointerCapture() to release".
    public class EventDispatcherNullGuardTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        EventDispatcher Build(Document doc, FakeHitTester ht = null) =>
            new EventDispatcher(doc, ht ?? new FakeHitTester(), new FakeUIClock());

        [Test]
        public void SetPointerCapture_with_null_throws_ArgumentNullException_NG1() {
            var doc = Html("<div id=\"a\"></div>");
            var d = Build(doc);
            Assert.Throws<ArgumentNullException>(() => d.SetPointerCapture(null));
        }

        [Test]
        public void SetPointerCapture_with_null_does_not_clobber_existing_capture_NG1() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var d = Build(doc);
            d.SetPointerCapture(a);
            Assert.That(d.CapturedPointerTarget, Is.SameAs(a));
            // The botched call throws; the previous capture must survive.
            Assert.Throws<ArgumentNullException>(() => d.SetPointerCapture(null));
            Assert.That(d.CapturedPointerTarget, Is.SameAs(a));
        }

        [Test]
        public void SetPointerCapture_happy_path_still_records_target_NG1() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var d = Build(doc);
            d.SetPointerCapture(a);
            Assert.That(d.CapturedPointerTarget, Is.SameAs(a));
            // Explicit release is still the documented "drop the capture" path.
            d.ReleasePointerCapture();
            Assert.That(d.CapturedPointerTarget, Is.Null);
        }
    }
}
