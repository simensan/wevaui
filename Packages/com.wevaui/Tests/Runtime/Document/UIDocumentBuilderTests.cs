using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Events;

namespace Weva.Tests.Documents {
    public class UIDocumentBuilderTests {
        static UIDocumentBuilder NewBuilder(string html, params string[] stylesheets) {
            return new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string>(stylesheets),
                MediaContext = MediaContext.Default(1024, 768),
                Clock = new FakeUIClock()
            };
        }

        [Test]
        public void Empty_document_produces_non_null_pipeline() {
            var s = NewBuilder("").Build();
            Assert.That(s.Doc, Is.Not.Null);
            Assert.That(s.Cascade, Is.Not.Null);
            Assert.That(s.LayoutEngine, Is.Not.Null);
            Assert.That(s.Painter, Is.Not.Null);
            Assert.That(s.Animator, Is.Not.Null);
            Assert.That(s.Components, Is.Not.Null);
            Assert.That(s.Bindings, Is.Not.Null);
            Assert.That(s.Events, Is.Not.Null);
            Assert.That(s.Invalidation, Is.Not.Null);
            Assert.That(s.HitTester, Is.Not.Null);
            Assert.That(s.ElementToBox, Is.Not.Null);
        }

        [Test]
        public void Cascade_computes_styles_for_simple_html_css_pair() {
            var s = NewBuilder(
                "<main><p id='msg'>hi</p></main>",
                "p { color: red; }").Build();
            var p = s.Doc.GetElementById("msg");
            var style = s.Cascade.Compute(p, s.State);
            Assert.That(style, Is.Not.Null);
            Assert.That(style.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Inline_style_block_is_applied() {
            // A sample that styles itself with an inline <style> block (no <link>,
            // no explicit StylesheetSources) — e.g. 9slice-demo.html. The builder
            // must collect the <style> CSS into the author cascade.
            var s = NewBuilder(
                "<style>p { color: green; padding-left: 7px; }</style><main><p id='msg'>hi</p></main>").Build();
            var p = s.Doc.GetElementById("msg");
            var style = s.Cascade.Compute(p, s.State);
            Assert.That(style.Get("color"), Is.EqualTo("green"),
                "inline <style> rule should win for color");
            Assert.That(style.Get("padding-left"), Is.EqualTo("7px"),
                "inline <style> rule should apply padding");
        }

        [Test]
        public void Inline_style_block_and_explicit_sheet_both_apply() {
            // Explicit StylesheetSources (e.g. inspector-wired .css) and an inline
            // <style> block coexist; both contribute to the cascade.
            var s = NewBuilder(
                "<style>.b { font-weight: 700; }</style><p id='msg' class='a b'>hi</p>",
                ".a { color: blue; }").Build();
            var p = s.Doc.GetElementById("msg");
            var style = s.Cascade.Compute(p, s.State);
            Assert.That(style.Get("color"), Is.EqualTo("blue"), "explicit sheet applies");
            Assert.That(style.Get("font-weight"), Is.EqualTo("700"), "inline <style> applies");
        }

        [Test]
        public void Components_registered_during_build() {
            var s = NewBuilder("<template id='card'><div class='c'><slot/></div></template><card>x</card>").Build();
            Assert.That(s.Components.Contains("card"), Is.True);
        }

        [Test]
        public void Components_expanded_before_cascade() {
            var s = NewBuilder("<template id='card'><div class='c'><slot/></div></template><card>x</card>").Build();
            var hosts = new List<Weva.Dom.Element>();
            foreach (var el in s.Doc.GetElementsByTagName("card")) hosts.Add(el);
            Assert.That(hosts.Count, Is.EqualTo(1));
            Assert.That(hosts[0].HasAttribute("data-uui-expanded"), Is.True,
                "Component should have been expanded by builder.");
        }

        [Test]
        public void Component_scoped_stylesheet_composes_with_author_sheet() {
            var s = NewBuilder("<p class='warn'>!</p>", "p.warn { color: red; }").Build();
            Weva.Dom.Element first = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { first = e; break; }
            var style = s.Cascade.Compute(first, s.State);
            Assert.That(style.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Build_twice_produces_independent_state() {
            var b = NewBuilder("<p>a</p>");
            var s1 = b.Build();
            var s2 = b.Build();
            Assert.That(ReferenceEquals(s1, s2), Is.False);
            Assert.That(ReferenceEquals(s1.Doc, s2.Doc), Is.False);
            Assert.That(ReferenceEquals(s1.Cascade, s2.Cascade), Is.False);
            Assert.That(ReferenceEquals(s1.LayoutEngine, s2.LayoutEngine), Is.False);
            Assert.That(ReferenceEquals(s1.Painter, s2.Painter), Is.False);
            Assert.That(ReferenceEquals(s1.Invalidation, s2.Invalidation), Is.False);
        }

        [Test]
        public void Invalidation_tracker_attached_to_document() {
            var s = NewBuilder("<div><p>a</p></div>").Build();
            Weva.Dom.Element pEl = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { pEl = e; break; }
            pEl.SetAttribute("class", "x");
            Assert.That(s.Invalidation.HasAny(Weva.Reactive.InvalidationKind.Style), Is.True);
        }

        [Test]
        public void Animation_runner_wired_to_cascade_and_tracker() {
            var s = NewBuilder("<p>a</p>").Build();
            Assert.That(s.Cascade.AnimationRunner, Is.SameAs(s.Animator));
            Assert.That(s.Animator.InvalidationTracker, Is.SameAs(s.Invalidation));
        }

        [Test]
        public void Hit_tester_starts_returning_null_until_layout() {
            var s = NewBuilder("<p>x</p>").Build();
            Assert.That(s.HitTester.HitTest(0, 0), Is.Null);
        }

        [Test]
        public void Bindings_are_scanned_with_supplied_controller() {
            var ctrl = new Ctrl { Coins = 7 };
            var s = new UIDocumentBuilder {
                DocumentSource = "<p>{{ Coins }}</p>",
                Controller = ctrl,
                MediaContext = MediaContext.Default(800, 600),
                Clock = new FakeUIClock()
            }.Build();
            Assert.That(s.Bindings.TextBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Empty_controller_does_not_crash() {
            var s = NewBuilder("<p>hi</p>").Build();
            Assert.DoesNotThrow(() => s.Bindings.Update(null, s.Invalidation));
        }

        [Test]
        public void User_agent_stylesheet_is_first_in_cascade_origin_order() {
            var s = NewBuilder("<p>a</p>", "p { display: inline; }").Build();
            // UA gives p { display: block; } via UserAgentStylesheet.Source.
            // Author override is at higher origin -> should win.
            Weva.Dom.Element pEl = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { pEl = e; break; }
            var style = s.Cascade.Compute(pEl, s.State);
            Assert.That(style.Get("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Link_rel_stylesheet_loads_relative_css_from_document_path() {
            var dir = NewTempDir();
            try {
                var htmlPath = Path.Combine(dir, "index.html");
                var cssPath = Path.Combine(dir, "game-hud.css");
                File.WriteAllText(cssPath, "p { color: rgb(255, 0, 0); }");
                var s = new UIDocumentBuilder {
                    DocumentSource = "<link rel='stylesheet' href='game-hud.css'><main><p id='msg'>hi</p></main>",
                    DocumentPath = htmlPath,
                    MediaContext = MediaContext.Default(1024, 768),
                    Clock = new FakeUIClock()
                }.Build();

                var p = s.Doc.GetElementById("msg");
                var style = s.Cascade.Compute(p, s.State);
                Assert.That(style.Get("color"), Is.EqualTo("rgb(255, 0, 0)"));
                Assert.That(s.StylesheetPaths, Has.Count.EqualTo(1));
                Assert.That(s.StylesheetPaths[0], Is.EqualTo(Path.GetFullPath(cssPath)));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Link_rel_stylesheet_respects_media_attribute() {
            var dir = NewTempDir();
            try {
                var htmlPath = Path.Combine(dir, "index.html");
                var wideCss = Path.Combine(dir, "wide.css");
                var narrowCss = Path.Combine(dir, "narrow.css");
                File.WriteAllText(wideCss, "p { color: rgb(0, 255, 0); }");
                File.WriteAllText(narrowCss, "p { color: rgb(255, 0, 0); }");
                var s = new UIDocumentBuilder {
                    DocumentSource =
                        "<link rel='stylesheet' href='narrow.css' media='(max-width: 500px)'>" +
                        "<link rel='stylesheet' href='wide.css' media='(min-width: 700px)'>" +
                        "<main><p id='msg'>hi</p></main>",
                    DocumentPath = htmlPath,
                    MediaContext = MediaContext.Default(1024, 768),
                    Clock = new FakeUIClock()
                }.Build();

                var p = s.Doc.GetElementById("msg");
                var style = s.Cascade.Compute(p, s.State);
                Assert.That(style.Get("color"), Is.EqualTo("rgb(0, 255, 0)"));
                Assert.That(s.StylesheetPaths, Has.Count.EqualTo(1));
                Assert.That(s.StylesheetPaths[0], Is.EqualTo(Path.GetFullPath(wideCss)));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Link_rel_stylesheet_cascades_after_explicit_stylesheet_assets() {
            var dir = NewTempDir();
            try {
                var htmlPath = Path.Combine(dir, "index.html");
                var cssPath = Path.Combine(dir, "linked.css");
                File.WriteAllText(cssPath, "p { color: rgb(0, 0, 255); }");
                var s = new UIDocumentBuilder {
                    DocumentSource = "<link rel='stylesheet' href='linked.css'><main><p id='msg'>hi</p></main>",
                    DocumentPath = htmlPath,
                    StylesheetSources = new List<string> { "p { color: rgb(255, 0, 0); }" },
                    StylesheetPaths = new List<string> { null },
                    MediaContext = MediaContext.Default(1024, 768),
                    Clock = new FakeUIClock()
                }.Build();

                var p = s.Doc.GetElementById("msg");
                var style = s.Cascade.Compute(p, s.State);
                Assert.That(style.Get("color"), Is.EqualTo("rgb(0, 0, 255)"));
                Assert.That(s.StylesheetPaths, Has.Count.EqualTo(2));
                Assert.That(s.StylesheetPaths[1], Is.EqualTo(Path.GetFullPath(cssPath)));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Lenient_html_parsing_swallows_malformed_input() {
            var b = new UIDocumentBuilder {
                DocumentSource = "<div><p>oops</div>",
                MediaContext = MediaContext.Default(640, 480),
                LenientHtmlParsing = true
            };
            Assert.DoesNotThrow(() => b.Build());
        }

        [Test]
        public void Null_document_source_yields_empty_doc() {
            var s = new UIDocumentBuilder { DocumentSource = null }.Build();
            Assert.That(s.Doc, Is.Not.Null);
            Assert.That(s.Doc.Children.Count, Is.EqualTo(0));
        }

        class Ctrl {
            public int Coins;
        }

        static string NewTempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "weva-link-css-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
