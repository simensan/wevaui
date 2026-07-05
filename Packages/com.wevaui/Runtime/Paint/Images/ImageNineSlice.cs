namespace Weva.Paint.Images {
    public readonly struct ImageNineSlice {
        public readonly double Top;
        public readonly double Right;
        public readonly double Bottom;
        public readonly double Left;

        public ImageNineSlice(double top, double right, double bottom, double left) {
            Top = top;
            Right = right;
            Bottom = bottom;
            Left = left;
        }

        public bool IsEmpty => Top <= 0 && Right <= 0 && Bottom <= 0 && Left <= 0;
    }

    // Optional metadata interface for image sources that carry a native
    // 9-slice border, such as Unity sprites. CSS remains authoritative:
    // explicit border-image-slice values still override this metadata.
    public interface IImageNineSliceSource {
        bool TryGetNineSlice(out ImageNineSlice slice);
    }
}
