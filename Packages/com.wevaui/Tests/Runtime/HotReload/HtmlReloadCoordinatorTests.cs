using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Events;
using Weva.HotReload;
using Weva.Reactive;

namespace Weva.Tests.HotReload {
    public class HtmlReloadCoordinatorTests {
        string tempRoot;

        [SetUp]
        public void Setup() {
            tempRoot = Path.Combine(Path.GetTempPath(), "weva-htmlreload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void Teardown() {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { }
        }

        UIDocumentState BuildState(string htmlPath, string html, string css = "") {
            File.WriteAllText(htmlPath, html);
            var b = new UIDocumentBuilder {
                DocumentSource = html,
                DocumentPath = htmlPath,
                StylesheetSources = new List<string> { css },
                StylesheetPaths = new List<string> { null },
                MediaContext = MediaContext.Default(1024, 768),
                Clock = new FakeUIClock()
            };
            return b.Build();
        }

        static Element FirstByTag(Document doc, string tag) {
            foreach (var e in doc.GetElementsByTagName(tag)) return e;
            return null;
        }

        [Test]
        public void Modified_html_applies_within_one_tick() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='m'>hi</p></main>");
            Assert.That(s.Doc.GetElementById("m").Children[0] is TextNode t && t.Data == "hi");
            UIDocumentLifecycle.Update(s, null, 0.0);
            long painterVersion = s.Painter.ContextVersion;

            File.WriteAllText(p, "<main><p id='m'>bye</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);

            Assert.That(coord.Tick(1.0), Is.True);
            var refreshed = s.Doc.GetElementById("m");
            var tn = refreshed.Children[0] as TextNode;
            Assert.That(tn, Is.Not.Null);
            Assert.That(tn.Data, Is.EqualTo("bye"));
            Assert.That(s.Painter.ContextVersion, Is.GreaterThan(painterVersion),
                "HTML hot reload must drop retained paint/subtree batches from the previous DOM");
            Assert.That(s.PaintInvalidated, Is.True);
            Assert.That(s.HasEmittedPaint, Is.False);
        }

        [Test]
        public void Identity_preserved_for_keyed_elements() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='m'>hi</p></main>");
            var live = s.Doc.GetElementById("m");
            live.SetAttribute("data-runtime", "live");

            File.WriteAllText(p, "<main><p id='m'>bye</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            var live2 = s.Doc.GetElementById("m");
            Assert.That(live2, Is.SameAs(live));
            // Runtime-set attribute is dropped because fresh HTML didn't
            // carry it. This is intentional: the diff makes attrs match
            // the on-disk source. Form input value preservation is
            // handled by separate test below using id-keyed input.
        }

        [Test]
        public void Form_input_keyed_by_id_keeps_element_identity() {
            // Element identity preservation is the v1 path for form-state
            // preservation: the InputElement (and any wrapper that caches
            // a TextEditModel keyed off Element identity) holds onto the
            // same Element across the reload, so its in-memory state
            // (selection, scroll position) is not lost.
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><input id='in' type='text'/></main>");
            var input = s.Doc.GetElementById("in");

            File.WriteAllText(p, "<main><input id='in' type='text' placeholder='ph'/></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            var input2 = s.Doc.GetElementById("in");
            Assert.That(input2, Is.SameAs(input));
            Assert.That(input2.GetAttribute("placeholder"), Is.EqualTo("ph"));
        }

        [Test]
        public void Unmatched_elements_are_added() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='a'>A</p></main>");
            File.WriteAllText(p, "<main><p id='a'>A</p><p id='b'>B</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            Assert.That(s.Doc.GetElementById("b"), Is.Not.Null);
        }

        [Test]
        public void Unmatched_elements_are_removed() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='a'>A</p><p id='b'>B</p></main>");
            File.WriteAllText(p, "<main><p id='a'>A</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            Assert.That(s.Doc.GetElementById("b"), Is.Null);
            Assert.That(s.Doc.GetElementById("a"), Is.Not.Null);
        }

        [Test]
        public void Removed_component_template_is_not_reused_from_stale_registry() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<template id='card'><div id='stale'></div></template><card></card>");
            Assert.That(s.Components.Contains("card"), Is.True);
            Assert.That(s.Doc.GetElementById("stale"), Is.Not.Null);

            File.WriteAllText(p, "<card></card>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            var card = FirstByTag(s.Doc, "card");
            Assert.That(s.Components.Contains("card"), Is.False);
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Children, Has.Count.EqualTo(0));
            Assert.That(s.Doc.GetElementById("stale"), Is.Null);
        }

        [Test]
        public void Imported_component_template_reload_rebuilds_main_document() {
            var p = Path.Combine(tempRoot, "a.html");
            var imported = Path.Combine(tempRoot, "card.html");
            File.WriteAllText(imported, "<template id='card'><div id='v1'>one</div></template>");
            var s = BuildState(p, "<main><template src='card.html'></template><card></card></main>");
            Assert.That(s.ComponentTemplatePaths, Does.Contain(Path.GetFullPath(imported)));
            Assert.That(s.Doc.GetElementById("v1"), Is.Not.Null);

            File.WriteAllText(imported, "<template id='card'><div id='v2'>two</div></template>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(imported);
            coord.Tick(1.0);

            Assert.That(s.Doc.GetElementById("v1"), Is.Null);
            Assert.That(s.Doc.GetElementById("v2"), Is.Not.Null);
            Assert.That(coord.ReloadCount, Is.EqualTo(1));
        }

        [Test]
        public void Attribute_changes_apply_to_matched_elements() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='m' class='x'>hi</p></main>");
            var elem = s.Doc.GetElementById("m");
            File.WriteAllText(p, "<main><p id='m' class='y z'>hi</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            Assert.That(s.Doc.GetElementById("m"), Is.SameAs(elem));
            Assert.That(elem.GetAttribute("class"), Is.EqualTo("y z"));
        }

        [Test]
        public void Reload_marks_invalidation_so_next_layout_reruns() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='m'>hi</p></main>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            Assert.That(s.RootBox, Is.Not.Null);

            File.WriteAllText(p, "<main><p id='m'>bye</p><p id='n'>new</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            // RootBox cleared; tracker has entries from the diff's mutations.
            Assert.That(s.RootBox, Is.Null);
            Assert.That(s.Invalidation.DirtyCount, Is.GreaterThan(0));
        }

        [Test]
        public void Parse_failure_keeps_previous_dom() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='m'>hi</p></main>");
            var elem = s.Doc.GetElementById("m");

            // Delete the file mid-edit — read fails, previous DOM survives.
            File.Delete(p);
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            Assert.That(s.Doc.GetElementById("m"), Is.SameAs(elem));
        }

        [Test]
        public void Unknown_path_is_ignored_and_does_not_throw() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p>hi</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(Path.Combine(tempRoot, "definitely-not-registered.html"));
            Assert.DoesNotThrow(() => coord.Tick(1.0));
            Assert.That(coord.ReloadCount, Is.EqualTo(0));
        }

