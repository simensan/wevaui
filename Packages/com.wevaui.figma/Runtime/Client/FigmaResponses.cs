using System.Collections.Generic;
using Weva.Figma.Json;
using Weva.Figma.Model;

namespace Weva.Figma.Client
{
    /// <summary>
    /// Parses Figma REST responses into the bridge's model. Pure (JSON in → model
    /// out), so it's fully testable without a network or Unity.
    /// </summary>
    public static class FigmaResponses
    {
        /// <summary>The document tree from <c>GET /v1/files/:key</c>.</summary>
        public static FigmaNode ParseFileDocument(string json)
        {
            JsonValue root = JsonParser.Parse(json);
            return FigmaNode.Parse(root["document"]);
        }

        /// <summary>The file's display name from <c>GET /v1/files/:key</c>.</summary>
        public static string ParseFileName(string json)
            => JsonParser.Parse(json)["name"].AsString(null);

        /// <summary>id → node from <c>GET /v1/files/:key/nodes?ids=…</c> (reads <c>nodes[id].document</c>).</summary>
        public static Dictionary<string, FigmaNode> ParseNodes(string json)
        {
            var map = new Dictionary<string, FigmaNode>();
            JsonValue nodes = JsonParser.Parse(json)["nodes"];
            foreach (var kv in nodes.Members)
            {
                JsonValue doc = kv.Value["document"];
                if (!doc.IsNull) map[kv.Key] = FigmaNode.Parse(doc);
            }
            return map;
        }

        /// <summary>id → rendered image URL from <c>GET /v1/images/:key</c> (nulls skipped).</summary>
        public static Dictionary<string, string> ParseRenderedImageUrls(string json)
        {
            var map = new Dictionary<string, string>();
            JsonValue images = JsonParser.Parse(json)["images"];
            foreach (var kv in images.Members)
            {
                string url = kv.Value.AsString(null);
                if (url != null) map[kv.Key] = url;
            }
            return map;
        }

        /// <summary>imageRef → bitmap URL from <c>GET /v1/files/:key/images</c> (reads <c>meta.images</c>).</summary>
        public static Dictionary<string, string> ParseImageFillUrls(string json)
        {
            var map = new Dictionary<string, string>();
            JsonValue images = JsonParser.Parse(json)["meta"]["images"];
            foreach (var kv in images.Members)
            {
                string url = kv.Value.AsString(null);
                if (url != null) map[kv.Key] = url;
            }
            return map;
        }
    }
}
