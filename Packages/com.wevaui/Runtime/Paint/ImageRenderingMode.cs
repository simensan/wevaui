namespace Weva.Paint {
    // Resolved value of the CSS `image-rendering` property. Renderer
    // backends translate this into their texture sampler state — URP sets
    // `Texture.filterMode = Point/Bilinear`; IMGUI uses `FilterMode.Point`
    // / `FilterMode.Bilinear` on the cached `Texture2D`. Headless test
    // backends (the software rasterizer used by golden snapshots) currently
    // ignore the value because they don't sample mip-mapped textures, but
    // the resolved enum still flows through Brush so the rendering path
    // can pick it up without re-parsing the style.
    //
    // CSS spec values not in this enum (`smooth`, `high-quality`) are
    // treated as `Auto` — the browsers do this in practice anyway, and
    // game UI authoring almost always wants `auto` (let the GPU pick) or
    // `pixelated` (point sampling for pixel art).
    public enum ImageRenderingMode {
        // Default. Backends pick whatever's most natural — typically
        // bilinear for upscaled textures.
        Auto,
        // CSS `crisp-edges`. Implementation-defined per spec; in v1 we
        // alias to Pixelated so authors who reach for it (often confused
        // about which keyword to use) get the expected nearest-neighbor
        // behavior.
        CrispEdges,
        // CSS `pixelated`. Explicit nearest-neighbor / point sampling.
        // Required for correct pixel-art rendering when sprites are
        // displayed at non-integer or non-1x scales.
        Pixelated,
    }
}
