using NUnit.Framework;
using Weva.Figma.Fidelity;

namespace Weva.Figma.Tests.Fidelity
{
    [TestFixture]
    public class ImageDiffTests
    {
        // A 2x2 solid-white RGBA buffer (16 bytes).
        static byte[] White() => Fill(255, 255, 255, 255);

        static byte[] Fill(byte r, byte g, byte b, byte a)
        {
            var buf = new byte[2 * 2 * 4];
            for (int i = 0; i < buf.Length; i += 4) { buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = a; }
            return buf;
        }

        [Test]
        public void IdenticalBuffersPassWithNoDiff()
        {
            FidelityReport r = ImageDiff.Compare(White(), 2, 2, White(), 2, 2);
            Assert.That(r.SizeMismatch, Is.False);
            Assert.That(r.DifferingPixels, Is.EqualTo(0));
            Assert.That(r.DiffFraction, Is.EqualTo(0).Within(1e-9));
            Assert.That(r.MaxChannelDelta, Is.EqualTo(0));
            Assert.That(r.Classify(), Is.EqualTo(FidelityVerdict.Pass));
        }

        [Test]
        public void ChangingOnePixelBeyondThresholdCountsAsDiffering()
        {
            byte[] b = White();
            b[0] = 0; // first pixel red channel 255 -> 0
            FidelityReport r = ImageDiff.Compare(White(), 2, 2, b, 2, 2);
            Assert.That(r.DifferingPixels, Is.EqualTo(1));
            Assert.That(r.MaxChannelDelta, Is.EqualTo(255));
            Assert.That(r.DiffFraction, Is.EqualTo(0.25).Within(1e-9)); // 1 of 4
        }

        [Test]
        public void ChangeWithinThresholdIsNotDiffering()
        {
            byte[] b = White();
            b[0] = 251; // delta 4, below default threshold 6
            FidelityReport r = ImageDiff.Compare(White(), 2, 2, b, 2, 2);
            Assert.That(r.DifferingPixels, Is.EqualTo(0));
            Assert.That(r.MaxChannelDelta, Is.EqualTo(4));
        }

        [Test]
        public void SizeMismatchFails()
        {
            FidelityReport r = ImageDiff.Compare(White(), 2, 2, new byte[4], 1, 1);
            Assert.That(r.SizeMismatch, Is.True);
            Assert.That(r.Classify(), Is.EqualTo(FidelityVerdict.Fail));
        }

        [Test]
        public void VerdictTransitionsOnFraction()
        {
            var t = new FidelityThresholds { ChannelThreshold = 6, WarnFraction = 0.10, FailFraction = 0.30 };

            // 1/4 = 0.25 → between warn and fail → Warn
            byte[] one = White(); one[0] = 0;
            Assert.That(ImageDiff.Compare(White(), 2, 2, one, 2, 2, t).Classify(t), Is.EqualTo(FidelityVerdict.Warn));

            // 2/4 = 0.50 → above fail → Fail
            byte[] two = White(); two[0] = 0; two[4] = 0;
            Assert.That(ImageDiff.Compare(White(), 2, 2, two, 2, 2, t).Classify(t), Is.EqualTo(FidelityVerdict.Fail));
        }

        [Test]
        public void HeatmapMarksDifferingPixelsRed()
        {
            byte[] b = White();
            b[0] = 0;
            FidelityReport r = ImageDiff.Compare(White(), 2, 2, b, 2, 2, null, buildHeatmap: true);
            Assert.That(r.Heatmap, Is.Not.Null);
            Assert.That(r.Heatmap.Length, Is.EqualTo(16));
            // first pixel differs → red opaque
            Assert.That(r.Heatmap[0], Is.EqualTo(255));
            Assert.That(r.Heatmap[1], Is.EqualTo(0));
            Assert.That(r.Heatmap[2], Is.EqualTo(0));
            Assert.That(r.Heatmap[3], Is.EqualTo(255));
        }

        [Test]
        public void MeanChannelDeltaAveragesAllChannels()
        {
            byte[] b = White();
            // change all 4 channels of pixel 0 by 255*? -> set to 0: delta 255 each on 4 channels of 1 px.
            b[0] = 0; b[1] = 0; b[2] = 0; b[3] = 0;
            FidelityReport r = ImageDiff.Compare(White(), 2, 2, b, 2, 2);
            // total channel delta = 4*255 over 16 channels (4px*4ch) = 1020/16 = 63.75
            Assert.That(r.MeanChannelDelta, Is.EqualTo(63.75).Within(1e-6));
        }
    }
}
