namespace Weva.Paint {
    // Pure pixel-snapping math shared by render backends. Lives in the Paint
    // namespace (not the URP backend) so the headless test suite can pin the
    // contracts — the backend folders are excluded from the headless compile.
    public static class PixelSnapping {
        // B-9SLICE-SNAP: 9-slice parts round ALL FOUR edges to device pixels
        // (Math.Round per edge, like Chrome snaps border-image part
        // boundaries — parts never AA against each other, CSS Backgrounds 3
        // §6.2). The parts share boundary coordinates pre-snap, so identical
        // inputs round to identical integers and abutting quads stay flush:
        // no fractional coverage at the seam, no ~1px dim band. The
        // icon-jump concern that keeps plain images on the origin-preserving
        // snap (UIBatcher.SnapSampledFillToPixels) doesn't apply — a frame's
        // parts move WITH their shared boundaries, so the whole frame shifts
        // coherently by <1px instead of any part sliding relative to
        // another. Consumed for brushes with Brush.SnapEdgesToDevicePixels.
        public static Rect SnapSlicePartEdges(Rect r) {
            double left = System.Math.Round(r.X);
            double top = System.Math.Round(r.Y);
            double right = System.Math.Round(r.X + r.Width);
            double bottom = System.Math.Round(r.Y + r.Height);
            if (right <= left && r.Width > 0) right = left + 1;
            if (bottom <= top && r.Height > 0) bottom = top + 1;
            return new Rect(left, top, right - left, bottom - top);
        }
    }
}
