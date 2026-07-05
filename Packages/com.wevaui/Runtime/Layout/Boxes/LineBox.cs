namespace Weva.Layout.Boxes {
    public sealed class LineBox : Box {
        public double Baseline { get; internal set; }
        public bool IsFinalLine { get; internal set; }

        // CSS Text §7 text-align centering / right-alignment offset that
        // InlineLayout.ApplyTextAlign added to each child TextRun.X (and
        // inline-block BlockBox.X) on this line. Tracked here so a later
        // ApplyTextAlign call — typically when FlexLayout/GridLayout re-run
        // an item's inline content after the item width finalises — can undo
        // the previous offset before applying the new one. Without this,
        // every additional pass re-stamps `tr.X += dx` ON TOP OF the prior
        // shift; if the first pass ran with a wider availableWidth (e.g. the
        // flex container's full content area, before per-item sizing), the
        // text run ends up shifted past the item's actual right edge.
        // Visible symptom: `text-align: center` (often inherited from the
        // button UA rule) on a flex item with `justify-content: center`
        // renders text outside the item's bounds.
        public double AppliedTextAlignDelta { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            Baseline = 0;
            IsFinalLine = false;
            AppliedTextAlignDelta = 0;
        }
    }
}
