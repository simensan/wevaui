using System.Collections.Generic;
using Weva.Figma.Model;

namespace Weva.Figma.Client
{
    /// <summary>Navigation helpers for picking export roots out of a parsed Figma tree.</summary>
    public static class FigmaNodeQuery
    {
        /// <summary>Depth-first search for the first node whose name matches exactly.</summary>
        public static FigmaNode FindByName(FigmaNode root, string name)
        {
            if (root == null) return null;
            if (root.Name == name) return root;
            if (root.Children != null)
                foreach (FigmaNode c in root.Children)
                {
                    FigmaNode hit = FindByName(c, name);
                    if (hit != null) return hit;
                }
            return null;
        }

        public static FigmaNode FindById(FigmaNode root, string id)
        {
            if (root == null) return null;
            if (root.Id == id) return root;
            if (root.Children != null)
                foreach (FigmaNode c in root.Children)
                {
                    FigmaNode hit = FindById(c, id);
                    if (hit != null) return hit;
                }
            return null;
        }

        /// <summary>
        /// The top-level exportable frames: direct frame/component/section children
        /// of the canvases (pages). If <paramref name="root"/> is already a single
        /// frame/component (e.g. from a <c>/nodes</c> fetch), returns just it.
        /// </summary>
        public static List<FigmaNode> CollectExportableFrames(FigmaNode root)
        {
            var result = new List<FigmaNode>();
            if (root == null) return result;

            if (root.Type == "DOCUMENT")
            {
                if (root.Children != null)
                    foreach (FigmaNode canvas in root.Children)
                        AddFrameChildren(canvas, result);
            }
            else if (root.Type == "CANVAS")
            {
                AddFrameChildren(root, result);
            }
            else
            {
                result.Add(root);
            }
            return result;
        }

        static void AddFrameChildren(FigmaNode container, List<FigmaNode> result)
        {
            if (container?.Children == null) return;
            foreach (FigmaNode c in container.Children)
                if (IsExportable(c)) result.Add(c);
        }

        static bool IsExportable(FigmaNode n)
            => n.Type == "FRAME" || n.Type == "COMPONENT" || n.Type == "COMPONENT_SET" || n.Type == "SECTION";
    }
}
