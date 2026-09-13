// The supported element API: WevaDocument.Query / QueryAll / FocusedElement
// return WevaElement values that read and write the core through the
// component, go stale on Reload instead of dangling, and never throw on a
// missing element. Plus the component's other public entry points that used
// to need Weva.Native: events as WevaEvent, the input knobs, the asset
// reader, a game-registered font family, safe-area insets, the cursor.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class WevaElementTests
    {
        private const string Html =
            "<body><h1 id=\"title\" class=\"big loud\">Hello</h1>" +
            "<input id=\"name\" value=\"Morgan\">" +
            "<button id=\"go\" on-click=\"Go\">Go</button>" +
            "<div id=\"list\" style=\"height:100px;overflow:auto\"><div id=\"tall\" style=\"height:1000px\">tall</div></div>" +
            "<ul id=\"quests\"><template data-each=\"Quests as quest\" data-key=\"Id\"><li id=\"quest-{{ quest.Id }}\">{{ quest.Title }}</li></template></ul>" +
            "<dialog id=\"dlg\"><p>Hi</p></dialog>" +
            "<p id=\"font\" style=\"font-family:GameFont\">Quick brown fox jumps</p><p id=\"plain\">Quick brown fox jumps</p>" +
            "<img id=\"img\" src=\"badge.png\"></body>";

        private GameObject _go;
        private WevaDocument _host;

        [SetUp]
        public void Open()
        {
            _go = new GameObject("element-api-under-test");
            _go.SetActive(false);
            _host = _go.AddComponent<WevaDocument>();
            _host.AutoInput = false;
            _host.InlineHtml = Html;
            _host.InlineCss = "body{margin:0}";
        }

        private void Enable()
        {
            _go.SetActive(true);
            Assume.That(_host.Document, Is.Not.Null, _host.LastError);
            _host.Step();
        }

        [TearDown]
        public void Close()
        {
            UnityEngine.Object.DestroyImmediate(_go);
        }

        [Test]
        public void Query_FindsElements_AndNoneIsHarmless()
        {
            Enable();
            WevaElement title = _host.Query("#title");
            Assert.That(title.IsValid);
            Assert.That(title.Id, Is.EqualTo("title"));
            Assert.That(title.Tag, Is.EqualTo("h1"));
            Assert.That(title.Text, Is.EqualTo("Hello"));
            Assert.That(title.HasClass("loud"), Is.True);
            Assert.That(title.HasClass("lou"), Is.False);
            Assert.That(title.ToString(), Is.EqualTo("<h1#title>"));
            Assert.That(title.Parent.Tag, Is.EqualTo("body"));
            Assert.That(_host.Query("body").Children.Length, Is.GreaterThan(5));

            WevaElement none = _host.Query("#nope");
            Assert.That(none.IsValid, Is.False);
            Assert.That(none, Is.EqualTo(WevaElement.None));
            Assert.That(none.Id, Is.EqualTo(""));
            Assert.That(none.Text, Is.EqualTo(""));
            Assert.That(none.Value, Is.EqualTo(""));
            Assert.That(none.Bounds, Is.EqualTo(Rect.zero));
            Assert.That(none.SetAttribute("x", "1"), Is.False);
            Assert.That(none.Focus(), Is.False);
            Assert.That(none.ScrollTo(1, 1), Is.False);
            Assert.That(none.ShowDialog(), Is.False);
            Assert.That(none.Parent.IsValid, Is.False);
            Assert.That(none.Children, Is.Empty);
            Assert.That(none.ToString(), Is.EqualTo("(none)"));
            Assert.That(_host.Query(""), Is.EqualTo(WevaElement.None));
            Assert.That(_host.QueryAll(".nothing"), Is.Empty);
            Assert.That(_host.QueryAll(""), Is.Empty);
        }

        [Test]
        public void Value_Attributes_AndBounds_ReadAndWriteTheCore()
        {
            Enable();
            WevaElement name = _host.Query("#name");
            Assert.That(name.Value, Is.EqualTo("Morgan"));
            name.Value = "Sam";
            Assert.That(name.Value, Is.EqualTo("Sam"));
            Assert.That(name.HasAttribute("data-state"), Is.False);
            Assert.That(name.SetAttribute("data-state", "locked"), Is.True);
            Assert.That(name.Attribute("data-state"), Is.EqualTo("locked"));
            Assert.That(name.HasAttribute("data-state"), Is.True);
            Assert.That(_host.Query("[data-state=locked]"), Is.EqualTo(name), "the attribute is queryable at once");

            _host.Step();
            Rect bounds = _host.Query("#tall").Bounds;
            Assert.That(bounds.height, Is.EqualTo(1000).Within(0.01));
            Assert.That(bounds.width, Is.GreaterThan(0));
        }

        [Test]
        public void Focus_AndFocusedElement()
        {
            Enable();
            WevaElement name = _host.Query("#name");
            Assert.That(_host.FocusedElement.IsValid, Is.False, "nothing focused at first");
            Assert.That(name.Focus(), Is.True);
            Assert.That(name.IsFocused, Is.True);
            Assert.That(_host.FocusedElement, Is.EqualTo(name));
            Assert.That(_host.Query("#go").Focus(), Is.True);
            Assert.That(_host.FocusedElement, Is.EqualTo(_host.Query("#go")));
            Assert.That(name.IsFocused, Is.False);
        }

        [Test]
        public void Scroll_ReadsAndSetsAContainer_AndIsZeroElsewhere()
        {
            Enable();
            WevaElement list = _host.Query("#list");
            Assert.That(list.Scroll, Is.EqualTo(Vector2.zero));
            Assert.That(list.MaxScroll.y, Is.EqualTo(900).Within(0.01));
            Assert.That(list.ScrollTo(0, 250), Is.True);
            _host.Step();
            Assert.That(list.Scroll.y, Is.EqualTo(250).Within(0.01));
            Assert.That(list.ScrollTo(new Vector2(0, 1e6f)), Is.True);
            _host.Step();
            Assert.That(list.Scroll.y, Is.EqualTo(900).Within(0.01), "clamped to the maximum");
            WevaElement title = _host.Query("#title");
            Assert.That(title.MaxScroll, Is.EqualTo(Vector2.zero), "a heading scrolls nowhere");
            title.ScrollTo(0, 10);
            _host.Step();
            Assert.That(title.Scroll, Is.EqualTo(Vector2.zero), "and a ScrollTo on it clamps to nothing");
        }

        public sealed class Quest { public string Id; public string Title; }
        public sealed class Controller
        {
            [Binding.UIBind] public List<Quest> Quests = new List<Quest> { new Quest { Id = "wood", Title = "Wood" }, new Quest { Id = "fire", Title = "Fire" } };
            public readonly List<string> Used = new List<string>();
            public void Go(string id) => Used.Add(id);
        }

        [Test]
        public void Rows_ReportTheDataEachRow()
        {
            Enable();
            _host.SetController(new Controller());
            _host.Step();
            WevaElement fire = _host.Query("#quest-fire");
            Assert.That(fire.IsValid);
            Assert.That(fire.TryGetRow(out int index, out string key), Is.True);
            Assert.That(index, Is.EqualTo(1));
            Assert.That(key, Is.EqualTo("fire"));
            Assert.That(fire.RowIndex, Is.EqualTo(1));
            Assert.That(fire.RowKey, Is.EqualTo("fire"));
            Assert.That(_host.Query("#title").RowIndex, Is.EqualTo(-1));
            Assert.That(_host.Query("#title").RowKey, Is.EqualTo(""));
            Assert.That(_host.QueryAll("#quests > li").Length, Is.EqualTo(2));
        }

        [Test]
        public void Dialog_OpensAndCloses_ThroughTheElement()
        {
            Enable();
            WevaElement dlg = _host.Query("#dlg");
            Assert.That(dlg.HasAttribute("open"), Is.False);
            Assert.That(dlg.ShowDialog(modal: true), Is.True);
            Assert.That(dlg.HasAttribute("open"), Is.True);
            Assert.That(dlg.CloseDialog(), Is.True);
            Assert.That(dlg.HasAttribute("open"), Is.False);
            Assert.That(_host.Query("#title").ShowDialog(), Is.False, "not a dialog");
        }

        [Test]
        public void Reload_StalesEveryHandle_AndQueryIssuesFreshOnes()
        {
            Enable();
            WevaElement before = _host.Query("#title");
            WevaElement same = _host.Query("#title");
            Assert.That(before, Is.EqualTo(same));
            Assert.That(before.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            _host.Reload();
            Assert.That(before.IsValid, Is.False, "a reload replaced the tree");
            Assert.That(before.Text, Is.EqualTo(""), "a stale element answers nothing rather than throwing");
            Assert.That(before.SetAttribute("x", "1"), Is.False);
            WevaElement after = _host.Query("#title");
            Assert.That(after.IsValid);
            Assert.That(after, Is.Not.EqualTo(before));
            _go.SetActive(false);
            Assert.That(after.IsValid, Is.False, "disabled: the core is gone");
            Assert.That(after.Text, Is.EqualTo(""));
        }

        [Test]
        public void Event_RaisesWevaEvents_WithTargetsAndHandlers()
        {
            Enable();
            var c = new Controller();
            _host.SetController(c);
            var seen = new List<WevaEvent>();
            _host.Event += e => seen.Add(e);
            WevaElement go = _host.Query("#go");
            go.Focus();
            _host.Document.Key(weva_key.WEVA_KEY_ENTER, true);
            _host.Step();
            _host.Document.Key(weva_key.WEVA_KEY_ENTER, false);
            _host.Step();
            WevaEvent click = seen.Find(e => e.Kind == WevaEventKind.Click);
            Assert.That(click.Kind, Is.EqualTo(WevaEventKind.Click), "a click arrived");
            Assert.That(click.Target, Is.EqualTo(go));
            Assert.That(click.Handler, Is.EqualTo("Go"));
            Assert.That(click.ToString(), Is.EqualTo("Click <button#go> on-Go"));
            Assert.That(seen.Exists(e => e.Kind == WevaEventKind.Focus && e.Target == go));
            Assert.That(c.Used, Is.EqualTo(new[] { "go" }));
        }

        [Test]
        public void EventKinds_MirrorTheAbiValueForValue()
        {
            foreach (weva_event_kind kind in Enum.GetValues(typeof(weva_event_kind)))
            {
                string expected = ToPascal(kind.ToString().Substring("WEVA_EVENT_".Length));
                Assert.That(Enum.IsDefined(typeof(WevaEventKind), (int)kind), kind + " has a WevaEventKind with its value");
                Assert.That(((WevaEventKind)(int)kind).ToString(), Is.EqualTo(expected), kind + " has the matching name");
            }
            Assert.That(Enum.GetValues(typeof(WevaEventKind)).Length, Is.EqualTo(Enum.GetValues(typeof(weva_event_kind)).Length), "no extra kinds on the C# side");
        }

        private static string ToPascal(string upperSnake)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string part in upperSnake.Split('_'))
            {
                if (part.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }

        [Test]
        public void SafeArea_AndCursor_AreOnTheComponent()
        {
            Enable();
            Assert.That(_host.Cursor, Is.EqualTo("default"));
            _host.SetSafeAreaInsets(44, 0, 0, 8);
            _host.InlineCss = "body{margin:0;padding-top:env(safe-area-inset-top)}h1{margin:0}";
            _host.Reload();
            _host.Step();
            Assert.That(_host.Query("#title").Bounds.y, Is.EqualTo(44).Within(0.01), "env() read the insets");
        }

        [Test]
        public void AssetReader_ServesTheCore_AndSurvivesEnable()
        {
            var asked = new List<string>();
            _host.AssetReader = path => { asked.Add(path); return null; };
            Enable();
            Assert.That(asked, Does.Contain("badge.png"), "the reader set before the core existed was installed");
            asked.Clear();
            _go.SetActive(false);
            _go.SetActive(true);
            _host.Step();
            Assert.That(asked, Does.Contain("badge.png"), "and again after a disable/enable");
        }

        [Test]
        public void RegisterFontFamily_NamesAUnityFont_AndSurvivesEnable()
        {
            var bold = Resources.Load<Font>("Fonts/Weva-Default-Bold");
            Assume.That(bold, Is.Not.Null);
            _host.RegisterFontFamily("GameFont", bold);
            Enable();
            // Both paragraphs fill the body, so compare the text runs: the
            // bold face is measurably wider than the default.
            Assert.That(TextWidth("#font"), Is.GreaterThan(TextWidth("#plain")), "GameFont resolved to the bold face, not the default");
            _go.SetActive(false);
            _go.SetActive(true);
            _host.Step();
            Assert.That(TextWidth("#font"), Is.GreaterThan(TextWidth("#plain")), "the family is registered again on enable");
            Assert.That(() => _host.RegisterFontFamily("", bold), Throws.ArgumentException);
            Assert.That(() => _host.RegisterFontFamily("X", null), Throws.ArgumentNullException);
        }

        // The widest text run under the element, from the core's box dump.
        private float TextWidth(string selector)
        {
            uint element = _host.Document.Query(selector);
            float widest = 0;
            foreach (weva_box box in _host.Document.Boxes())
            {
                if (box.kind != (uint)weva_box_kind.WEVA_BOX_TEXT || box.element == WevaNative.WEVA_ELEMENT_NONE) continue;
                if (box.element == element || _host.Document.ElementContains(element, box.element)) widest = Math.Max(widest, (float)box.width);
            }
            return widest;
        }

        [Test]
        public void InputKnobs_ReachTheFeed_BeforeAndAfterItExists()
        {
            _host.WrapTab = false;
            _host.GamepadTextEntry = true;
            _host.AcceptsKeyboard = false;
            Enable();
#if WEVA_INPUTSYSTEM
            NativeInputFeed feed = _host.Input;
            Assert.That(feed.WrapTab, Is.False);
            Assert.That(feed.GamepadTextEntry, Is.True);
            Assert.That(feed.AcceptsKeyboard, Is.False);
            _host.WrapTab = true;
            _host.AcceptsKeyboard = true;
            Assert.That(feed.WrapTab, Is.True);
            Assert.That(feed.AcceptsKeyboard, Is.True);
            var tabbed = new List<bool>();
            _host.TabbedOut += b => tabbed.Add(b);
            var entries = new List<string>();
            _host.TextEntryRequested += id => entries.Add(id);
            Assert.That(tabbed, Is.Empty);
            Assert.That(entries, Is.Empty);
#endif
        }
    }
}
