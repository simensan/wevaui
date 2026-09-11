// ABI minor 34: the parse errors a lenient load recovered from, with positions.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeHtmlDiagnosticsTests
    {
        [Test]
        public void HtmlDiagnostics_NameTheRecoveriesWithPositions()
        {
            using (var doc = new NativeDocument(400, 300))
            {
                doc.LoadHtml("<div id=\"a\">ok</div>");
                Assert.That(doc.HtmlDiagnostics, Is.Empty);
                doc.LoadHtml("<div><span>x</div>\n<br></br>");
                string d = doc.HtmlDiagnostics;
                Assert.That(d, Does.StartWith("1:"));
                Assert.That(d, Does.Contain("closes 'span'"));
                Assert.That(d, Does.Contain("2:"));
                Assert.That(d, Does.Contain("void element 'br'"));
                doc.ReloadHtml("<div id=\"a\">fine</div>");
                Assert.That(doc.HtmlDiagnostics, Is.Empty);
            }
        }
    }
}
