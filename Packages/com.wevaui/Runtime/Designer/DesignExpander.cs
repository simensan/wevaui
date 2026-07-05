using System.Collections.Generic;

namespace Weva.Designer
{
    /// <summary>
    /// Expands component instances into a concrete <see cref="DesignNode"/> tree before
    /// compilation: clones each component's template, substitutes effective props
    /// (component defaults ⊕ variant ⊕ instance overrides), applies instance-level
    /// sizing/placement, and fills the template's slot with the instance's children.
    /// The result has no instances left, so <see cref="DesignCompiler"/> stays simple.
    /// </summary>
    public static class DesignExpander
    {
        const int MaxDepth = 64; // guards against component recursion cycles

        public static DesignNode Expand(DesignNode node, DesignDocument doc)
            => Expand(node, doc, 0);

        static DesignNode Expand(DesignNode node, DesignDocument doc, int depth)
        {
            if (node == null) return null;
            if (depth > MaxDepth) return CloneSelf(node); // cycle backstop — stop expanding

            // Expand children first (they may themselves be instances or contain them).
            var expandedChildren = new List<DesignNode>(node.Children.Count);
            for (int i = 0; i < node.Children.Count; i++)
                expandedChildren.Add(Expand(node.Children[i], doc, depth + 1));

            if (node.IsInstance && doc.Components.TryGetValue(node.ComponentRef, out DesignComponent comp)
                && comp.Template != null)
            {
                Dictionary<string, string> props = MergeProps(comp, node);
                DesignNode tpl = Expand(comp.Template, doc, depth + 1); // expand nested instances in the template
                SubstituteProps(tpl, props);
                ApplyInstanceOverrides(tpl, node);
                FillSlot(tpl, expandedChildren);
                return tpl;
            }

            DesignNode c = CloneSelf(node);
            c.Children.AddRange(expandedChildren);
            return c;
        }

        static Dictionary<string, string> MergeProps(DesignComponent comp, DesignNode instance)
        {
            var props = new Dictionary<string, string>(comp.Props);
            if (instance.Variant != null && comp.Variants.TryGetValue(instance.Variant, out var vp))
                foreach (var kv in vp) props[kv.Key] = kv.Value;
            if (instance.Props != null)
                foreach (var kv in instance.Props) props[kv.Key] = kv.Value;
            return props;
        }

        static void SubstituteProps(DesignNode node, Dictionary<string, string> props)
        {
            if (props.Count > 0)
            {
                // Longest names first so $title is not clobbered by $titlebar etc.
                var keys = new List<string>(props.Keys);
                keys.Sort((a, b) => b.Length.CompareTo(a.Length));

                node.Text = Sub(node.Text, props, keys);
                node.Fill = Sub(node.Fill, props, keys);
                node.TextColor = Sub(node.TextColor, props, keys);
                node.Shadow = Sub(node.Shadow, props, keys);
                if (node.Binding != null) node.Binding.Text = Sub(node.Binding.Text, props, keys);
            }
            for (int i = 0; i < node.Children.Count; i++)
                SubstituteProps(node.Children[i], props);
        }

        static string Sub(string value, Dictionary<string, string> props, List<string> keys)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('$') < 0) return value;
            for (int i = 0; i < keys.Count; i++)
                value = value.Replace("$" + keys[i], props[keys[i]]);
            return value;
        }

        // Instance controls placement/sizing; the component controls its look.
        static void ApplyInstanceOverrides(DesignNode tpl, DesignNode instance)
        {
            tpl.WidthMode = instance.WidthMode;
            tpl.HeightMode = instance.HeightMode;
            tpl.Width = instance.Width;
            tpl.Height = instance.Height;
            if (!string.IsNullOrEmpty(instance.Name)) tpl.Name = instance.Name;
        }

        static void FillSlot(DesignNode tpl, List<DesignNode> slotChildren)
        {
            DesignNode slot = FindSlot(tpl);
            if (slot == null) return; // no slot → instance children are dropped
            slot.Children.Clear();
            slot.Children.AddRange(slotChildren);
        }

        static DesignNode FindSlot(DesignNode node)
        {
            if (node.IsSlot) return node;
            for (int i = 0; i < node.Children.Count; i++)
            {
                DesignNode found = FindSlot(node.Children[i]);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Clone a node's own fields without its children (children handled by the caller).</summary>
        static DesignNode CloneSelf(DesignNode node)
        {
            DesignNode c = node.Clone();
            c.Children.Clear();
            return c;
        }
    }
}
