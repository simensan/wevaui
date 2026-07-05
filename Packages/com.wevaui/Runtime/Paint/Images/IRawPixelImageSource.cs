namespace Weva.Paint.Images {
    // Extension interface for IImageSource implementations that can expose their
    // pixel data directly as a managed RGBA byte array. Used by the software
    // rasterizer (golden pipeline) to sample URL-sourced mask images without
    // touching any Unity APIs. Unity backends (Texture2D, Sprite, RenderTexture)
    // do NOT implement this interface and remain opaque to the software path.
    //
    // Layout:
    //   pixels[y * width * 4 + x * 4 + 0]  = R   (0..255 linear, NOT sRGB)
    //   pixels[y * width * 4 + x * 4 + 1]  = G
    //   pixels[y * width * 4 + x * 4 + 2]  = B
    //   pixels[y * width * 4 + x * 4 + 3]  = A   (pre-multiplied? No — straight alpha)
    //
    // Row 0 is the TOP row (matches CSS image-data conventions).
    public interface IRawPixelImageSource : IImageSource {
        // RGBA bytes, row-major, top-left origin. Length must be Width * Height * 4.
        byte[] Pixels { get; }
    }
}
