using NUnit.Framework;

namespace Weva.Tests.EditorTests {
    // The editor assembly's entry points exist and are the types the docs
    // name: the WevaDocument inspector, the Elements window, the setup.
    public class EditorSmokeTests {
        [Test]
        public void Editor_types_are_present() {
            Assert.That(typeof(Weva.EditorTools.WevaDocumentEditor), Is.Not.Null);
            Assert.That(typeof(Weva.EditorTools.DevTools.NativeElementsWindow), Is.Not.Null);
            Assert.That(typeof(Weva.EditorTools.Setup.UrpFeatureSetup), Is.Not.Null);
            Assert.That(typeof(Weva.EditorTools.Documents.UIDocumentAssetWatcher), Is.Not.Null);
            Assert.That(typeof(Weva.EditorTools.Documents.WevaDocumentLinkBaker), Is.Not.Null);
        }
    }
}
