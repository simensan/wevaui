// ABI minor 30: engine counters and the inspector's hit test through the wrapper.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeStatsTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\"></div><div id=\"n\"></div><div id=\"h\"></div>");
            _doc.SetCss("body{margin:0}div{width:100px;height:40px;background:#123}#n{pointer-events:none}#h{visibility:hidden}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        [Test]
        public void Stats_CountTheDocumentAndTheUpdates()
        {
            weva_stats s = _doc.Stats();
            Assert.That(s.updates, Is.EqualTo(1UL));
            Assert.That(s.elements, Is.GreaterThanOrEqualTo(5U));
            Assert.That(s.boxes, Is.GreaterThanOrEqualTo(4U));
            Assert.That(s.draws, Is.GreaterThanOrEqualTo(1U));
            Assert.That(s.update_ms, Is.GreaterThanOrEqualTo(0.0));
            Assert.That(s.cascade_elements, Is.GreaterThanOrEqualTo(5UL));
            _doc.Update(0);
            Assert.That(_doc.Stats().updates, Is.EqualTo(2UL));
        }

        [Test]
        public void DevToolsHit_SeesThroughPointerEventsAndVisibility()
        {
            uint body = _doc.Query("body");
            Assert.That(_doc.ElementAt(50, 60), Is.EqualTo(body));
            Assert.That(_doc.ElementAtForDevTools(50, 60), Is.EqualTo(_doc.Query("#n")));
            Assert.That(_doc.ElementAt(50, 100), Is.EqualTo(body));
            Assert.That(_doc.ElementAtForDevTools(50, 100), Is.EqualTo(_doc.Query("#h")));
        }
    }
}
