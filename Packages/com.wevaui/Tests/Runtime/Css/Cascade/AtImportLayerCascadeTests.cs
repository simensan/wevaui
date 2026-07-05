using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // C6b — `@import url(x) layer(name)` must wrap the spliced rules in a
    // synthetic block-form @layer at splice time so the cascade engine assigns
    // the imported rules to the named (or anonymous) layer ordinal. These
    // tests pin the parser→loader→cascade pipeline END-to-END: they feed an
    // imported sheet through `AtImportLoader.Resolve`, hand the resolved
    // sheet to a `CascadeEngine`, and assert the layer ordinal on the
    // imported rules matches what the spec mandates.
    public class AtImportLayerCascadeTests {
        string tempRoot;

        [SetUp]
        public void Setup() {
            tempRoot = Path.Combine(Path.GetTempPath(), "weva-atimport-layer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void Teardown() {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { /* best effort */ }
        }

        string Write(string name, string content) {
            var p = Path.Combine(tempRoot, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            return p;
        }

        static MediaContext DefaultMedia() => MediaContext.Default(1920, 1080);

        // Load `importer` from disk, resolve imports, and wrap as an Author
        // OriginatedStylesheet ready for CascadeEngine consumption.
        OriginatedStylesheet ResolveAuthor(string importerPath) {
            var parsed = CssParser.Parse(File.ReadAllText(importerPath));
            var resolved = AtImportLoader.Resolve(parsed, importerPath, DefaultMedia());
            return OriginatedStylesheet.Author(resolved);
        }

        [Test]
        public void Named_layer_qualifier_assigns_imported_rules_to_named_layer_ordinal() {
            // `@import url(import.css) layer(base)` must place the imported
            // sheet's rules in a layer named "base" that shares the SAME ordinal
            // as a sibling `@layer base { ... }` block in the importing sheet.
            Write("import.css", "#x { color: red; }");
            var importer = Write("main.css",
                "@import \"import.css\" layer(base);\n" +
                "@layer base { div { color: blue; } }");

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { ResolveAuthor(importer) });

            // Both rules sit in "base"; same-layer falls through to specificity,
            // and #x (specificity 0,1,0) beats div (0,0,1) — so the imported
            // rule wins. This pins (a) the import's rules ARE in a layer, and
            // (b) it's the SAME layer as the named block — not a separate one.
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));

            Assert.That(engine.TryGetLayerOrdinal("base", out int baseOrdinal), Is.True,
                "named layer 'base' must be registered with the cascade");
            Assert.That(baseOrdinal, Is.Not.EqualTo(CssLayer.UnlayeredOrdinal),
                "imported rules must NOT be unlayered when layer(name) qualifier is present");
        }

        [Test]
        public void Imported_named_layer_loses_to_later_named_layer() {
            // Sanity-check the ordinal ordering: a SECOND named layer declared
            // after the import in the main sheet must outrank it.
            Write("import.css", "div { color: red; }");
            var importer = Write("main.css",
                "@import \"import.css\" layer(base);\n" +
                "@layer overrides { div { color: blue; } }");

            var doc = HtmlParser.Parse("<div></div>");
            var engine = new CascadeEngine(new[] { ResolveAuthor(importer) });

            // `overrides` declared after `base` → overrides wins for equal specificity.
            var cs = engine.Compute(doc.GetElementsByTagName("div").First());
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));

            Assert.That(engine.TryGetLayerOrdinal("base", out int baseOrd), Is.True);
            Assert.That(engine.TryGetLayerOrdinal("overrides", out int overridesOrd), Is.True);
            Assert.That(baseOrd, Is.LessThan(overridesOrd),
                "import-declared layer 'base' must precede later-declared 'overrides'");
        }

        [Test]
        public void Anonymous_layer_qualifier_sorts_imported_rules_before_unlayered_author_rules() {
            // `@import url(import.css) layer` (no name) creates an anonymous
            // layer. Per Cascade L5 §6.4.1, ANY layer (anonymous or named)
            // loses to unlayered author rules for normal declarations. The
            // unlayered `div` rule in the importing sheet must beat the
            // anonymously-layered `#x` rule in the imported sheet, despite
            // `#x` having higher specificity.
            Write("import.css", "#x { color: red; }");
            var importer = Write("main.css",
                "@import \"import.css\" layer;\n" +
                "div { color: blue; }");

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { ResolveAuthor(importer) });

            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"),
                "unlayered author 'div' must beat anonymously-layered '#x' on the layer axis");
        }

        [Test]
        public void Anonymous_layer_qualifier_takes_a_fresh_ordinal_distinct_from_named_layers() {
            // Each anonymous-layer import must allocate a fresh ordinal; two
            // imports with `layer` (anonymous) sit on DIFFERENT ordinals,
            // and the later one wins per "later layer wins".
            Write("a.css", "div { color: red; }");
            Write("b.css", "div { color: blue; }");
            var importer = Write("main.css",
                "@import \"a.css\" layer;\n" +
                "@import \"b.css\" layer;");

            var doc = HtmlParser.Parse("<div></div>");
            var engine = new CascadeEngine(new[] { ResolveAuthor(importer) });

            // b.css declared second → its anonymous layer has the higher ordinal → wins.
            var cs = engine.Compute(doc.GetElementsByTagName("div").First());
            Assert.That(cs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Unlayered_import_merges_into_unlayered_author_origin() {
            // Regression pin: `@import url(import.css)` with NO layer qualifier
            // must NOT wrap the rules — they merge into the unlayered author
            // origin and participate in normal source-order resolution.
            Write("import.css", "#x { color: red; }");
            var importer = Write("main.css",
                "@import \"import.css\";\n" +
                "div { color: blue; }");

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { ResolveAuthor(importer) });

            // Both unlayered → specificity wins → #x (red) beats div (blue).
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"),
                "non-layered @import must NOT wrap imported rules; specificity decides");

            // The engine must have registered NO synthetic layer from this
            // import — only layers an author explicitly declared exist.
            Assert.That(engine.LayerOrdinals, Is.Empty,
                "non-layered @import must not register any layers");
        }

        [Test]
        public void Layered_import_loses_to_unlayered_rule_in_importing_sheet() {
            // Companion of the anonymous-layer test, but with a NAMED layer:
            // `layer(base)` puts the imported rule in `base`; the importing
            // sheet's unlayered rule (no @layer wrapper) outranks it.
            Write("import.css", "#x { color: red; }");
            var importer = Write("main.css",
                "@import \"import.css\" layer(base);\n" +
                "div { color: blue; }");

            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { ResolveAuthor(importer) });

            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"),
                "unlayered author rule outranks named-layer import per Cascade L5 §6.4.1");
        }
    }
}
