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

        public sealed class CollectionController
        {
            [UIBind] public List<Quest> Quests = new List<Quest> { new Quest { Id = "wood", Title = "Gather wood" } };
            [UIBind] public Dictionary<string, object> Settings = new Dictionary<string, object>
            {
                ["audio"] = new Settings(),
                ["levels"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["volume"] = 65 } },
            };
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

        [TestCase(false)]
        [TestCase(true)]
        public void HiddenPreparation_RetainsDocumentAndUsesCurrentController(bool versioned)
        {
            Controller controller = versioned ? new VersionedController() : new Controller();
            _host.SetController(controller);
            _host.Prepare();
            var document = _host.Document;
            int generation = _host.Generation;
            Assert.That(_host.Query("#name").Focus(), Is.True);
            _host.Visible = false;
            Assert.That(_host.FocusedElement.IsValid, Is.False);
#if WEVA_INPUTSYSTEM
            Assert.That(_host.Input, Is.Null, "hidden documents must not register an input feed");
            Assert.That(_host.TickInput(), Is.False);
#endif
            Assert.That(_host.InputConsumed, Is.False);
            ulong serial = document.DrawSerial;
            controller.PlayerName = "Prepared while hidden";
            if (controller is VersionedController changed) changed.Bump();
            _go.SendMessage("Update");
            Assert.That(document.DrawSerial, Is.EqualTo(serial), "automatic hidden updates defer paint");
            _host.Prepare();
            Assert.That(_host.Visible, Is.False);
            Assert.That(_host.Query("#title").Text, Is.EqualTo("Prepared while hidden"));
            Assert.That(_host.Document, Is.SameAs(document));
            Assert.That(_host.Generation, Is.EqualTo(generation));
            serial = document.DrawSerial;
            _host.Prepare();
            Assert.That(document.DrawSerial, Is.EqualTo(serial), "idle preparation reuses paint");
            _host.Visible = true;
            _host.Prepare();
            Assert.That(document.DrawSerial, Is.EqualTo(serial), "showing does not rebuild unchanged paint");
            Assert.That(_host.Query("#name").Focus(), Is.True);
            _host.enabled = false;
            Assert.That(_host.Document, Is.Null, "disable still releases the document");
        }

        [Test]
        public void EnablingConsumesThePendingInspectorReload()
        {
            _go.SetActive(false);
            _host.InlineHtml = "<p id=current>Latest settings</p>";
            typeof(WevaDocument).GetMethod("OnValidate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(_host, null);
            _go.SetActive(true);
            int generation = _host.Generation;
            _host.Prepare();
            _go.SendMessage("Update");
            Assert.That(_host.Generation, Is.EqualTo(generation), "Create already applied the pending settings");
            Assert.That(_host.Query("#current").Text, Is.EqualTo("Latest settings"));
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

        [TestCase("Quests.0.Title", "Cut wood")]
        [TestCase("Settings.audio.Volume", "80")]
        [TestCase("Settings.levels.0.volume", "90")]
        public void DataModel_WritesThroughNestedCollections(string path, string value)
        {
            var controller = new CollectionController();
            _host.InlineHtml = "<input id='edit' data-model='" + path + "'><p id='mirror'>{{ " + path + " }}</p>";
            _host.Reload();
            _host.SetController(controller);
            _host.Step();
            var resolver = new UIBindResolver(controller);
            System.Type originalType = resolver.Resolve(path).GetType();
            _host.Query("#edit").Focus();
            _host.Document.SelectAll();
            _host.Document.TryTextInput(value);
            _host.Step();
            Assert.That(NativeBindings.Format(resolver.Resolve(path)), Is.EqualTo(value));
            Assert.That(resolver.Resolve(path).GetType(), Is.EqualTo(originalType));
            Assert.That(Text("#mirror"), Is.EqualTo(value));
        }

        [Test]
        public void DictionaryBinding_SurvivesBindingWhileDisabled_AndReenable()
        {
            _go.SetActive(false);
            _host.InlineHtml = "<p id='label'>{{ Name }}</p><input id='edit' data-model='Name'>";
            var model = new Dictionary<string, object> { ["Name"] = "Morgan" };
            _host.Bind(model);
            _go.SetActive(true);
            _host.Step();
            Assert.That(Text("#label"), Is.EqualTo("Morgan"));
            Assert.That(_host.Data, Is.SameAs(model));
            _go.SetActive(false);
            model["Name"] = "Sam";
            _go.SetActive(true);
            _host.Step();
            Assert.That(Text("#label"), Is.EqualTo("Sam"));
            _host.Query("#edit").Focus();
            _host.Document.SelectAll();
            _host.Document.TryTextInput("Alex");
            _host.Step();
            Assert.That(model["Name"], Is.EqualTo("Alex"));
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
