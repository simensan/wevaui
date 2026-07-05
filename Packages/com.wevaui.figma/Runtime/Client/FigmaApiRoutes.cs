using System;
using System.Collections.Generic;
using System.Text;

namespace Weva.Figma.Client
{
    /// <summary>
    /// Builds Figma REST API endpoint URLs. Pure string work so it's testable
    /// headlessly; the Editor transport just GETs these with an auth token.
    /// </summary>
    public static class FigmaApiRoutes
    {
        public const string Base = "https://api.figma.com/v1";

        /// <summary>Whole file: <c>GET /v1/files/:key</c>.</summary>
        public static string File(string fileKey)
            => $"{Base}/files/{Uri.EscapeDataString(fileKey ?? "")}";

        /// <summary>Specific node subtrees: <c>GET /v1/files/:key/nodes?ids=…</c>.</summary>
        public static string FileNodes(string fileKey, IEnumerable<string> ids)
            => $"{Base}/files/{Uri.EscapeDataString(fileKey ?? "")}/nodes?ids={JoinIds(ids)}";

        /// <summary>Local variables: <c>GET /v1/files/:key/variables/local</c>.</summary>
        public static string Variables(string fileKey)
            => $"{Base}/files/{Uri.EscapeDataString(fileKey ?? "")}/variables/local";

        /// <summary>Render nodes to images: <c>GET /v1/images/:key?ids=…&amp;format=…&amp;scale=…</c>.</summary>
        public static string RenderImages(string fileKey, IEnumerable<string> ids, string format = "png", double scale = 1)
        {
            var sb = new StringBuilder();
            sb.Append(Base).Append("/images/").Append(Uri.EscapeDataString(fileKey ?? ""));
            sb.Append("?ids=").Append(JoinIds(ids));
            sb.Append("&format=").Append(format);
            if (Math.Abs(scale - 1) > 1e-9) sb.Append("&scale=").Append(CssText.Number(scale));
            return sb.ToString();
        }

        /// <summary>Image-fill bitmap URLs: <c>GET /v1/files/:key/images</c> (returns imageRef → url).</summary>
        public static string ImageFills(string fileKey)
            => $"{Base}/files/{Uri.EscapeDataString(fileKey ?? "")}/images";

        static string JoinIds(IEnumerable<string> ids)
        {
            if (ids == null) return "";
            var sb = new StringBuilder();
            bool first = true;
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!first) sb.Append(',');
                sb.Append(id); // node ids (e.g. "1:23") are query-safe; comma separates
                first = false;
            }
            return sb.ToString();
        }
    }
}
