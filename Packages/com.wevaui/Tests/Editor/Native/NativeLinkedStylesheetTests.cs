// <link rel="stylesheet"> on the core-backed WevaDocument: the core reads
// <style> and @import, the host fetches links -- from the TextAsset next to
// the document asset in the editor, from what the scene hook baked in a
// player, and from the core's asset reader under BasePath last. Inspector
// sheets come after the page's own, so a later declaration wins.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeLinkedStylesheetTests
    {
        private const string FixtureDir = "Packages/com.wevaui/Tests/Editor/Native/Fixtures";

        private GameObject _go;
        private WevaDocument _host;

        [SetUp]
        public void Open()
        {
            _go = new GameObject("linked-under-test");
            _go.SetActive(false);
            _host = _go.AddComponent<WevaDocument>();
            _host.AutoInput = false;
        }

        [TearDown]
        public void Close()
        {
            Object.DestroyImmediate(_go);
        }

        private double BoxWidth()
        {
            _host.Document.Update(0);
            Assert.That(_host.Document.TryGetBounds(_host.Document.Query("#box"), out NativeBounds b), "#box is in the tree");
            return b.Width;
        }

        [Test]
        public void LinkedHrefs_ReadsEveryStylesheetLink_InDocumentOrder()
        {
            var hrefs = WevaDocument.LinkedHrefs(
                "<link rel=\"stylesheet\" href=\"a.css\">" +
                "<LINK HREF='b.css' REL='stylesheet' />" +
                "<link rel=\"icon\" href=\"favicon.png\">" +
                "<link rel=\"preload stylesheet\" href=c.css>" +
                "<link href=\"nrel.css\">" +
                "<link rel=\"stylesheet\">");
            Assert.That(hrefs, Is.EqualTo(new[] { "a.css", "b.css", "c.css" }));
            Assert.That(WevaDocument.LinkedHrefs(null), Is.Empty);
            Assert.That(WevaDocument.LinkedHrefs("<body>no links</body>"), Is.Empty);
        }

        [Test]
        public void Editor_ResolvesTheLinkNextToTheDocumentAsset()
        {
            var html = AssetDatabase.LoadAssetAtPath<TextAsset>(FixtureDir + "/linked.html");
            Assume.That(html, Is.Not.Null, "the fixture imported");
            _host.DocumentAsset = html;
            _go.SetActive(true);
            Assume.That(_host.Document, Is.Not.Null, _host.LastError);
            Assert.That(_host.LinkedStylesheetHrefs, Is.EqualTo(new[] { "linked.css" }));
            Assert.That(BoxWidth(), Is.EqualTo(123).Within(0.01), "linked.css applied");
            Assert.That(_host.Document.BasePath, Is.EqualTo(FixtureDir), "url() and @import resolve next to the asset by default");
        }

        // Expectations recorded by check_linked_stylesheets_chrome.cjs. Link
        // discovery must share HTML parsing with the document, including inert
        // template contents, raw text, character references and real attributes.
        [TestCase("<!-- <link rel=stylesheet href=comment.css> -->", null)]
        [TestCase("<script>'<link rel=stylesheet href=script.css>'</script>", null)]
        [TestCase("<textarea><link rel=stylesheet href=textarea.css></textarea>", null)]
        [TestCase("<template><link rel=stylesheet href=template.css></template>", null)]
        [TestCase("<link rel=stylesheet data-href=data.css>", null)]
        [TestCase("<link data-rel=stylesheet href=data.css>", null)]
        [TestCase("<link title='a>b' rel=stylesheet href=real.css>", "real.css")]
        [TestCase("<link rel='style&#x73;heet' href='theme&amp;mode.css'>", "theme&mode.css")]
        [TestCase("<link rel='alternate\fSTYLESHEET' href=real.css>", "real.css")]
        [TestCase("<link rel=stylesheet href=first.css href=second.css>", "first.css")]
        public void LinkedHrefs_UsesTheParsedDocument(string html, string expected)
        {
            Assert.That(WevaDocument.LinkedHrefs(html),
                Is.EqualTo(expected == null ? new string[0] : new[] { expected }));
        }

        [Test]
        public void ReloadAndBake_UseTheSameParsedLinks()
        {
            _host.InlineHtml = "<!-- <link rel=stylesheet href=missing.css> -->" +
                "<template><link rel=stylesheet href=unused.css></template>" +
                "<link rel=stylesheet href='theme&amp;mode.css'><div id=box></div>";
            var requested = new List<string>();
            Assert.That(_host.BakeLinkedStylesheets(href =>
            {
                requested.Add(href);
                return "#box{width:73px}";
            }), Is.EqualTo(1));
            Assert.That(requested, Is.EqualTo(new[] { "theme&mode.css" }));
            _go.SetActive(true);
            Assert.That(_host.Document, Is.Not.Null, _host.LastError);
            Assert.That(_host.LinkedStylesheetHrefs, Is.EqualTo(requested));
            Assert.That(BoxWidth(), Is.EqualTo(73).Within(0.01));
        }

        [Test]
        public void Player_UsesTheBakedSheet_WhenThereIsNoAssetPath()
        {
            var html = new TextAsset("<link rel=\"stylesheet\" href=\"theme.css\"><body><div id=\"box\">x</div></body>");
            _host.DocumentAsset = html;
            int baked = _host.BakeLinkedStylesheets(href => href == "theme.css" ? "body{margin:0}#box{width:77px}" : null);
            Assert.That(baked, Is.EqualTo(1));
            _go.SetActive(true);
            Assume.That(_host.Document, Is.Not.Null, _host.LastError);
            Assert.That(BoxWidth(), Is.EqualTo(77).Within(0.01), "the baked text applied: an in-memory asset has no editor path");
        }

        [Test]
        public void AssetReader_IsTheLastResort_AndAMissingLinkWarnsOnce()
        {
            var html = new TextAsset("<link rel=\"stylesheet\" href=\"disk.css\"><body><div id=\"box\">x</div></body>");
            _host.DocumentAsset = html;
            _go.SetActive(true);
            Assume.That(_host.Document, Is.Not.Null, _host.LastError);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("linked stylesheet 'disk.css' not found"));
            _host.Reload();
            _host.Document.AssetReader = path => path == "disk.css" ? System.Text.Encoding.UTF8.GetBytes("body{margin:0}#box{width:55px}") : null;
            _host.Reload();
            Assert.That(BoxWidth(), Is.EqualTo(55).Within(0.01), "the core's asset reader served the sheet");
        }

        // A document on a prefab never passes through the scene hook; the
        // build's preprocess step bakes every prefab document instead, and only
        // saves a prefab whose bake changed.
        [Test]
        public void Prefab_IsBakedBeforeABuild_AndOnlyWhenItChanged()
        {
            const string path = FixtureDir + "/tmp-linked.prefab";
            var html = AssetDatabase.LoadAssetAtPath<TextAsset>(FixtureDir + "/linked.html");
            Assume.That(html, Is.Not.Null);
            _host.DocumentAsset = html;
            var prefab = PrefabUtility.SaveAsPrefabAsset(_go, path);
            try
            {
                Assume.That(prefab, Is.Not.Null, "the fixture prefab saved");
                Assert.That(prefab.GetComponent<WevaDocument>().BakedLinkedStylesheets.Hrefs, Is.Null.Or.Empty, "nothing baked yet");
                int baked = Weva.EditorTools.Documents.WevaDocumentLinkBaker.BakePrefabs();
                Assert.That(baked, Is.GreaterThanOrEqualTo(1), "the prefab document was baked");
                var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<WevaDocument>();
                var bake = reloaded.BakedLinkedStylesheets;
                Assert.That(bake.Hrefs, Is.EqualTo(new[] { "linked.css" }));
                Assert.That(bake.Css[0], Does.Contain("width: 123px"));
                Assert.That(Weva.EditorTools.Documents.WevaDocumentLinkBaker.BakePrefabs(), Is.EqualTo(0), "a bake that is already current changes nothing");
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void InspectorSheets_ComeAfterThePagesOwn_SoTheyWin()
        {
            var html = AssetDatabase.LoadAssetAtPath<TextAsset>(FixtureDir + "/linked.html");
            Assume.That(html, Is.Not.Null);
            _host.DocumentAsset = html;
            _host.StylesheetAssets = new[] { new TextAsset("#box{width:200px}") };
            _go.SetActive(true);
            Assume.That(_host.Document, Is.Not.Null, _host.LastError);
            Assert.That(BoxWidth(), Is.EqualTo(200).Within(0.01));
        }
    }
}
