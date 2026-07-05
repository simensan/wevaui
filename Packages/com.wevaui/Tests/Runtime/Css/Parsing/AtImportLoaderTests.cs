using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Media;
using Weva.Diagnostics;
using Weva.Parsing;

namespace Weva.Tests.Css.Parsing {
    // Bug #252 — @import is parsed but never loaded. AtImportLoader is the
    // document-level driver that fetches + splices the imported sheet into
    // the importing sheet. These tests pin the loader's contract: source
    // order, relative-path resolution, cycle detection, media-query gating,
    // and nested-import traversal.
    public class AtImportLoaderTests {
        string tempRoot;

        [SetUp]
        public void Setup() {
            tempRoot = Path.Combine(Path.GetTempPath(), "weva-atimport-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            UICssDiagnostics.ResetForTests();
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

        static Stylesheet Parse(string s) => CssParser.Parse(s);

        // Collect every StyleRule selector in the order it appears in the
        // resolved sheet. Asserting on the *sequence* is the cleanest way to
        // pin source-order semantics from CSS Cascading L4 §6.4.1.
        static string[] Selectors(Stylesheet s) {
            return s.Rules.OfType<StyleRule>().SelectMany(r => r.Selectors).ToArray();
        }

        [Test]
        public void Basic_import_resolves_and_inlines_imported_rules() {
            var imported = Write("base.css", ".base { color: green; }");
            var importer = Write("main.css", "@import \"base.css\";\n.main { color: red; }");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".base", ".main" }));
        }

        [Test]
        public void Imported_rules_come_before_following_rules_in_source_order() {
            // CSS Cascading L4 §6.4.1: the imported sheet's rules cascade as if
            // they were written at the position of the @import. Same
            // specificity → later rule wins, so .x from the importer must win
            // over the same selector in the imported sheet.
            var imported = Write("a.css", ".x { color: green; }");
            var importer = Write("b.css", "@import \"a.css\";\n.x { color: red; }");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            var sels = Selectors(resolved);
            Assert.That(sels.Length, Is.EqualTo(2));
            Assert.That(sels[0], Is.EqualTo(".x"));
            Assert.That(sels[1], Is.EqualTo(".x"));
            // The second occurrence is the importer's rule (red).
            var rules = resolved.Rules.OfType<StyleRule>().ToList();
            Assert.That(rules[1].Declarations[0].ValueText, Is.EqualTo("red"));
            Assert.That(rules[0].Declarations[0].ValueText, Is.EqualTo("green"));
        }

