// Data binding through the shared core from C#: {{ path }} text and
// attributes, data-class, data-each rows with keys, data-model controls in
// both directions with the model's types kept, and controller dispatch by
// handler name through WevaDocument.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeBindingTests
    {
        private const string Html =
            "<body><h1 id=\"title\" data-class-hurt=\"Player.Hurt\">{{ Player.Name }}</h1>" +
            "<p id=\"hp\">HP {{ Player.Health }}/100</p>" +
            "<ul id=\"quests\"><template data-each=\"Quests as quest\" data-key=\"Id\"><li id=\"quest-{{ quest.Id }}\" data-class-done=\"quest.Done\">{{ $index }}. {{ quest.Title }}</li></template></ul>" +
            "<input id=\"name\" data-model=\"Player.Name\"><input id=\"volume\" type=\"range\" min=\"0\" max=\"100\" data-model=\"Settings.Volume\">" +
            "<input id=\"music\" type=\"checkbox\" data-model=\"Settings.Music\">" +
            "<button id=\"go\" on-click=\"Go\">Go</button></body>";

        private static Dictionary<string, object> Model()
        {
            return new Dictionary<string, object>
            {
                ["Player"] = new Dictionary<string, object> { ["Name"] = "Morgan", ["Health"] = 72, ["Hurt"] = false },
                ["Settings"] = new Dictionary<string, object> { ["Volume"] = 65, ["Music"] = true },
                ["Quests"] = new List<object>
                {
                    new Dictionary<string, object> { ["Id"] = "wood", ["Title"] = "Gather wood", ["Done"] = false },
                    new Dictionary<string, object> { ["Id"] = "fire", ["Title"] = "Light a fire", ["Done"] = true },
                },
            };
        }

        private NativeDocument _doc;
        private NativeBindings _bindings;
        private Dictionary<string, object> _model;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 400);
            _doc.LoadHtml(Html);
            _doc.SetCss("body{margin:0}");
            _model = Model();
            _bindings = new NativeBindings(_model);
            _bindings.Install(_doc);
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _bindings.Dispose();
            _doc.Dispose();
        }

        private string Text(string selector) => _doc.ElementText(_doc.Query(selector));
        private string Value(string selector) => _doc.ElementValue(_doc.Query(selector));

        [Test]
        public void Text_Attributes_AndClasses_FillFromTheModel()
        {
            Assert.That(Text("#title"), Is.EqualTo("Morgan"));
            Assert.That(Text("#hp"), Is.EqualTo("HP 72/100"));
            Assert.That(_doc.Query("#title.hurt"), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "data-class off for false");
            ((Dictionary<string, object>)_model["Player"])["Hurt"] = true;
            ((Dictionary<string, object>)_model["Player"])["Health"] = 57;
            int changed = _bindings.Refresh();
            _doc.Update(0);
            Assert.That(changed, Is.GreaterThan(0), "the refresh reports what it changed");
            Assert.That(_doc.Query("#title.hurt"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "data-class on for true");
            Assert.That(Text("#hp"), Is.EqualTo("HP 57/100"));
            Assert.That(_bindings.Refresh(), Is.EqualTo(0), "a refresh with nothing moved changes nothing");
        }

        [Test]
        public void DataEach_MakesKeyedRows_AndFollowsTheList()
        {
            uint[] rows = _doc.QueryAll("#quests > li");
            Assert.That(rows.Length, Is.EqualTo(2));
            Assert.That(Text("#quest-wood"), Is.EqualTo("0. Gather wood"));
            Assert.That(Text("#quest-fire"), Is.EqualTo("1. Light a fire"));
            Assert.That(_doc.Query("#quest-fire.done"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE));
            Assert.That(_doc.TryGetRow(_doc.Query("#quest-fire"), out int index, out string key));
            Assert.That(index, Is.EqualTo(1));
            Assert.That(key, Is.EqualTo("fire"));
            Assert.That(_doc.TryGetRow(_doc.Query("#title"), out _, out _), Is.False, "outside a row there is no row");
            Assert.That(_doc.ModelPath(_doc.Query("#quest-fire"), "quest.Done"), Is.EqualTo("Quests.1.Done"), "the alias unwinds to the list path");

            ((List<object>)_model["Quests"]).Add(new Dictionary<string, object> { ["Id"] = "rest", ["Title"] = "Rest", ["Done"] = false });
            _bindings.Refresh();
            _doc.Update(0);
            Assert.That(_doc.QueryAll("#quests > li").Length, Is.EqualTo(3));
            Assert.That(Text("#quest-rest"), Is.EqualTo("2. Rest"));
            ((List<object>)_model["Quests"]).RemoveAt(0);
            _bindings.Refresh();
            _doc.Update(0);
            Assert.That(_doc.QueryAll("#quests > li").Length, Is.EqualTo(2));
            Assert.That(Text("#quest-fire"), Is.EqualTo("0. Light a fire"), "a kept row is refilled in place with its new index");
        }

        [Test]
        public void DataModel_FlowsIntoControls_AndBackWithTheModelsTypes()
        {
            Assert.That(Value("#name"), Is.EqualTo("Morgan"), "a text model fills its field");
            Assert.That(Value("#volume"), Is.EqualTo("65"), "a number fills its slider");
            Assert.That(Value("#music"), Is.EqualTo("on"), "true checks its box");

            var changes = new List<(string, string)>();
            _bindings.DataChanged += (path, text) => changes.Add((path, text));

            // Control -> data: the user drags the slider and toggles the box.
            _doc.SetFocus("#volume");
            _doc.Key(weva_key.WEVA_KEY_RIGHT, true);
            _doc.Key(weva_key.WEVA_KEY_RIGHT, false);
            _doc.Update(0);
            var events = new List<NativeEvent>();
            _doc.PollEvents(events);
            foreach (NativeEvent e in events)
            {
                if (e.Kind == weva_event_kind.WEVA_EVENT_VALUE_CHANGED || e.Kind == weva_event_kind.WEVA_EVENT_CHANGE) _bindings.WriteBack(e.Target);
            }
            Assert.That(((Dictionary<string, object>)_model["Settings"])["Volume"], Is.EqualTo(66), "the model keeps its int");
            Assert.That(changes, Does.Contain(("Settings.Volume", "66")));
            int count = changes.Count;
            Assert.That(_bindings.WriteBack(_doc.Query("#volume")), Is.False, "the same value again is not a change");
            Assert.That(changes.Count, Is.EqualTo(count));

            _doc.SetFocus("#music");
            _doc.Key(weva_key.WEVA_KEY_SPACE, true);
            _doc.Key(weva_key.WEVA_KEY_SPACE, false);
            _doc.Update(0);
            events.Clear();
            _doc.PollEvents(events);
            foreach (NativeEvent e in events)
            {
                if (e.Kind == weva_event_kind.WEVA_EVENT_VALUE_CHANGED || e.Kind == weva_event_kind.WEVA_EVENT_CHANGE) _bindings.WriteBack(e.Target);
            }
            Assert.That(((Dictionary<string, object>)_model["Settings"])["Music"], Is.EqualTo(false), "the model keeps its bool");

            // Data -> control, only where they disagree.
            ((Dictionary<string, object>)_model["Player"])["Name"] = "Jessie";
            _bindings.Refresh();
            _doc.Update(0);
            Assert.That(Value("#name"), Is.EqualTo("Jessie"));
            Assert.That(Text("#title"), Is.EqualTo("Jessie"));
        }

        [Test]
        public void Resolver_ServesPathsBeforeTheDictionary_AndUnknownOnesFallThrough()
        {
            _bindings.Resolver = path => path == "Player.Name" ? "Resolver" : path == "Quests" ? (object)new List<object>() : null;
            _bindings.Refresh();
            _doc.Update(0);
            Assert.That(Text("#title"), Is.EqualTo("Resolver"));
            Assert.That(Text("#hp"), Is.EqualTo("HP 72/100"), "a path the resolver does not know reads from the dictionary");
            Assert.That(_doc.QueryAll("#quests > li").Length, Is.EqualTo(0), "an empty list makes no rows");
        }

        [Test]
        public void Format_BoolsAndNumbersReadTheWayBindingsExpect()
        {
            Assert.That(NativeBindings.Format(true), Is.EqualTo("true"));
            Assert.That(NativeBindings.Format(0.5), Is.EqualTo("0.5"));
            Assert.That(NativeBindings.Format(65.0), Is.EqualTo("65"));
            Assert.That(NativeBindings.Format(1234567), Is.EqualTo("1234567"));
            Assert.That(NativeBindings.Format(null), Is.EqualTo(""));
        }

        public sealed class Controller
        {
            public readonly List<string> Calls = new List<string>();
            public void Go(string id) => Calls.Add("Go:" + id);
            public void Other() => Calls.Add("Other");
        }

        [Test]
        public void WevaDocument_DispatchesHandlersToTheController_AndWritesDataBack()
        {
            var go = new GameObject("native-doc-under-test");
            try
            {
                var host = go.AddComponent<WevaDocument>();
                host.AutoInput = false;
                host.InlineHtml = Html;
                host.InlineCss = "body{margin:0}";
                host.Reload();
                Assume.That(host.Document, Is.Not.Null, host.LastError);
                var controller = new Controller();
                var model = Model();
                host.Bind(model, controller);
                host.Document.Update(0);
                Assert.That(host.Document.ElementText(host.Document.Query("#title")), Is.EqualTo("Morgan"));

                var invoked = new List<string>();
                host.HandlerInvoked += (handler, id) => invoked.Add(handler + "@" + id);
                var data = new List<string>();
                host.DataChanged += (path, text) => data.Add(path + "=" + text);

                host.Document.SetFocus("#go");
                host.Document.Key(weva_key.WEVA_KEY_ENTER, true);
                host.Document.Update(0);
                host.PumpEvents();
                host.Document.Key(weva_key.WEVA_KEY_ENTER, false);
                host.Document.Update(0);
                host.PumpEvents();
                Assert.That(controller.Calls, Is.EqualTo(new[] { "Go:go" }), "on-click=\"Go\" called Controller.Go(id) once");
                Assert.That(invoked, Is.EqualTo(new[] { "Go@go" }));

                host.Document.SetFocus("#volume");
                host.Document.Key(weva_key.WEVA_KEY_END, true);
                host.Document.Key(weva_key.WEVA_KEY_END, false);
                host.Document.Update(0);
                host.PumpEvents();
                Assert.That(((Dictionary<string, object>)model["Settings"])["Volume"], Is.EqualTo(100));
                Assert.That(data, Does.Contain("Settings.Volume=100"));

                ((Dictionary<string, object>)model["Player"])["Name"] = "Sam";
                host.RequestRefresh();
                host.PumpEvents();   // an update's pump flushes the pending refresh
                host.Document.Update(0);
                Assert.That(host.Document.ElementText(host.Document.Query("#title")), Is.EqualTo("Sam"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
