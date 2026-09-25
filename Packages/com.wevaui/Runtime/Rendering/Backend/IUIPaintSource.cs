namespace Weva.Rendering {
    // Something the URP pass draws: registered with UIPaintSourceRegistry,
    // drawn in ascending Order. The core-backed WevaDocument is one, through
    // Weva.Native.IUINativePaintSource.
    public interface IUIPaintSource {
        int Order { get; }
    }

    public interface IRenderViewportAwarePaintSource {
        void PrepareForRenderViewport(int width, int height);
    }
}
