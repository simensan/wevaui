#if UNITY_2023_1_OR_NEWER
using UnityEngine;

namespace Weva.Text.TextCore {
    public sealed partial class GlyphAtlas {
        Texture2D texture;
        byte[] cpuBuffer;

        // When non-null, the Texture property returns this override instead of
        // our owned R8 texture. Used by the TMP-backed text path
        // (TmpFontAssetSource.Atlas) so AtlasRegistry.GetTextureById hands the
        // renderer a pre-baked TMP atlas without forcing the entire shelf-pack
        // pipeline to run. The owned texture is kept alive for fall-through
        // glyphs that miss the TMP asset and get rasterized at runtime.
        Texture2D textureOverride;
        public Texture2D TextureOverride {
            get => textureOverride;
            set {
                if (textureOverride == value) return;
                textureOverride = value;
                Revision++;
            }
        }

        public Texture2D Texture => textureOverride != null ? textureOverride : EnsureTexture();

        partial void InitializeBackingStore() {
            // R8 keeps memory cost at 1 byte/texel. The CPU-side byte[] copy
            // exists so we can grow without re-rasterizing every glyph and so
            // that LoadRawTextureData uploads are cheap. Apply(false, false)
            // keeps mipmap generation off and preserves CPU readability for
            // subsequent Apply calls.
            texture = new Texture2D(Width, Height, TextureFormat.R8, mipChain: false, linear: true);
            texture.name = "Weva.GlyphAtlas";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            cpuBuffer = new byte[Width * Height];
            texture.LoadRawTextureData(cpuBuffer);
            texture.Apply(false, false);
        }

        Texture2D EnsureTexture() {
            if (texture != null) return texture;
            texture = new Texture2D(Width, Height, TextureFormat.R8, mipChain: false, linear: true);
            texture.name = "Weva.GlyphAtlas";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            if (cpuBuffer == null || cpuBuffer.Length != Width * Height) {
                cpuBuffer = new byte[Width * Height];
            }
            texture.LoadRawTextureData(cpuBuffer);
            texture.Apply(false, false);
            return texture;
        }

        partial void ResizeBackingStore(int oldW, int oldH, int newW, int newH) {
            var newBuf = new byte[newW * newH];
            for (int y = 0; y < oldH; y++) {
                System.Buffer.BlockCopy(cpuBuffer, y * oldW, newBuf, y * newW, oldW);
            }
            cpuBuffer = newBuf;
            DestroyTexture();
            texture = new Texture2D(newW, newH, TextureFormat.R8, mipChain: false, linear: true);
            texture.name = "Weva.GlyphAtlas";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.LoadRawTextureData(cpuBuffer);
            texture.Apply(false, false);
        }

        static void DestroyAny(Object obj) {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj);
            else Object.DestroyImmediate(obj);
        }

        void DestroyTexture() {
            if (texture != null) {
                DestroyAny(texture);
                texture = null;
            }
        }

        partial void UploadPixels(int dstX, int dstY, RasterizedGlyph raster) {
            for (int row = 0; row < raster.Height; row++) {
                int srcOff = row * raster.Width;
                int dstOff = (dstY + row) * Width + dstX;
                System.Buffer.BlockCopy(raster.Pixels, srcOff, cpuBuffer, dstOff, raster.Width);
            }
            // The cpuBuffer now holds the new glyph. Inside a prepare window
            // NotifyPixelsChanged just marks the page dirty; the actual
            // LoadRawTextureData + Apply is coalesced to one call at the batch
            // flush (see FlushTextureUploadToGpu) instead of once per glyph.
            NotifyPixelsChanged();
        }

        partial void RelocatePixels(System.Collections.Generic.List<SlotMove> moves) {
            if (cpuBuffer == null || moves == null || moves.Count == 0) return;
            // Snapshot the pre-defrag buffer: moves can overlap (a slot's new
            // rect may cover another slot's old rect), so every copy reads
            // from the frozen source, never the in-place destination.
            var src = new byte[cpuBuffer.Length];
            System.Buffer.BlockCopy(cpuBuffer, 0, src, 0, cpuBuffer.Length);
            for (int i = 0; i < moves.Count; i++) {
                CopyBlock(src, cpuBuffer, Width, moves[i]);
            }
            // Only push if a texture already exists — a defrag happens during
            // an eviction inside a glyph insert, and that insert's UploadPixels
            // fires NotifyPixelsChanged right after, so the relocated rows reach
            // the GPU in the same (possibly deferred) upload regardless.
            if (texture != null) NotifyPixelsChanged();
        }

        partial void FlushTextureUploadToGpu() {
            var tex = EnsureTexture();
            tex.LoadRawTextureData(cpuBuffer);
            tex.Apply(false, false);
        }
    }
}
#endif
