using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Documents;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Documents {
    // Player builds have no disk/AssetDatabase, so <template src="..."> resolution
    // (which reads files relative to DocumentPath) used to silently produce
    // unexpanded components. The fix mirrors the linked-stylesheet bake: an
    // editor build hook captures each template's HTML text into WevaDocument
    // serialized fields (LinkedStylesheetBaker.Bake), and UIDocumentBuilder
    // passes them through to ComponentTemplateImporter.Resolve via
    // BakedTemplateHrefs/BakedTemplateHtml. These tests pin the builder half
    // of that contract.
    public class BakedTemplateTests {

        static UIDocumentState BuildWithBakedTemplates(string html,
            IReadOnlyList<string> hrefs, IReadOnlyList<string> tmplHtml,
            string documentPath = null) {
            var builder = new UIDocumentBuilder {
                DocumentSource = html,
                DocumentPath = documentPath,
                BakedTemplateHrefs = hrefs,
                BakedTemplateHtml = tmplHtml
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

        // ---------------------------------------------------------------
        // Basic bake-consumption tests (builder half)
        // ---------------------------------------------------------------

        [Test]
        public void Baked_template_expands_component_without_document_path() {
            // In a player build DocumentPath is null; component expansion must
            // succeed using only the baked template HTML.
            const string html =
                "<html><body>" +
                "<template src=\"card.html\"></template>" +
                "<my-card></my-card>" +
                "</body></html>";
            const string tmplHtml =
                "<template id=\"my-card\"><div class=\"card-root\"><slot></slot></div></template>";

            var state = BuildWithBakedTemplates(html,
                new[] { "card.html" },
                new[] { tmplHtml });

            Assert.That(state.Components.Contains("my-card"), Is.True,
                "the baked template must register the my-card component");
            var cardRoot = FindByClass(state.RootBox, "card-root");
            Assert.That(cardRoot, Is.Not.Null,
                "the baked template content must have been expanded into the DOM");
        }

        [Test]
        public void Without_path_and_without_bake_templates_do_not_expand() {
            // Pre-fix player behaviour: template imports are dropped with a
            // warning, not a crash.
            const string html =
                "<html><body>" +
                "<template src=\"card.html\"></template>" +
                "<my-card></my-card>" +
                "</body></html>";

            var state = BuildWithBakedTemplates(html, null, null);

            // Component was never registered because the template couldn't be
            // loaded, so <my-card> stays as a raw unknown element.
            Assert.That(state.Components.Contains("my-card"), Is.False,
                "without a bake the component cannot be registered");
        }

        [Test]
        public void Disk_wins_over_bake_when_document_path_is_available() {
            // Editor semantics: the live disk file always takes priority over
            // the baked copy so a stale bake never shadows an edit.
            string dir = Path.Combine(Path.GetTempPath(),
                "weva-tmpl-bake-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string htmlPath = Path.Combine(dir, "page.html");
                File.WriteAllText(htmlPath,
                    "<html><body><template src=\"card.html\"></template><my-card></my-card></body></html>");
                // Disk file uses "disk-class"; bake uses "baked-class".
                File.WriteAllText(Path.Combine(dir, "card.html"),
                    "<template id=\"my-card\"><div class=\"disk-class\"><slot></slot></div></template>");

                var state = BuildWithBakedTemplates(
                    File.ReadAllText(htmlPath),
                    new[] { "card.html" },
                    new[] { "<template id=\"my-card\"><div class=\"baked-class\"></div></template>" },
                    documentPath: htmlPath);

                var diskBox = FindByClass(state.RootBox, "disk-class");
                var bakedBox = FindByClass(state.RootBox, "baked-class");
                Assert.That(diskBox, Is.Not.Null, "the disk file must be preferred over the bake");
                Assert.That(bakedBox, Is.Null, "the stale bake must not shadow the disk file");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Bake_is_fallback_when_disk_file_is_missing() {
            // DocumentPath present, but the template file was moved/deleted
            // between bake and load. The bake must provide the fallback content.
            string dir = Path.Combine(Path.GetTempPath(),
                "weva-tmpl-bake-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string htmlPath = Path.Combine(dir, "page.html");
                File.WriteAllText(htmlPath,
                    "<html><body><template src=\"card.html\"></template><my-card></my-card></body></html>");
                // No card.html on disk — only baked.

                var state = BuildWithBakedTemplates(
                    File.ReadAllText(htmlPath),
                    new[] { "card.html" },
                    new[] { "<template id=\"my-card\"><div class=\"baked-class\"><slot></slot></div></template>" },
                    documentPath: htmlPath);

                Assert.That(state.Components.Contains("my-card"), Is.True,
                    "missing disk file must fall back to the baked copy");
                var bakedBox = FindByClass(state.RootBox, "baked-class");
                Assert.That(bakedBox, Is.Not.Null,
                    "the baked template content must reach the DOM when disk is missing");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Multiple_baked_templates_all_register_as_components() {
            // Two separate <template src> references in the same document —
            // both must be resolved from the bake array.
            const string html =
                "<html><body>" +
                "<template src=\"card.html\"></template>" +
                "<template src=\"badge.html\"></template>" +
                "<my-card></my-card><my-badge></my-badge>" +
                "</body></html>";
            const string cardTmpl =
                "<template id=\"my-card\"><div class=\"card-root\"><slot></slot></div></template>";
            const string badgeTmpl =
                "<template id=\"my-badge\"><span class=\"badge-root\"><slot></slot></span></template>";

            var state = BuildWithBakedTemplates(html,
                new[] { "card.html", "badge.html" },
                new[] { cardTmpl, badgeTmpl });

            Assert.That(state.Components.Contains("my-card"), Is.True,
                "my-card must register from the bake");
            Assert.That(state.Components.Contains("my-badge"), Is.True,
                "my-badge must register from the bake (second baked template)");
            var cardRoot = FindByClass(state.RootBox, "card-root");
            var badgeRoot = FindByClass(state.RootBox, "badge-root");
            Assert.That(cardRoot, Is.Not.Null, "card content must be in the DOM");
            Assert.That(badgeRoot, Is.Not.Null, "badge content must be in the DOM");
        }

        [Test]
        public void Missing_baked_template_warns_but_does_not_throw() {
            // A <template src> that points to an href not in the bake array
            // must produce a warning and skip that template — no exception.
            const string html =
                "<html><body>" +
                "<template src=\"missing.html\"></template>" +
                "<template src=\"present.html\"></template>" +
                "<my-widget></my-widget>" +
                "</body></html>";
            const string presentTmpl =
                "<template id=\"my-widget\"><div class=\"widget\"><slot></slot></div></template>";

            UIDocumentState state = null;
            Assert.DoesNotThrow(() => {
                state = BuildWithBakedTemplates(html,
                    new[] { "present.html" },
                    new[] { presentTmpl });
            }, "a missing baked template must not throw");

            Assert.That(state, Is.Not.Null);
            // The present template must still register.
            Assert.That(state.Components.Contains("my-widget"), Is.True,
                "the present baked template must still register even when another is missing");
        }

        // ---------------------------------------------------------------
        // CollectTemplateHrefs build-tooling seam tests
        // ---------------------------------------------------------------

        [Test]
        public void CollectTemplateHrefs_returns_src_values_in_document_order() {
            const string html =
                "<html><body>" +
                "<template src=\"a.html\"></template>" +
                "<template src=\"b.html\"></template>" +
                "<template id=\"inline\">content</template>" +
                "<template src=\"a.html\"></template>" +
                "</body></html>";

            var hrefs = UIDocumentBuilder.CollectTemplateHrefs(html);
            // document order, no duplicates
            Assert.That(hrefs, Is.EqualTo(new List<string> { "a.html", "b.html" }));
        }

        [Test]
        public void CollectTemplateHrefs_returns_empty_list_for_html_with_no_template_imports() {
            const string html =
                "<html><head><link rel=\"stylesheet\" href=\"a.css\"></head>" +
                "<body><p>Hello</p></body></html>";

            var hrefs = UIDocumentBuilder.CollectTemplateHrefs(html);
            Assert.That(hrefs, Is.Empty);
        }

        // ---------------------------------------------------------------
        // ResolveTemplateHref build-tooling seam tests
        // ---------------------------------------------------------------

        [Test]
        public void ResolveTemplateHref_resolves_relative_to_document() {
            string resolved = UIDocumentBuilder.ResolveTemplateHref(
                "sub/card.html", Path.Combine(Path.GetTempPath(), "pages", "index.html"));
            Assert.That(resolved, Does.Contain("pages"));
            Assert.That(resolved, Does.Contain("card.html"));
        }

        [Test]
        public void ResolveTemplateHref_returns_null_for_missing_inputs() {
            Assert.That(UIDocumentBuilder.ResolveTemplateHref(null, "x.html"), Is.Null);
            Assert.That(UIDocumentBuilder.ResolveTemplateHref("a.html", null), Is.Null);
            Assert.That(UIDocumentBuilder.ResolveTemplateHref("", "x.html"), Is.Null);
        }

        // ---------------------------------------------------------------
        // Nested-template descent: a <template src> INSIDE a <template id>
        // definition body now resolves at import time (was the documented
        // residual limitation of A-LINK-PLAYER-TEMPLATES on BOTH paths).
        // ---------------------------------------------------------------

        static Weva.Dom.Element FindTemplateById(Weva.Dom.Node node, string id) {
            if (node is Weva.Dom.Element el
                && string.Equals(el.TagName, "template", System.StringComparison.OrdinalIgnoreCase)
                && el.GetAttribute("id") == id) return el;
            foreach (var child in node.Children) {
                var hit = FindTemplateById(child, id);
                if (hit != null) return hit;
            }
            return null;
        }

        [Test]
        public void Nested_template_import_inside_definition_resolves_from_bake() {
            const string html =
                "<html><body>" +
                "<template id=\"card\">" +
                "<template src=\"inner.html\"></template>" +
                "<div class=\"card-body\">x</div>" +
                "</template>" +
                "</body></html>";
            const string innerHtml =
                "<template id=\"inner-bit\"><b>I</b></template>";

            var state = BuildWithBakedTemplates(html,
                new[] { "inner.html" },
                new[] { innerHtml });

            var card = FindTemplateById(state.Doc, "card");
            Assert.That(card, Is.Not.Null, "card definition survives");
            var inner = FindTemplateById(card, "inner-bit");
            Assert.That(inner, Is.Not.Null,
                "the nested <template src> placeholder must be replaced by the imported definition");
            // No unresolved src placeholders left inside the definition.
            bool anySrcLeft = false;
            void Walk(Weva.Dom.Node n) {
                if (n is Weva.Dom.Element e2
                    && string.Equals(e2.TagName, "template", System.StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(e2.GetAttribute("src"))) anySrcLeft = true;
                foreach (var c in n.Children) Walk(c);
            }
            Walk(card);
            Assert.That(anySrcLeft, Is.False, "no unresolved src templates remain in the body");
        }

        [Test]
        public void Nested_template_import_inside_definition_resolves_from_disk() {
            string dir = Path.Combine(Path.GetTempPath(), "weva-nested-tmpl-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string innerPath = Path.Combine(dir, "inner.html");
                string pagePath = Path.Combine(dir, "page.html");
                File.WriteAllText(innerPath, "<template id=\"inner-bit\"><b>I</b></template>");
                const string html =
                    "<html><body>" +
                    "<template id=\"card\"><template src=\"inner.html\"></template></template>" +
                    "</body></html>";
                File.WriteAllText(pagePath, html);
                var state = BuildWithBakedTemplates(html, null, null, documentPath: pagePath);
                var card = FindTemplateById(state.Doc, "card");
                Assert.That(card, Is.Not.Null);
                Assert.That(FindTemplateById(card, "inner-bit"), Is.Not.Null,
                    "nested import must resolve from disk too");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void CollectTemplateHrefs_descends_into_definition_bodies() {
            const string html =
                "<html><body>" +
                "<template id=\"card\"><template src=\"inner.html\"></template></template>" +
                "<template src=\"top.html\"></template>" +
                "</body></html>";
            var hrefs = Weva.Documents.UIDocumentBuilder.CollectTemplateHrefs(html);
            Assert.That(hrefs, Does.Contain("inner.html"),
                "the baker must capture nested imports or players drop them");
            Assert.That(hrefs, Does.Contain("top.html"));
        }

        [Test]
        public void Nested_self_referencing_import_does_not_hang() {
            // inner.html nests an import of ITSELF inside a definition — the
            // bake path has no path-based cycle stack (importedPath == null),
            // so the depth cap is the guard. Build must terminate and succeed.
            const string html =
                "<html><body>" +
                "<template id=\"card\"><template src=\"inner.html\"></template></template>" +
                "</body></html>";
            const string innerHtml =
                "<template id=\"inner\"><template src=\"inner.html\"></template></template>";
            var state = BuildWithBakedTemplates(html,
                new[] { "inner.html" },
                new[] { innerHtml });
            Assert.That(state.Doc, Is.Not.Null, "build terminates despite the cycle");
        }
    }
}
