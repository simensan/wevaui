namespace Weva.Paint.Images {
    // Backend-agnostic handle to a 2D image. The framework never inspects
    // the contents — backends downcast to their concrete type (URP wraps
    // `UnityEngine.Texture2D`/`Sprite`, IMGUI wraps `Texture`, the headless
    // software rasterizer wraps a managed `byte[]` of RGBA pixels). Game
    // code registers IImageSource instances with `IImageRegistry`; the
    // converter never resolves them, only ferries the handle string.
    //
    // Width/Height are the SOURCE pixel dimensions; layout / paint use them
    // for natural-size resolution (e.g. `<img>` with no explicit width
    // takes the source width as its content size). Returning 0 from
    // either is treated as "unknown" and consumers fall back to the
    // element's CSS-set size.
    public interface IImageSource {
        int Width { get; }
        int Height { get; }
    }
}
