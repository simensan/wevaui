using System;

namespace Weva.Figma.Client
{
    /// <summary>A file key plus an optional node id, parsed from a Figma URL or a raw key.</summary>
    public struct FigmaTarget
    {
        public string FileKey;
        public string NodeId; // canonical "1:23" form, or null
        public bool HasNode => !string.IsNullOrEmpty(NodeId);
    }

    /// <summary>
    /// Extracts the file key and selected node from what a designer pastes —
    /// either a full Figma URL
    /// (<c>https://www.figma.com/design/KEY/Title?node-id=1-23</c>) or a bare
    /// file key. URL node ids use '-' as the separator; this normalizes them to
    /// the API's ':' form.
    /// </summary>
    public static class FigmaUrl
    {
        public static FigmaTarget Parse(string input)
        {
            var target = new FigmaTarget();
            if (string.IsNullOrEmpty(input)) return target;
            input = input.Trim();

            if (input.IndexOf("figma.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                target.FileKey = input; // a bare key
                return target;
            }

            string path = input;
            string query = "";
            int q = input.IndexOf('?');
            if (q >= 0) { path = input.Substring(0, q); query = input.Substring(q + 1); }

            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string s = segments[i];
                if (s == "design" || s == "file" || s == "proto")
                {
                    target.FileKey = segments[i + 1];
                    break;
                }
            }
            if (target.FileKey == null) target.FileKey = input;

            foreach (string part in query.Split('&'))
            {
                if (part.StartsWith("node-id=", StringComparison.Ordinal))
                {
                    string raw = Uri.UnescapeDataString(part.Substring("node-id=".Length));
                    target.NodeId = raw.Replace('-', ':');
                    break;
                }
            }
            return target;
        }
    }
}
