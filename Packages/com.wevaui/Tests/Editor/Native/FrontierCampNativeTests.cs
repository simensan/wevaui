// The Frontier Camp sample driven through the core in Unity: the same
// camp.html and camp.css the Godot example ships, the game logic ported to
// FrontierCampState, the handlers camp.html names dispatched to
// FrontierCampController, and data-model controls writing back. This is
// the gate of step 6 of the shared-core plan.
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class FrontierCampNativeTests
    {
        private const string UiDir = "examples/frontier_camp/ui";
        private GameObject _go;
        private WevaDocument _ui;
        private FrontierCampState _state;
        private FrontierCampController _controller;

        [SetUp]
        public void Open()
        {
            Assume.That(File.Exists(Path.Combine(UiDir, "camp.html")), "the Godot example's markup is in the repository");
            _go = new GameObject("frontier-camp-native");
            _ui = _go.AddComponent<WevaDocument>();
            _ui.AutoInput = false;
            _ui.BasePath = Path.GetFullPath(UiDir);
            _ui.InlineHtml = File.ReadAllText(Path.Combine(UiDir, "camp.html"));
            _ui.InlineCss = File.ReadAllText(Path.Combine(UiDir, "camp.css"));
            _ui.Reload();
            Assume.That(_ui.Document, Is.Not.Null, _ui.LastError);
            _ui.Document.SetViewport(1280, 720);
            _state = new FrontierCampState();
            _controller = new FrontierCampController(_state, _ui);
            Pump();
        }

        [TearDown]
        public void Close()
        {
            Object.DestroyImmediate(_go);
        }

        private NativeDocument Doc => _ui.Document;
        private string Text(string selector) => Doc.ElementText(Doc.Query(selector));
        private bool Has(string selector) => Doc.Query(selector) != WevaNative.WEVA_ELEMENT_NONE;

        private void Pump()
        {
            Doc.Update(0);
            _ui.PumpEvents();
            Doc.Update(0);
        }

        private void Click(string selector)
        {
            Assert.That(Doc.TryGetBounds(Doc.Query(selector), out NativeBounds b), selector + " exists");
            double x = b.X + b.Width / 2, y = b.Y + b.Height / 2;
            Doc.SetPointer(x, y, 0);
            Pump();
            Doc.SetPointer(x, y, (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY);
            Pump();
            Doc.SetPointer(x, y, 0);
            Pump();
        }

        [Test]
        public void Bindings_FillTheCampScreen()
        {
            Assert.That(Text("#clock"), Is.EqualTo("17:40"));
            Assert.That(Doc.QueryAll("#inventory > .item").Length, Is.EqualTo(4), "one row per satchel item");
            Assert.That(Has("#item-beans") && Has("#item-bandage") && Has("#item-wood") && Has("#item-stone"), "rows carry the item ids");
            Assert.That(Text("#wood-cost"), Does.Contain("3"), "View.Wood is derived from the items");
            Assert.That(Text("#stack-count"), Does.Contain("4"), "View.Stacks counts the rows");
            // disabled="{{ item.Disabled }}" binds the attribute; :disabled is the
            // state the core derives from it, whatever spelling it keeps.
            Assert.That(Has("#craft:disabled"), "crafting is disabled without four wood (disabled=" + Doc.ElementAttribute(Doc.Query("#craft"), "disabled") + ")");
            Assert.That(Has("#use-wood:disabled"), "a material cannot be used");
            Assert.That(Has("#use-beans:enabled"), "beans can be eaten");
            Assert.That(Text("#message"), Does.Contain("warmth"));
            Assert.That(Doc.ElementValue(Doc.Query("#volume")), Is.EqualTo("65"), "the settings slider reads Settings.Volume");
            Assert.That(Doc.ElementValue(Doc.Query("#music")), Is.EqualTo("on"));
            Assert.That(Doc.ElementValue(Doc.Query("#player-name")), Is.EqualTo("Morgan"));
        }

        [Test]
        public void Forage_ThenCraft_ThenPlaceTheCampfire()
        {
            Click("#forage");
            Assert.That(_state.Count("wood"), Is.EqualTo(6), "on-click=\"forage\" reached the controller once");
            Assert.That((int)_state.Player["Gold"], Is.EqualTo(19));
            Assert.That(Text("#wood-cost"), Does.Contain("6"), "the refresh the state requested ran on the next pump");
            Assert.That(Text("#message"), Does.Contain("silver dollar"));
            Assert.That(Has("#craft:enabled"), "crafting is enabled with the wood (disabled=" + Doc.ElementAttribute(Doc.Query("#craft"), "disabled") + ")");

            Click("#craft");
            Assert.That(_state.Count("campfire"), Is.EqualTo(1));
            Assert.That(_state.Count("wood"), Is.EqualTo(2));
            Assert.That(Has("#item-campfire"), "the new item got a row");
            Assert.That(Has("#item-stone"), Is.False, "the stones were used up and their row removed");
            Assert.That(Doc.QueryAll("#inventory > .item").Length, Is.EqualTo(4), "beans, bandage, wood, campfire");
            Assert.That(Has("#craft:disabled"), "one kit at a time");

            Click("#use-campfire");
            Assert.That((bool)_state.View["CampBuilt"], "use_item found its row's key through the core");
            Assert.That(Has("#item-campfire"), Is.False, "the used kit's row is gone");
            Assert.That(Text("#objective"), Does.Contain("established"));
        }

        [Test]
        public void DisabledButton_DoesNotReachTheController()
        {
            int wood = _state.Count("wood");
            Click("#craft");
            Assert.That(_state.Count("wood"), Is.EqualTo(wood), "a disabled craft button does nothing");
            Assert.That(_state.Count("campfire"), Is.EqualTo(0));
        }

        [Test]
        public void Settings_DialogEditsWriteBackThroughDataModel()
        {
            int applied = _controller.SettingsApplied;
            Click("#settings-button");
            Assert.That(_controller.SettingsOpen);
            Assert.That(Has("#settings[open]"), "open_settings showed the dialog");
            Assert.That(Doc.ElementId(Doc.Focus), Is.EqualTo("player-name"));

            Doc.Key(weva_key.WEVA_KEY_END, true);
            Doc.Key(weva_key.WEVA_KEY_END, false);
            Doc.TryTextInput("!");
            Pump();
            Assert.That((string)_state.Player["Name"], Is.EqualTo("Morgan!"), "typing writes Player.Name back");
            Assert.That(Text("#player-label"), Does.Contain("Morgan!"), "and the HUD shows it after the refresh");

            Doc.SetFocus("#volume");
            Doc.Key(weva_key.WEVA_KEY_HOME, true);
            Doc.Key(weva_key.WEVA_KEY_HOME, false);
            Pump();
            Assert.That(_state.Settings["Volume"], Is.EqualTo(0), "the slider writes an int back");
            Assert.That(_controller.SettingsApplied, Is.GreaterThan(applied), "the game reacted to Settings.Volume");

            Doc.SetFocus("#music");
            Doc.Key(weva_key.WEVA_KEY_SPACE, true);
            Doc.Key(weva_key.WEVA_KEY_SPACE, false);
            Pump();
            Assert.That(_state.Settings["Music"], Is.EqualTo(false), "the box writes a bool back");

            Click("#close-settings");
            Assert.That(_controller.SettingsOpen, Is.False);
            Assert.That(Has("#settings[open]"), Is.False, "close_settings closed the dialog");
            Assert.That(Doc.ElementId(Doc.Focus), Is.EqualTo("settings-button"));
        }

        [Test]
        public void Clock_TicksIntoTheMasthead()
        {
            _state.Tick();
            Pump();
            Assert.That(Text("#clock"), Is.EqualTo("17:41"));
        }
    }
}
