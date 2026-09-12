// The Frontier Camp game logic (examples/frontier_camp/camp_state.gd)
// as a Unity script: the same model shape, the same rules, so the same
// camp.html drives it through the core in Unity. Kept plain C# on
// dictionaries and lists so the binding source reads it directly.
using System;
using System.Collections.Generic;

namespace Weva.Tests.EditorTests.Native
{
    public sealed class FrontierCampState
    {
        public readonly Dictionary<string, object> Model;
        public event Action Changed;
        public int ElapsedSeconds;

        public FrontierCampState()
        {
            Model = new Dictionary<string, object>
            {
                ["Player"] = new Dictionary<string, object> { ["Name"] = "Morgan", ["Health"] = 72, ["Stamina"] = 86, ["Gold"] = 18 },
                ["Settings"] = new Dictionary<string, object> { ["Volume"] = 65, ["Music"] = true },
                ["Items"] = new List<object>
                {
                    Entry("beans", "Baked beans", "Restores 20 stamina", 2, "Eat"),
                    Entry("bandage", "Clean bandage", "Restores 20 health", 2, "Use"),
                    Entry("wood", "Dry wood", "Crafting material", 3, "Material"),
                    Entry("stone", "River stone", "Crafting material", 2, "Material"),
                },
                ["View"] = new Dictionary<string, object>
                {
                    ["Message"] = "A little warmth against a very big night.",
                    ["Objective"] = "Build a fire before sundown.",
                    ["Clock"] = "17:40",
                    ["CampBuilt"] = false,
                },
            };
            Derive();
        }

        private static Dictionary<string, object> Entry(string id, string name, string detail, int count, string action)
        {
            return new Dictionary<string, object> { ["Id"] = id, ["Name"] = name, ["Detail"] = detail, ["Count"] = count, ["Action"] = action };
        }

        public Dictionary<string, object> Player => (Dictionary<string, object>)Model["Player"];
        public Dictionary<string, object> Settings => (Dictionary<string, object>)Model["Settings"];
        public Dictionary<string, object> View => (Dictionary<string, object>)Model["View"];
        public List<object> Items => (List<object>)Model["Items"];

        public Dictionary<string, object> Item(string key)
        {
            foreach (object o in Items)
            {
                var entry = (Dictionary<string, object>)o;
                if ((string)entry["Id"] == key) return entry;
            }
            return null;
        }

        public int Count(string key)
        {
            Dictionary<string, object> entry = Item(key);
            return entry == null ? 0 : (int)entry["Count"];
        }

        private void Add(string key, int amount)
        {
            Dictionary<string, object> entry = Item(key);
            if (entry == null)
            {
                if (key == "campfire") entry = Entry(key, "Campfire kit", "Establish your camp", 0, "Place");
                else if (key == "wood") entry = Entry(key, "Dry wood", "Crafting material", 0, "Material");
                else return;
                Items.Add(entry);
            }
            entry["Count"] = (int)entry["Count"] + amount;
        }

        public void Derive()
        {
            View["CraftDisabled"] = Count("wood") < 4 || Count("stone") < 2 || (bool)View["CampBuilt"] || Count("campfire") > 0;
            View["Wood"] = Count("wood");
            View["Stone"] = Count("stone");
            View["Stacks"] = Items.Count;
            foreach (object o in Items)
            {
                var entry = (Dictionary<string, object>)o;
                string id = (string)entry["Id"];
                entry["Icon"] = "assets/" + id + ".svg";
                entry["Disabled"] = id == "wood" || id == "stone" ||
                                    (id == "beans" && (int)Player["Stamina"] >= 100) ||
                                    (id == "bandage" && (int)Player["Health"] >= 100);
            }
        }

        public void Publish()
        {
            Derive();
            Changed?.Invoke();
        }

        public void Forage()
        {
            Add("wood", 3);
            Player["Stamina"] = Math.Max(0, (int)Player["Stamina"] - 8);
            Player["Gold"] = (int)Player["Gold"] + 1;
            View["Message"] = "Found 3 dry wood and a silver dollar.";
            Publish();
        }

        public void TakeDamage()
        {
            Player["Health"] = Math.Max(0, (int)Player["Health"] - 15);
            View["Message"] = "A thorny trail. Lost 15 health.";
            Publish();
        }

        public void Craft()
        {
            if ((bool)View["CraftDisabled"]) return;
            Item("wood")["Count"] = (int)Item("wood")["Count"] - 4;
            Item("stone")["Count"] = (int)Item("stone")["Count"] - 2;
            RemoveEmptyStacks();
            Add("campfire", 1);
            View["Message"] = "Campfire kit crafted. Place it from your satchel.";
            Publish();
        }

        public void UseItem(string key)
        {
            Dictionary<string, object> entry = Item(key);
            if (entry == null || (bool)entry["Disabled"]) return;
            switch (key)
            {
                case "beans": Player["Stamina"] = Math.Min(100, (int)Player["Stamina"] + 20); break;
                case "bandage": Player["Health"] = Math.Min(100, (int)Player["Health"] + 20); break;
                case "campfire":
                    View["CampBuilt"] = true;
                    View["Objective"] = "Camp established. Rest easy, traveler.";
                    break;
                default: return;
            }
            entry["Count"] = (int)entry["Count"] - 1;
            View["Message"] = "Used " + ((string)entry["Name"]).ToLowerInvariant() + ".";
            RemoveEmptyStacks();
            Publish();
        }

        public void SortItems()
        {
            Items.Reverse();
            View["Message"] = "Satchel reordered.";
            Publish();
        }

        public void Tick()
        {
            ElapsedSeconds++;
            int minutes = 17 * 60 + 40 + ElapsedSeconds;
            View["Clock"] = (minutes / 60 % 24).ToString("00") + ":" + (minutes % 60).ToString("00");
            Publish();
        }

        private void RemoveEmptyStacks()
        {
            Items.RemoveAll(o => (int)((Dictionary<string, object>)o)["Count"] <= 0);
        }
    }

    /// <summary>The handlers camp.html names (on-click="forage" and friends), as game.gd has them.</summary>
    public sealed class FrontierCampController
    {
        public readonly FrontierCampState State;
        private readonly Weva.Native.WevaNativeDocument _ui;
        public bool SettingsOpen;
        public int SettingsApplied;

        public FrontierCampController(FrontierCampState state, Weva.Native.WevaNativeDocument ui)
        {
            State = state;
            _ui = ui;
            ui.Bind(state.Model, this);
            state.Changed += ui.RequestRefresh;
            ui.DataChanged += OnDataChanged;
            ApplySettings();
        }

        public void forage(string id) => State.Forage();
        public void craft(string id) => State.Craft();
        public void sort_items(string id) => State.SortItems();

        public void use_item(string id)
        {
            uint element = _ui.Document.Query("#" + id);
            if (_ui.TryGetRow(element, out _, out string key)) State.UseItem(key);
        }

        public void open_settings(string id)
        {
            SettingsOpen = true;
            _ui.Document.ShowDialog(_ui.Document.Query("#settings"), true);
            _ui.Document.SetFocus("#player-name");
        }

        public void close_settings(string id)
        {
            SettingsOpen = false;
            _ui.Document.CloseDialog(_ui.Document.Query("#settings"));
            _ui.Document.SetFocus("#settings-button");
        }

        private void OnDataChanged(string path, string text)
        {
            if (path == "Settings.Volume" || path == "Settings.Music") ApplySettings();
        }

        private void ApplySettings()
        {
            SettingsApplied++;
        }
    }
}