        [Test]
        public void Relative_path_resolves_against_importing_sheet_directory() {
            Directory.CreateDirectory(Path.Combine(tempRoot, "sub"));
            var imported = Write("sub/theme.css", ".theme { color: purple; }");
            var importer = Write("sub/page.css", "@import \"theme.css\";");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".theme" }));
        }

        [Test]
        public void Nested_import_is_recursively_resolved() {
            // a.css imports b.css imports c.css. All three should land in the
            // final sheet in source order: c, b, a.
            var c = Write("c.css", ".c {}");
            var b = Write("b.css", "@import \"c.css\";\n.b {}");
            var a = Write("a.css", "@import \"b.css\";\n.a {}");

            var parsed = Parse(File.ReadAllText(a));
            var resolved = AtImportLoader.Resolve(parsed, a, DefaultMedia());

            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".c", ".b", ".a" }));
        }

        [Test]
        public void Cycle_between_two_sheets_is_detected_and_broken() {
            // A imports B; B imports A. The loader must not stack-overflow;
            // it should emit a one-time warning and skip the back-edge.
            var aPath = Path.Combine(tempRoot, "a.css");
            var bPath = Path.Combine(tempRoot, "b.css");
            File.WriteAllText(aPath, "@import \"b.css\";\n.a {}");
            File.WriteAllText(bPath, "@import \"a.css\";\n.b {}");

            var parsed = Parse(File.ReadAllText(aPath));
            var resolved = AtImportLoader.Resolve(parsed, aPath, DefaultMedia());

            // We expect both .a and .b to appear exactly once each. The
            // cycle-detection skip drops the second visit of a.css from
            // inside b.css, so .a is not duplicated.
            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".b", ".a" }));
            Assert.That(
                UICssDiagnostics.HasEmittedForTests("AtImportLoader", "cyclic @import detected, skipped: a.css"),
                Is.True,
                "expected cycle-detection warning for back-edge");
        }

        [Test]
        public void Media_query_suffix_gates_the_imported_sheet() {
            var imported = Write("big.css", ".big { color: red; }");
            var importer = Write("main.css", "@import \"big.css\" screen and (min-width: 1000px);");

            var parsed = Parse(File.ReadAllText(importer));

            // Viewport is wide enough → import applies.
            var wide = AtImportLoader.Resolve(parsed, importer, MediaContext.Default(1200, 800));
            Assert.That(Selectors(wide), Is.EqualTo(new[] { ".big" }));

            // Viewport is too narrow → import is dropped.
            var narrow = AtImportLoader.Resolve(parsed, importer, MediaContext.Default(600, 800));
            Assert.That(Selectors(narrow), Is.Empty);
        }

        [Test]
        public void Diamond_import_is_not_treated_as_a_cycle() {
            // A imports B and C; both B and C import D. D must load BOTH
            // times — only a back-edge (a → ancestor) is a cycle. This pins
            // the "remove from visited after recursion" branch.
            var d = Write("d.css", ".d {}");
            var b = Write("b.css", "@import \"d.css\";\n.b {}");
            var c = Write("c.css", "@import \"d.css\";\n.c {}");
            var a = Write("a.css", "@import \"b.css\";\n@import \"c.css\";");

            var parsed = Parse(File.ReadAllText(a));
            var resolved = AtImportLoader.Resolve(parsed, a, DefaultMedia());

            // Diamond: d's rules appear twice. The cascade absorbs the dupes
            // identically to two sibling style rules with the same selector;
            // the loader's job is just not to drop them.
            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".d", ".b", ".d", ".c" }));
        }

        [Test]
        public void Url_function_form_is_loaded() {
            // CSS allows both `@import "x.css"` and `@import url(x.css)` —
            // the tokenizer produces a Url token for the latter, and the
            // parser writes it into ImportRule.Href identically. Pin both
            // forms reach the loader.
            var imported = Write("base.css", ".base {}");
            var importer = Write("main.css", "@import url(\"base.css\");");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".base" }));
        }

        [Test]
        public void Remote_url_is_dropped_with_warning_not_crashed() {
            // v1 scope: no remote fetching. http:// hrefs must be a no-op,
            // not a File.ReadAllText("http://...") exception.
            var importer = Write("main.css", "@import url(\"https://example.com/x.css\");\n.local {}");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".local" }));
            Assert.That(
                UICssDiagnostics.HasEmittedForTests("AtImportLoader", "remote @import not supported (v1): https://example.com/x.css"),
                Is.True);
        }

        [Test]
        public void Missing_target_file_drops_import_and_keeps_rest() {
            var importer = Write("main.css", "@import \"nope.css\";\n.local {}");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            // The local rule survives; the unreadable import is logged + dropped.
            Assert.That(Selectors(resolved), Is.EqualTo(new[] { ".local" }));
        }

        [Test]
        public void Non_style_rules_in_imported_sheet_are_preserved() {
            // @media (and other non-style rules) in the imported sheet must
            // also make it into the resolved sheet. Selectors() only sees
            // top-level StyleRules, so we check Rule kinds directly.
            var imported = Write("base.css", "@media screen { .m {} }\n.base {}");
            var importer = Write("main.css", "@import \"base.css\";\n.main {}");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(resolved.Rules.Count, Is.EqualTo(3));
            Assert.That(resolved.Rules[0], Is.InstanceOf<MediaRule>());
            Assert.That(resolved.Rules[1], Is.InstanceOf<StyleRule>());
            Assert.That(((StyleRule)resolved.Rules[1]).Selectors[0], Is.EqualTo(".base"));
            Assert.That(((StyleRule)resolved.Rules[2]).Selectors[0], Is.EqualTo(".main"));
        }

        // C6b + C6d — the @import layer(...) and supports(...) qualifiers
        // were parsed but ignored at splice time. The loader now wraps
        // layered imports in a synthetic block-form LayerRule and gates
        // imports on the supports() condition before any disk hit.
        [Test]
        public void Layer_qualifier_wraps_imported_rules_in_named_layer() {
            Write("foo.css", ".btn { color: red; }");
            var importer = Write("main.css", "@import \"foo.css\" layer(utilities);");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(resolved.Rules, Has.Count.EqualTo(1));
            Assert.That(resolved.Rules[0], Is.InstanceOf<LayerRule>());
            var lr = (LayerRule)resolved.Rules[0];
            Assert.That(lr.IsBlock, Is.True);
            Assert.That(lr.Names, Is.EqualTo(new[] { "utilities" }));
            Assert.That(lr.Rules, Has.Count.EqualTo(1));
            Assert.That(((StyleRule)lr.Rules[0]).Selectors[0], Is.EqualTo(".btn"));
        }

        [Test]
        public void Anonymous_layer_qualifier_wraps_with_null_layer_name() {
            Write("foo.css", ".btn {}");
            var importer = Write("main.css", "@import \"foo.css\" layer;");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(resolved.Rules, Has.Count.EqualTo(1));
            var lr = (LayerRule)resolved.Rules[0];
            Assert.That(lr.IsBlock, Is.True);
            Assert.That(lr.Names[0], Is.Null);
            Assert.That(((StyleRule)lr.Rules[0]).Selectors[0], Is.EqualTo(".btn"));
        }

        [Test]
        public void Supports_qualifier_gates_import_on_condition_result() {
            Write("foo.css", ".btn { color: red; }");
            var supportedImporter = Write(
                "supported.css", "@import \"foo.css\" supports(display: grid);\n.local {}");
            var unsupportedImporter = Write(
                "unsupported.css", "@import \"foo.css\" supports(foo: bar);\n.local {}");

            var supportedResolved = AtImportLoader.Resolve(
                Parse(File.ReadAllText(supportedImporter)), supportedImporter, DefaultMedia());
            Assert.That(Selectors(supportedResolved), Is.EqualTo(new[] { ".btn", ".local" }));

            var unsupportedResolved = AtImportLoader.Resolve(
                Parse(File.ReadAllText(unsupportedImporter)), unsupportedImporter, DefaultMedia());
            Assert.That(Selectors(unsupportedResolved), Is.EqualTo(new[] { ".local" }));
        }

        [Test]
        public void Layer_and_supports_qualifiers_combine() {
            Write("foo.css", ".btn { color: red; }");
            var importer = Write(
                "main.css", "@import \"foo.css\" layer(base) supports(display: grid);");

            var parsed = Parse(File.ReadAllText(importer));
            var resolved = AtImportLoader.Resolve(parsed, importer, DefaultMedia());

            Assert.That(resolved.Rules, Has.Count.EqualTo(1));
            var lr = (LayerRule)resolved.Rules[0];
            Assert.That(lr.Names, Is.EqualTo(new[] { "base" }));
            Assert.That(((StyleRule)lr.Rules[0]).Selectors[0], Is.EqualTo(".btn"));

            // Same import with a failing supports() drops the entire splice —
            // including the would-be layer wrapper.
            var unsupported = Write(
                "fail.css", "@import \"foo.css\" layer(base) supports(foo: bar);");
            var unsupportedResolved = AtImportLoader.Resolve(
                Parse(File.ReadAllText(unsupported)), unsupported, DefaultMedia());
            Assert.That(unsupportedResolved.Rules, Is.Empty);
        }
    }
}
