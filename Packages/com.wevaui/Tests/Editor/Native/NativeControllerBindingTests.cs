// The 1.0 controller contract on the core-backed document: a controller's
// [UIBind] fields and properties feed {{ }}, data-class, data-each and
// data-model, its public methods answer on-<event>, a plain controller is
// polled every frame and an IBindingVersion one only when it bumps -- the
// same rules the C# engine's WevaDocument applied, so a 0.1.x controller
// binds unchanged.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Binding;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeControllerBindingTests
    {
        private const string Html =
            "<body><h1 id=\"title\" data-class-hurt=\"IsHurt\">{{ PlayerName }}</h1>" +
            "<p id=\"hp\">HP {{ Stats.Health }}/{{ MaxHealth }}</p>" +
            "<p id=\"gold\">{{ FormattedGold }}</p>" +
            "<ul id=\"quests\"><template data-each=\"Quests as quest\" data-key=\"Id\"><li id=\"quest-{{ quest.Id }}\" data-class-done=\"quest.Done\">{{ $index }}. {{ quest.Title }}</li></template></ul>" +
            "<input id=\"name\" data-model=\"PlayerName\"><input id=\"volume\" type=\"range\" min=\"0\" max=\"100\" data-model=\"Settings.Volume\">" +
            "<input id=\"music\" type=\"checkbox\" data-model=\"Settings.Music\">" +
            "<p id=\"extra\">{{ Extra.Note }}</p>" +
            "<button id=\"go\" on-click=\"Go\">Go</button></body>";

        public sealed class Quest
        {
            public string Id;
            public string Title;
            public bool Done { get; set; }
        }

        public sealed class Stats
        {
            public int Health = 72;
        }

        public sealed class Settings
        {
            public int Volume = 65;
            public bool Music = true;
        }

        public class Controller
        {
            [UIBind] public string PlayerName = "Morgan";
            [UIBind] public Stats Stats = new Stats();
            [UIBind] public int MaxHealth => 100;
            [UIBind] public bool IsHurt => Stats.Health < 25;
            [UIBind] private int Gold = 1234;
            [UIBind] public string FormattedGold => Gold + "g";
            [UIBind] public List<Quest> Quests = new List<Quest>
            {
                new Quest { Id = "wood", Title = "Gather wood", Done = false },
                new Quest { Id = "fire", Title = "Light a fire", Done = true },
            };
            [UIBind] public Settings Settings = new Settings();
            public string NotBound = "hidden";
            public readonly List<string> Calls = new List<string>();
            public void Go(string id) => Calls.Add("Go:" + id);
            public void Hurt(int by) => Stats.Health -= by;
        }

        public sealed class VersionedController : Controller, IBindingVersion
        {
            public int BindingVersion { get; private set; }
            public void Bump() => BindingVersion++;
        }

        private GameObject _go;
        private WevaDocument _host;

        [SetUp]
        public void Open()
        {
            _go = new GameObject("native-controller-under-test");
            _host = _go.AddComponent<WevaDocument>();
            _host.AutoInput = false;
            _host.InlineHtml = Html;
            _host.InlineCss = "body{margin:0}";
            _host.Reload();
            Assume.That(_host.Document, Is.Not.Null, _host.LastError);
        }

        [TearDown]
        public void Close()
        {
            Object.DestroyImmediate(_go);
        }

        private string Text(string selector) => _host.Document.ElementText(_host.Document.Query(selector));
        private string Value(string selector) => _host.Document.ElementValue(_host.Document.Query(selector));
        private string Class(string selector) => _host.Document.ElementAttribute(_host.Document.Query(selector), "class");

        [Test]
        public void UIBindMembers_FeedText_Paths_Lists_AndClasses()
        {
            var c = new Controller();
            _host.SetController(c);
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Morgan"), "a [UIBind] field");
            Assert.That(Text("#hp"), Is.EqualTo("HP 72/100"), "a dotted path into a plain object and a computed property");
            Assert.That(Text("#gold"), Is.EqualTo("1234g"), "a computed property over a private [UIBind] field");
            Assert.That(Class("#title"), Does.Not.Contain("hurt"));
            uint[] rows = _host.Document.QueryAll("#quests > li");
            Assert.That(rows.Length, Is.EqualTo(2), "data-each over a List<T>");
            Assert.That(_host.Document.ElementText(rows[0]), Is.EqualTo("0. Gather wood"));
            Assert.That(_host.Document.ElementAttribute(rows[1], "class"), Does.Contain("done"), "a bool property on the item");
            Assert.That(Value("#name"), Is.EqualTo("Morgan"), "data-model reads the controller");
            Assert.That(Value("#volume"), Is.EqualTo("65"));
            Assert.That(Value("#music"), Is.EqualTo("on"));
            Assert.That(Text("#extra"), Is.EqualTo(""), "an unknown root shows nothing");
            Assert.That(_host.GetController<Controller>(), Is.SameAs(c));
            Assert.That(_host.GetController<VersionedController>(), Is.Null, "a failed cast is null, not a throw");
        }

        [Test]
        public void OnlyMembersMarkedUIBind_AreRoots()
        {
            _host.InlineHtml = "<body><p id=\"p\">{{ NotBound }}</p><p id=\"q\">{{ Calls }}</p></body>";
            _host.Reload();
            _host.SetController(new Controller());
            _host.Step();
            Assert.That(Text("#p"), Is.EqualTo(""), "a public field without [UIBind] is not reachable");
            Assert.That(Text("#q"), Is.EqualTo(""));
        }

        [Test]
        public void PlainController_IsPolledEveryFrame()
        {
            var c = new Controller();
            _host.SetController(c);
            _host.Step();
            c.PlayerName = "Sam";
            c.Hurt(60);
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Sam"), "mutating a [UIBind] field is enough");
            Assert.That(Text("#hp"), Is.EqualTo("HP 12/100"));
            Assert.That(Class("#title"), Does.Contain("hurt"), "a computed bool re-evaluates");
            c.Quests.Add(new Quest { Id = "cook", Title = "Cook" });
            _host.Step();
            Assert.That(_host.Document.QueryAll("#quests > li").Length, Is.EqualTo(3), "a list grows a row");
        }

        [Test]
        public void VersionedController_IsReadOnlyWhenItBumps()
        {
            var c = new VersionedController();
            _host.SetController(c);
            _host.Step();
            c.PlayerName = "Sam";
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Morgan"), "no bump: the frame skipped the read");
            c.Bump();
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Sam"), "a bump re-reads");
            c.PlayerName = "Alex";
            _host.RequestRefresh();
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Alex"), "RequestRefresh reads regardless of the version");
        }

        [Test]
        public void DataModel_WritesBackIntoTheController_KeepingTypes()
        {
            var c = new Controller();
            _host.SetController(c);
            _host.Step();
            var data = new List<string>();
            _host.DataChanged += (path, text) => data.Add(path + "=" + text);

            _host.Document.SetFocus("#volume");
            _host.Document.Key(weva_key.WEVA_KEY_END, true);
            _host.Document.Key(weva_key.WEVA_KEY_END, false);
            _host.Step();
            Assert.That(c.Settings.Volume, Is.EqualTo(100), "an int field stays an int");
            Assert.That(data, Does.Contain("Settings.Volume=100"));

            _host.Document.SetFocus("#music");
            _host.Document.Key(weva_key.WEVA_KEY_SPACE, true);
            _host.Document.Key(weva_key.WEVA_KEY_SPACE, false);
            _host.Step();
            Assert.That(c.Settings.Music, Is.False, "a bool field stays a bool");

            _host.Document.SetFocus("#name");
            _host.Document.Key(weva_key.WEVA_KEY_END, true);
            _host.Document.Key(weva_key.WEVA_KEY_END, false);
            _host.Document.TryTextInput("!");
            _host.Step();
            Assert.That(c.PlayerName, Is.EqualTo("Morgan!"), "a root [UIBind] string field takes the typed text");
            Assert.That(Text("#title"), Is.EqualTo("Morgan!"), "and everything bound to it follows on the same frame");
        }

        [Test]
        public void Handlers_DispatchToTheController_ThroughSetController()
        {
            var c = new Controller();
            _host.SetController(c);
            _host.Step();
            _host.Document.SetFocus("#go");
            _host.Document.Key(weva_key.WEVA_KEY_ENTER, true);
            _host.Step();
            _host.Document.Key(weva_key.WEVA_KEY_ENTER, false);
            _host.Step();
            Assert.That(c.Calls, Is.EqualTo(new[] { "Go:go" }));
        }

        [Test]
        public void ModelDictionary_AndController_BindSideBySide()
        {
            var model = new Dictionary<string, object> { ["Extra"] = new Dictionary<string, object> { ["Note"] = "from the dictionary" } };
            _host.Bind(model, new Controller());
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Morgan"), "the controller answers its roots");
            Assert.That(Text("#extra"), Is.EqualTo("from the dictionary"), "the dictionary answers the rest");
        }

        [Test]
        public void SetController_SurvivesReload_AndNullDetaches()
        {
            var c = new Controller();
            _host.SetController(c);
            _host.Reload();
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo("Morgan"), "a reload re-installs the controller on the new tree");
            _host.SetController(null);
            _host.Step();
            Assert.That(Text("#title"), Is.EqualTo(""), "detached: nothing answers");
            Assert.That(_host.GetController<Controller>(), Is.Null);
        }
    }
}
