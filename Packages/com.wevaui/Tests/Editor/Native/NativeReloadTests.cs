// ABI minor 32: a hot reload through the wrapper keeps the elements it can match.
using NUnit.Framework;
using System;
using System.IO;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeReloadTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\"><p id=\"p\">one</p></div><div id=\"b\">two</div>");
            _doc.SetCss("body{margin:0}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        [Test]
        public void ReloadHtml_KeepsMatchedHandlesAndAppliesTheRest()
        {
            uint a = _doc.Query("#a"), p = _doc.Query("#p");
            _doc.ReloadHtml("<div id=\"a\" class=\"new\"><p id=\"p\">uno</p><p id=\"q\">q</p></div>");
            _doc.Update(0);
            Assert.That(_doc.Query("#a"), Is.EqualTo(a));
            Assert.That(_doc.Query("#p"), Is.EqualTo(p));
            Assert.That(_doc.ElementAttribute(a, "class"), Is.EqualTo("new"));
            Assert.That(_doc.ElementText(p), Is.EqualTo("uno"));
            Assert.That(_doc.Query("#q"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE));
            Assert.That(_doc.Query("#b"), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE));
        }

        [Test]
        public void ReloadHtml_ReversesKeyedSiblingsInPlace()
        {
            _doc.LoadHtml("<div id=list><input id=a><input id=b><input id=c></div>");
            _doc.SetCss("body{margin:0} input{display:block;width:100px;height:20px;border:0;padding:0}"
                + "#a{background:red} #b{background:blue} #c{background:lime}");
            _doc.Update(0);
            uint list = _doc.Query("#list"), a = _doc.Query("#a"), b = _doc.Query("#b"), c = _doc.Query("#c");
            _doc.SetFocus(b);
            _doc.SetElementValue(b, "edited");

            _doc.ReloadHtml("<div id=list><input id=c><input id=b><input id=a></div>");
            _doc.Update(0);

            Assert.That(_doc.Children(list), Is.EqualTo(new[] { c, b, a }));
            Assert.That(_doc.ElementValue(b), Is.EqualTo("edited"));
            Assert.That(_doc.Focus, Is.EqualTo(b));
            string dump = Environment.GetEnvironmentVariable("WEVA_RELOAD_ORDER_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                using (var renderer = new NativeDocumentRenderer())
                {
                    Texture2D image = renderer.RenderToTexture(_doc, 160, 100, Color.white);
                    try
                    {
                        Directory.CreateDirectory(dump);
                        File.WriteAllBytes(Path.Combine(dump, "reversed.png"), image.EncodeToPNG());
                    }
                    finally { UnityEngine.Object.DestroyImmediate(image); }
                }
            }
        }
    }
}
