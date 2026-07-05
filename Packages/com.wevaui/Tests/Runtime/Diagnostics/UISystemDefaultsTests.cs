using NUnit.Framework;
using Weva.Diagnostics;
using Weva.Layout;
using Weva.Layout.Diagnostics;
using Weva.Layout.Scrolling;
using Weva.Paint.Conversion;

namespace Weva.Tests.Diagnostics {
    // A6: ResetCoreDefaults restores every always-compiled static feature /
    // diagnostic flag to its shipping default, so a play session (with domain
    // reload off) or a test that flipped a flag can't leak it forward.
    public class UISystemDefaultsTests {
        [SetUp]
        public void Reset() => UISystemDefaults.ResetCoreDefaults();

        [TearDown]
        public void Restore() => UISystemDefaults.ResetCoreDefaults();

        [Test]
        public void Reset_restores_every_core_flag_to_default() {
            // Flip them all away from their defaults.
            UICssDiagnostics.Enabled = false;
            UICssDiagnostics.LogLevel = WevaLogLevel.Off;
            UILayoutDiagnostics.Enabled = true;
            UILayoutDiagnostics.MatchClassContains = "debug";
            UILayoutDiagnostics.TraceMaskEncoding = true;
            LayoutInvariants.Enabled = true;
            LayoutEngine.EnableScrollBoundaryReuse = false;
            LayoutEngine.EnableBubbleSkip = false;
            LayoutEngine.EnableIncrementalHeightPropagation = false;
            ScrollEventHandler.EnableViewportDragScroll = true;
            BoxToPaintConverter.EnableExactSrgbGlassCompositing = false;

            UISystemDefaults.ResetCoreDefaults();

            Assert.That(UICssDiagnostics.Enabled, Is.True);
            Assert.That(UICssDiagnostics.LogLevel, Is.EqualTo(WevaLogLevel.Warnings));
            Assert.That(UILayoutDiagnostics.Enabled, Is.False);
            Assert.That(UILayoutDiagnostics.MatchClassContains, Is.Null);
            Assert.That(UILayoutDiagnostics.TraceMaskEncoding, Is.False);
            Assert.That(LayoutInvariants.Enabled, Is.False);
            Assert.That(LayoutEngine.EnableScrollBoundaryReuse, Is.True);
            Assert.That(LayoutEngine.EnableBubbleSkip, Is.True);
            Assert.That(LayoutEngine.EnableIncrementalHeightPropagation, Is.True);
            Assert.That(ScrollEventHandler.EnableViewportDragScroll, Is.False);
            Assert.That(BoxToPaintConverter.EnableExactSrgbGlassCompositing, Is.True);
        }
    }
}
