using System.Collections.Generic;

namespace Weva.Designer.Templates
{
    /// <summary>
    /// A ready-made library of standard components (Button, Card, Panel, SettingRow,
    /// Heading, ListItem) plus the theme tokens they use. <see cref="Install"/> drops the
    /// whole kit into a document so the editor's library panel and "New from template"
    /// have real, token-driven building blocks out of the box. Everything is token-based,
    /// so a single theme swap restyles the entire kit.
    /// </summary>
    public static class DesignComponentKit
    {
        /// <summary>Add the kit's theme tokens to a token set.</summary>
        public static DesignTokens ApplyTheme(DesignTokens t)
        {
            t.Color("bg", "#0e0f14").Color("surface", "#1b1d27").Color("surface-hover", "#242636")
             .Color("primary", "#5b8cff").Color("on-primary", "#ffffff")
             .Color("text", "#f2f4f8").Color("muted", "#9aa0ad")
             .Color("accent", "#ffcc00").Color("danger", "#ff5566");
            t.Space("xs", 4).Space("sm", 8).Space("md", 16).Space("lg", 24).Space("xl", 32);
            t.Radius("sm", 6).Radius("md", 10).Radius("lg", 16).Radius("pill", 999);
            t.Font("caption", 12).Font("body", 16).Font("h2", 24).Font("h1", 32);
            t.Shadow("elevated", "0 4px 12px rgba(0,0,0,0.35)");
            return t;
        }

        /// <summary>Install theme tokens + all kit components into a document.</summary>
        public static void Install(DesignDocument doc)
        {
            ApplyTheme(doc.Tokens);
            foreach (DesignComponent c in All()) doc.AddComponent(c);
        }

        public static IReadOnlyList<DesignComponent> All() => new[]
        {
            Button(), Card(), Panel(), SettingRow(), Heading(), ListItem(),
        };

        // --- Components ---

        public static DesignComponent Button()
        {
            var tpl = new DesignNode("Button")
            {
                Layout = LayoutMode.Row, MainAlign = MainAlign.Center, CrossAlign = CrossAlign.Center,
                Fill = "$bg", Radius = Dim.Token("md"), Gap = Dim.Token("sm"),
                // Production polish: feels clickable + animates into its hover/pressed states.
                Cursor = Cursor.Pointer, TransitionMs = 120,
            };
            SetPad(tpl, "sm", "md");
            tpl.Add(new DesignNode("Label")
            {
                Text = "$label", TextColor = "$fg", FontSize = Dim.Token("body"), FontWeight = FontWeight.SemiBold,
            });
            tpl.State(InteractionState.Hover).Opacity = 0.92;
            tpl.State(InteractionState.Pressed).Opacity = 0.85;
            tpl.State(InteractionState.Disabled).Opacity = 0.4;

            return new DesignComponent("Button", tpl)
                .Prop("label", "Button").Prop("bg", "{primary}").Prop("fg", "{on-primary}")
                .Variant("primary", new Dictionary<string, string> { { "bg", "{primary}" }, { "fg", "{on-primary}" } })
                .Variant("secondary", new Dictionary<string, string> { { "bg", "{surface}" }, { "fg", "{text}" } })
                .Variant("ghost", new Dictionary<string, string> { { "bg", "transparent" }, { "fg", "{primary}" } });
        }

        public static DesignComponent Card()
        {
            var tpl = new DesignNode("Card")
            {
                Layout = LayoutMode.Column, Fill = "{surface}", Radius = Dim.Token("lg"),
                Shadow = "{elevated}", Gap = Dim.Token("md"),
                // Clip content (e.g. a cover image) to the rounded corners; the outer shadow is unaffected.
                Overflow = Overflow.Clip,
            };
            SetPad(tpl, "lg", "lg");
            var slot = new DesignNode("Content") { Layout = LayoutMode.Column, IsSlot = true, Gap = Dim.Token("sm") };
            slot.SetSize(SizeMode.Fill, SizeMode.Hug);
            tpl.Add(slot);
            return new DesignComponent("Card", tpl);
        }

        public static DesignComponent Panel()
        {
            var tpl = new DesignNode("Panel")
            {
                Layout = LayoutMode.Column, Fill = "{bg}", Gap = Dim.Token("md"),
            };
            SetPad(tpl, "lg", "lg");
            var slot = new DesignNode("Content") { Layout = LayoutMode.Column, IsSlot = true, Gap = Dim.Token("md") };
            slot.SetSize(SizeMode.Fill, SizeMode.Fill);
            tpl.Add(slot);
            return new DesignComponent("Panel", tpl);
        }

        public static DesignComponent SettingRow()
        {
            var tpl = new DesignNode("SettingRow")
            {
                Layout = LayoutMode.Row, MainAlign = MainAlign.SpaceBetween, CrossAlign = CrossAlign.Center,
                Fill = "{surface}", Radius = Dim.Token("md"),
            };
            tpl.SetSize(SizeMode.Fill, SizeMode.Hug);
            SetPad(tpl, "md", "md");
            tpl.Add(new DesignNode("Label") { Text = "$label", TextColor = "{text}", FontSize = Dim.Token("body") });
            tpl.Add(new DesignNode("Value") { Text = "$value", TextColor = "{muted}", FontSize = Dim.Token("body") });
            return new DesignComponent("SettingRow", tpl)
                .Prop("label", "Setting").Prop("value", "On");
        }

        public static DesignComponent Heading()
        {
            var tpl = new DesignNode("Heading") { Text = "$text", TextColor = "{text}", FontSize = Dim.Token("h1") };
            return new DesignComponent("Heading", tpl).Prop("text", "Heading");
        }

        public static DesignComponent ListItem()
        {
            var tpl = new DesignNode("ListItem")
            {
                Layout = LayoutMode.Row, CrossAlign = CrossAlign.Center, Gap = Dim.Token("sm"),
                Radius = Dim.Token("sm"),
                // Rows are selectable: clickable cursor + a smooth hover highlight.
                Cursor = Cursor.Pointer, TransitionMs = 120,
            };
            tpl.SetSize(SizeMode.Fill, SizeMode.Hug);
            SetPad(tpl, "sm", "sm");
            tpl.State(InteractionState.Hover).Fill = "{surface-hover}";
            var slot = new DesignNode("Content") { Layout = LayoutMode.Row, IsSlot = true, Gap = Dim.Token("sm") };
            slot.SetSize(SizeMode.Fill, SizeMode.Hug);
            tpl.Add(slot);
            return new DesignComponent("ListItem", tpl);
        }

        // Set vertical (v) and horizontal (h) padding from spacing tokens.
        static void SetPad(DesignNode n, string v, string h)
        {
            n.PadTop = Dim.Token(v);
            n.PadBottom = Dim.Token(v);
            n.PadLeft = Dim.Token(h);
            n.PadRight = Dim.Token(h);
        }
    }
}
