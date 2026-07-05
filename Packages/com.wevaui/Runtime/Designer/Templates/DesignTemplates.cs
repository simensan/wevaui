using System;
using System.Collections.Generic;

namespace Weva.Designer.Templates
{
    /// <summary>One named starting point for the editor's "New from template" picker.</summary>
    public struct DesignTemplate
    {
        public string Name;
        public string Description;
        public Func<DesignDocument> Create;
    }

    /// <summary>
    /// Starter documents built straight from the IR — proof the model is expressive
    /// enough for real game screens, and the seed content for the editor's template
    /// picker. Designers start by editing one of these, never a blank div.
    /// All values are token-driven so a theme swap restyles every template.
    /// </summary>
    public static class DesignTemplates
    {
        /// <summary>The shared dark game-UI theme used by the starter templates.</summary>
        public static DesignTokens DarkTheme(DesignTokens t = null)
        {
            t ??= new DesignTokens();
            t.Color("bg", "#0e0f14");
            t.Color("surface", "#1b1d27");
            t.Color("primary", "#5b8cff");
            t.Color("on-primary", "#ffffff");
            t.Color("text", "#f2f4f8");
            t.Color("muted", "#9aa0ad");
            t.Color("accent", "#ffcc00");
            t.Color("danger", "#ff5566");
            return t;
        }

        /// <summary>The catalog shown in the editor's template picker.</summary>
        public static IReadOnlyList<DesignTemplate> Catalog() => new[]
        {
            new DesignTemplate { Name = "Blank", Description = "An empty screen to start from.", Create = Blank },
            new DesignTemplate { Name = "Main Menu", Description = "Centered title with a stack of buttons.", Create = MainMenu },
            new DesignTemplate { Name = "Combat HUD", Description = "Top bar with health and score.", Create = CombatHud },
            new DesignTemplate { Name = "Settings", Description = "A list of labeled setting rows.", Create = SettingsPanel },
        };

        // --- Templates ---

        public static DesignDocument Blank()
        {
            var root = new DesignNode("Screen") { Layout = LayoutMode.Column, Fill = "{bg}" };
            root.SetFixedSize(390, 844);
            root.SetPadding(24);
            var doc = new DesignDocument(root);
            DarkTheme(doc.Tokens);
            return doc;
        }

        public static DesignDocument MainMenu()
        {
            var root = new DesignNode("Main Menu")
            {
                Layout = LayoutMode.Column,
                MainAlign = MainAlign.Center,
                CrossAlign = CrossAlign.Center,
                Gap = 24,
                Fill = "{bg}",
            };
            root.SetFixedSize(390, 844);
            root.SetPadding(32);

            root.Add(new DesignNode("Title") { Text = "WEVA", TextColor = "{text}", FontSize = 48 });
            root.Add(new DesignNode("Subtitle") { Text = "Press start", TextColor = "{muted}", FontSize = 16 });

            var buttons = new DesignNode("Buttons") { Layout = LayoutMode.Column, Gap = 12 };
            buttons.SetSize(SizeMode.Fill, SizeMode.Hug);
            buttons.Add(PrimaryButton("Play"));
            buttons.Add(PrimaryButton("Settings"));
            buttons.Add(PrimaryButton("Quit"));
            root.Add(buttons);

            var doc = new DesignDocument(root);
            DarkTheme(doc.Tokens);
            return doc;
        }

        public static DesignDocument CombatHud()
        {
            var root = new DesignNode("HUD") { Layout = LayoutMode.Column };
            root.SetFixedSize(960, 540);

            var top = new DesignNode("Top Bar")
            {
                Layout = LayoutMode.Row,
                MainAlign = MainAlign.SpaceBetween,
                CrossAlign = CrossAlign.Center,
            };
            top.SetSize(SizeMode.Fill, SizeMode.Hug);
            top.SetPadding(16);
            top.Add(Badge("Health", "100", "{danger}"));
            top.Add(Badge("Score", "12,400", "{accent}"));
            root.Add(top);

            // Spacer fills the middle so the top bar pins to the top.
            var spacer = new DesignNode("Spacer");
            spacer.SetSize(SizeMode.Fill, SizeMode.Fill);
            root.Add(spacer);

            var doc = new DesignDocument(root);
            DarkTheme(doc.Tokens);
            return doc;
        }

        public static DesignDocument SettingsPanel()
        {
            var root = new DesignNode("Settings")
            {
                Layout = LayoutMode.Column,
                Gap = 8,
                Fill = "{bg}",
            };
            root.SetFixedSize(480, 720);
            root.SetPadding(24);

            root.Add(new DesignNode("Heading") { Text = "Settings", TextColor = "{text}", FontSize = 28 });

            var list = new DesignNode("List") { Layout = LayoutMode.Column, Gap = 8 };
            list.SetSize(SizeMode.Fill, SizeMode.Hug);
            list.Add(SettingRow("Master Volume", "80%"));
            list.Add(SettingRow("Difficulty", "Normal"));
            list.Add(SettingRow("Fullscreen", "On"));
            list.Add(SettingRow("Language", "English"));
            root.Add(list);

            var doc = new DesignDocument(root);
            DarkTheme(doc.Tokens);
            return doc;
        }

        // --- Reusable fragments ---

        static DesignNode PrimaryButton(string label)
        {
            var b = new DesignNode(label)
            {
                Layout = LayoutMode.Row,
                MainAlign = MainAlign.Center,
                CrossAlign = CrossAlign.Center,
                Fill = "{primary}",
                Radius = 10,
            };
            b.SetSize(SizeMode.Fill, SizeMode.Hug);
            b.SetPadding(14);
            b.Add(new DesignNode("Label") { Text = label, TextColor = "{on-primary}", FontSize = 18 });
            return b;
        }

        static DesignNode Badge(string name, string value, string accentToken)
        {
            var badge = new DesignNode(name)
            {
                Layout = LayoutMode.Row,
                CrossAlign = CrossAlign.Center,
                Gap = 8,
                Fill = "{surface}",
                Radius = 8,
            };
            badge.SetPadding(10);
            badge.Add(new DesignNode("Name") { Text = name, TextColor = "{muted}", FontSize = 14 });
            badge.Add(new DesignNode("Value") { Text = value, TextColor = accentToken, FontSize = 18 });
            return badge;
        }

        static DesignNode SettingRow(string label, string value)
        {
            var row = new DesignNode(label)
            {
                Layout = LayoutMode.Row,
                MainAlign = MainAlign.SpaceBetween,
                CrossAlign = CrossAlign.Center,
                Fill = "{surface}",
                Radius = 8,
            };
            row.SetSize(SizeMode.Fill, SizeMode.Hug);
            row.SetPadding(14);
            row.Add(new DesignNode("Label") { Text = label, TextColor = "{text}", FontSize = 16 });
            row.Add(new DesignNode("Value") { Text = value, TextColor = "{muted}", FontSize = 16 });
            return row;
        }
    }
}
