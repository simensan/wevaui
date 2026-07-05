using NUnit.Framework;

namespace Weva.Tests {
    public class SmokeTests {
        [Test]
        public void UIDocument_TypeExists() {
            Assert.That(typeof(Weva.WevaDocument), Is.Not.Null);
        }
    }
}
