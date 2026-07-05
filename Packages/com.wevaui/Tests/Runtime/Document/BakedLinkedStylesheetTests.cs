using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Documents;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Documents {
    // Player builds have no disk/AssetDatabase, so <link rel="stylesheet">
    // resolution (which reads href relative to DocumentPath) used to drop
    // every linked sheet and render UA-only — glass.html shipped unstyled
    // (build report, 2026-06-06). The fix: the editor bakes each link's CSS
    // text into the WevaDocument (LinkedStylesheetBakeProcessor) and
    // UIDocumentBuilder consumes the bake whenever DocumentPath is
    // unavailable. These tests pin the builder half of that contract.
    public class BakedLinkedStylesheetTests {

        static UIDocumentState BuildWithBake(string html, IReadOnlyList<string> hrefs,
                                             IReadOnlyList<string> css, string documentPath = null) {
            var builder = new UIDocumentBuilder {
                DocumentSource = html,
                DocumentPath = documentPath,
                BakedLinkedHrefs = hrefs,
                BakedLinkedCss = css
            };
            var state = builder.Build();
            UIDocumentLifecycle.RunLayout(state);
            return state;
        }

        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && !(b is TextRun)
                    && (b.Element.GetAttribute("class") ?? "") == cls) return b;
            }
            return null;
        }

        const string Html =
            "<html><head><link rel=\"stylesheet\" href=\"theme.css\"></head>" +
            "<body><div class=\"card\">x</div></body></html>";

        [Test]
        public void Baked_linked_css_applies_without_document_path() {
            var state = BuildWithBake(Html,
                new[] { "theme.css" },
                new[] { ".card { width: 123px; height: 45px; }" });
            var card = FindByClass(state.RootBox, "card");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Width, Is.EqualTo(123).Within(0.01),
                "the baked linked sheet must reach the cascade in a player");
            Assert.That(card.Height, Is.EqualTo(45).Within(0.01));
        }

        [Test]
        public void Without_path_and_without_bake_links_drop_but_build_succeeds() {
            // The pre-fix player behavior, preserved as the no-bake fallback:
            // UA-only rendering, never a crash.
            var state = BuildWithBake(Html, null, null);
            var card = FindByClass(state.RootBox, "card");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Width, Is.Not.EqualTo(123),
                "no bake -> the linked sheet cannot apply");
        }

        [Test]
        public void Baked_sheets_keep_document_order_for_cascade_ties() {
            const string html =
                "<html><head>" +
                "<link rel=\"stylesheet\" href=\"a.css\">" +
                "<link rel=\"stylesheet\" href=\"b.css\">" +
                "</head><body><div class=\"card\">x</div></body></html>";
            // Equal specificity — the LATER link in document order must win,
            // regardless of bake-array order (bake stores b first here).
            var state = BuildWithBake(html,
                new[] { "b.css", "a.css" },
                new[] { ".card { width: 222px; }", ".card { width: 111px; }" });
            var card = FindByClass(state.RootBox, "card");
            Assert.That(card.Width, Is.EqualTo(222).Within(0.01),
                "b.css is the later <link>; its equal-specificity rule must win");
        }

        [Test]
        public void Link_media_attribute_evaluates_at_runtime_even_when_baked() {
            const string html =
                "<html><head>" +
                "<link rel=\"stylesheet\" href=\"wide.css\" media=\"(min-width: 5000px)\">" +
                "</head><body><div class=\"card\">x</div></body></html>";
            var state = BuildWithBake(html,
                new[] { "wide.css" },
                new[] { ".card { width: 123px; }" });
            var card = FindByClass(state.RootBox, "card");
            Assert.That(card.Width, Is.Not.EqualTo(123),
                "the media attribute gates baked sheets against the runtime viewport");
        }

        [Test]
        public void Unbaked_href_warns_and_skips_but_other_links_apply() {
            const string html =
                "<html><head>" +
                "<link rel=\"stylesheet\" href=\"missing.css\">" +
                "<link rel=\"stylesheet\" href=\"theme.css\">" +
                "</head><body><div class=\"card\">x</div></body></html>";
            var state = BuildWithBake(html,
                new[] { "theme.css" },
                new[] { ".card { width: 123px; }" });
            var card = FindByClass(state.RootBox, "card");
            Assert.That(card.Width, Is.EqualTo(123).Within(0.01),
                "one unbaked link must not take down its siblings");
        }

        [Test]
        public void Disk_wins_over_bake_when_document_path_is_available() {
            // Editor semantics: a stale bake must never shadow the live file.
            string dir = Path.Combine(Path.GetTempPath(), "weva-bake-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string cssPath = Path.Combine(dir, "theme.css");
                string htmlPath = Path.Combine(dir, "page.html");
                File.WriteAllText(cssPath, ".card { width: 200px; }");
                File.WriteAllText(htmlPath, Html);
                var state = BuildWithBake(Html,
                    new[] { "theme.css" },
                    new[] { ".card { width: 123px; }" },
                    documentPath: htmlPath);
                var card = FindByClass(state.RootBox, "card");
                Assert.That(card.Width, Is.EqualTo(200).Within(0.01),
                    "with a document path the on-disk file wins over the bake");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Bake_is_fallback_when_disk_file_is_missing() {
            // Document path present but the css file isn't on disk (moved
            // between bake and load) -> fall back to the baked copy rather
            // than dropping the sheet.
            string dir = Path.Combine(Path.GetTempPath(), "weva-bake-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string htmlPath = Path.Combine(dir, "page.html");
                File.WriteAllText(htmlPath, Html);
                var state = BuildWithBake(Html,
                    new[] { "theme.css" },
                    new[] { ".card { width: 123px; }" },
                    documentPath: htmlPath);
                var card = FindByClass(state.RootBox, "card");
                Assert.That(card.Width, Is.EqualTo(123).Within(0.01),
                    "missing disk file falls back to the baked copy");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void CollectLinkedStylesheetHrefs_orders_filters_and_dedups() {
            const string html =
                "<html><head>" +
                "<link rel=\"stylesheet\" href=\"a.css\">" +
                "<link rel=\"icon\" href=\"favicon.ico\">" +
                "<link rel=\"stylesheet\" href=\"https://cdn.example/x.css\">" +
                "<link rel=\"stylesheet\">" +
                "<link rel=\"alternate stylesheet\" href=\"alt.css\">" +
                "<link rel=\"stylesheet\" href=\"sub/b.css\" media=\"(min-width: 5000px)\">" +
                "<link rel=\"stylesheet\" href=\"a.css\">" +
                "</head><body></body></html>";
            var hrefs = UIDocumentBuilder.CollectLinkedStylesheetHrefs(html);
            // Media is NOT evaluated here — bake-time can't know the device,
            // so sub/b.css must be captured despite its media attribute.
            Assert.That(hrefs, Is.EqualTo(new List<string> { "a.css", "sub/b.css" }));
        }

        [Test]
        public void ResolveStylesheetHref_resolves_relative_to_document() {
            string resolved = UIDocumentBuilder.ResolveStylesheetHref(
                "sub/b.css", Path.Combine(Path.GetTempPath(), "pages", "index.html"));
            Assert.That(resolved, Does.Contain("pages"));
            Assert.That(resolved, Does.Contain("b.css"));
            Assert.That(UIDocumentBuilder.ResolveStylesheetHref(null, "x.html"), Is.Null);
            Assert.That(UIDocumentBuilder.ResolveStylesheetHref("a.css", null), Is.Null);
        }
    }
}
