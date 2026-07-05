using NUnit.Framework;

namespace Weva.Tests.EditorTests {
    public class EditorSmokeTests {
        [Test]
        public void Editor_TypesExist() {
            Assert.That(typeof(Weva.EditorTools.UIPreviewWindow), Is.Not.Null);
            Assert.That(typeof(Weva.EditorTools.UIDocumentEditor), Is.Not.Null);
        }
    }
}
