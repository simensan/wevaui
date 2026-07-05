using System.Collections.Generic;

namespace Weva.Figma.Mapping
{
    public enum RasterKind
    {
        /// <summary>A vector/boolean shape that has no Weva-subset equivalent; render the node to PNG.</summary>
        Vector,
        /// <summary>An image fill referenced by <c>imageRef</c>; fetch the bitmap.</summary>
        ImageFill,
    }

    /// <summary>An asset the export references but can't express in CSS — to be fetched/rendered separately.</summary>
    public sealed class RasterRequest
    {
        public RasterKind Kind;
        public string NodeId;    // for Vector
        public string ImageRef;  // for ImageFill
        public string FileName;  // path the generated CSS/HTML points at, e.g. images/foo.png
    }

    public sealed class ExportedDocument
    {
        public string Html;
        public string Css;
        public readonly List<RasterRequest> RasterRequests = new List<RasterRequest>();
    }
}