        [Test]
        public void Tick_is_a_noop_when_queue_is_empty() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            Assert.That(coord.Tick(1.0), Is.False);
            Assert.That(coord.ReloadCount, Is.EqualTo(0));
        }

        [Test]
        public void Debounce_collapses_repeated_saves_within_50ms() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><p id='m'>hi</p></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);

            File.WriteAllText(p, "<main><p id='m'>v2</p></main>");
            queue.Enqueue(p);
            Assert.That(coord.Tick(1.000), Is.True);

            queue.Enqueue(p);
            Assert.That(coord.Tick(1.010), Is.False);
            Assert.That(coord.ReloadCount, Is.EqualTo(1));

            File.WriteAllText(p, "<main><p id='m'>v3</p></main>");
            queue.Enqueue(p);
            Assert.That(coord.Tick(1.100), Is.True);
            Assert.That(coord.ReloadCount, Is.EqualTo(2));
        }

        [Test]
        public void Nested_subtree_diff_preserves_matching_descendants() {
            var p = Path.Combine(tempRoot, "a.html");
            var s = BuildState(p, "<main><section id='s'><p id='inner'>v1</p></section></main>");
            var section = s.Doc.GetElementById("s");
            var inner = s.Doc.GetElementById("inner");

            File.WriteAllText(p, "<main><section id='s'><p id='inner'>v2</p></section></main>");
            var queue = new HtmlReloadQueue();
            var coord = new HtmlReloadCoordinator(s, queue);
            queue.Enqueue(p);
            coord.Tick(1.0);

            Assert.That(s.Doc.GetElementById("s"), Is.SameAs(section));
            Assert.That(s.Doc.GetElementById("inner"), Is.SameAs(inner));
            Assert.That(((TextNode)inner.Children[0]).Data, Is.EqualTo("v2"));
        }
    }
}
