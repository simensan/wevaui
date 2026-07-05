using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Weva.Css;

namespace Weva.Tests.Css.Parsing {
    // Tests for CssImportFlattener — the build-time @import pre-flattener
    // that makes baked linked CSS sheets self-contained so AtImportLoader
    // (which needs a disk base path) can be a no-op in players.
    //
    // Tests write to temp directories and clean up in TearDown.
    // The injected fileReader is File.ReadAllText in production; these tests
    // use the real FS so path resolution works correctly on all platforms.
    public class CssImportFlattenerTests {
        string tempDir;

        [SetUp]
        public void SetUp() {
            tempDir = Path.Combine(Path.GetTempPath(),
                "weva-import-flatten-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown() {
            if (tempDir != null && Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        // Helper: write a file into tempDir and return its absolute path.
        string Write(string relativeName, string content) {
            string path = Path.Combine(tempDir, relativeName);
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            return path;
        }

        // Helper: flatten using File.ReadAllText as the production reader.
        static string Flatten(string cssText, string cssFilePath) {
            return CssImportFlattener.Flatten(cssText, cssFilePath, File.ReadAllText);
        }

        // ---------------------------------------------------------------
        // Basic inlining — string forms
        // ---------------------------------------------------------------

        [Test]
        public void Single_import_double_quote_is_inlined() {
            string basePath = Write("base.css", ".base { font-size: 14px; }");
            string mainPath = Write("main.css", $"@import \"base.css\"; .a {{ color: red; }}");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain(".base { font-size: 14px; }"),
                "imported base.css must be inlined");
            Assert.That(result, Does.Not.Contain("@import"),
                "no @import statement must remain in the flattened output");
            Assert.That(result, Does.Contain(".a { color: red; }"),
                "rules after the @import must be preserved");
        }

        [Test]
        public void Single_import_url_double_quote_is_inlined() {
            Write("vars.css", ":root { --c: blue; }");
            string mainPath = Write("main.css", "@import url(\"vars.css\"); .b { margin: 0; }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain("--c: blue"),
                "url()-form double-quote @import must be inlined");
            Assert.That(result, Does.Not.Contain("@import"));
        }

        [Test]
        public void Single_import_url_bare_is_inlined() {
            Write("vars.css", ":root { --x: 1; }");
            string mainPath = Write("main.css", "@import url(vars.css); .c { padding: 0; }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain("--x: 1"),
                "bare url()-form @import must be inlined");
        }

        [Test]
        public void Single_import_single_quote_is_inlined() {
            Write("utils.css", "* { box-sizing: border-box; }");
            string mainPath = Write("styles.css", "@import 'utils.css'; body { margin: 0; }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain("box-sizing: border-box"),
                "single-quote @import must be inlined");
        }

        // ---------------------------------------------------------------
        // Nested (chained) imports
        // ---------------------------------------------------------------

        [Test]
        public void Nested_import_chain_is_fully_resolved() {
            // a.css @imports b.css which @imports c.css
            Write("c.css", ".c { color: green; }");
            Write("b.css", "@import \"c.css\"; .b { }");
            string aPath = Write("a.css", "@import \"b.css\"; .a { }");

            string result = Flatten(File.ReadAllText(aPath), aPath);

            Assert.That(result, Does.Contain(".c { color: green; }"),
                "transitively imported c.css must be inlined");
            Assert.That(result, Does.Contain(".b { }"),
                "b.css rules must be inlined");
            Assert.That(result, Does.Contain(".a { }"),
                "a.css rules must be preserved");
            Assert.That(result, Does.Not.Contain("@import"),
                "no @import must remain after full flattening");
        }

        // ---------------------------------------------------------------
        // Media query wrapping (CSS Cascade 4 §6)
        // ---------------------------------------------------------------

        [Test]
        public void Import_with_media_query_wraps_content_in_at_media() {
            Write("print.css", ".print-only { display: block; }");
            string mainPath = Write("main.css", "@import \"print.css\" print; body { }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain("@media print"),
                "a media-query @import must be wrapped in @media");
            Assert.That(result, Does.Contain(".print-only { display: block; }"),
                "the imported content must appear inside the @media block");
            Assert.That(result, Does.Not.Contain("@import"),
                "the @import statement itself must be removed");
        }

        [Test]
        public void Import_with_compound_media_query_wraps_correctly() {
            Write("wide.css", ".wide { width: 100%; }");
            string mainPath = Write("main.css",
                "@import \"wide.css\" screen and (min-width: 600px); .x { }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain("@media screen and (min-width: 600px)"),
                "compound media query must be preserved in the @media wrapper");
            Assert.That(result, Does.Contain(".wide { width: 100%; }"));
        }

        // ---------------------------------------------------------------
        // Cycle detection
        // ---------------------------------------------------------------

        [Test]
        public void Cyclic_import_is_dropped_without_throwing() {
            // a.css @imports b.css which @imports a.css (cycle).
            Write("b.css", "@import \"a.css\"; .b { }");
            string aPath = Write("a.css", "@import \"b.css\"; .a { }");

            string result = null;
            Assert.DoesNotThrow(() => {
                result = Flatten(File.ReadAllText(aPath), aPath);
            }, "a cyclic @import must not throw");

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Does.Contain(".b { }"),
                "b.css rules inlined before cycle detection must be preserved");
            // The cycle back-edge (a.css re-imported by b.css) is dropped;
            // a.css rules must not be doubled.
            int aCount = CountOccurrences(result, ".a { }");
            Assert.That(aCount, Is.EqualTo(1),
                ".a rules must appear exactly once (cycle back-edge dropped)");
        }

        [Test]
        public void Self_referential_import_is_dropped_gracefully() {
            string aPath = Write("a.css", "@import \"a.css\"; .a { color: red; }");

            string result = null;
            Assert.DoesNotThrow(() => {
                result = Flatten(File.ReadAllText(aPath), aPath);
            });

            Assert.That(result, Does.Contain(".a { color: red; }"),
                "the main file's own rules must survive the self-referential import");
            Assert.That(result, Does.Not.Contain("@import"),
                "the self-referential @import must be dropped");
        }

        // ---------------------------------------------------------------
        // Missing file handling
        // ---------------------------------------------------------------

        [Test]
        public void Missing_imported_file_drops_that_import_only() {
            // base.css exists; missing.css does not.
            Write("base.css", ".base { }");
            string mainPath = Write("main.css",
                "@import \"missing.css\"; @import \"base.css\"; .r { }");

            string result = null;
            Assert.DoesNotThrow(() => {
                result = Flatten(File.ReadAllText(mainPath), mainPath);
            }, "a missing import target must not throw");

            Assert.That(result, Does.Contain(".base { }"),
                "the present import must still be inlined");
            Assert.That(result, Does.Contain(".r { }"),
                "rules after the missing import must be preserved");
        }

        // ---------------------------------------------------------------
        // Relative path resolution
        // ---------------------------------------------------------------

        [Test]
        public void Relative_path_resolves_against_importing_file_directory() {
            // main.css is in styles/, reset.css is in shared/ (sibling of styles/).
            Write("shared/reset.css", "* { margin: 0; }");
            string mainPath = Write("styles/main.css", "@import \"../shared/reset.css\"; .x { }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            Assert.That(result, Does.Contain("* { margin: 0; }"),
                "relative path with ../ must resolve against the importing file's directory");
        }

        [Test]
        public void Nested_import_relative_path_resolves_against_imported_file() {
            // top.css in root, imports sub/a.css which imports ../shared/b.css
            // ../shared/b.css is relative to sub/a.css → root/shared/b.css.
            Write("shared/b.css", ".shared { }");
            Write("sub/a.css", "@import \"../shared/b.css\"; .sub { }");
            string topPath = Write("top.css", "@import \"sub/a.css\"; .top { }");

            string result = Flatten(File.ReadAllText(topPath), topPath);

            Assert.That(result, Does.Contain(".shared { }"),
                "a relative path in a nested @import must resolve against " +
                "the importing file's own directory");
        }

        // ---------------------------------------------------------------
        // Depth cap
        // ---------------------------------------------------------------

        [Test]
        public void Import_depth_cap_prevents_unbounded_recursion() {
            int maxDepth = CssImportFlattener.MaxImportDepth;
            int limit = maxDepth + 4; // clearly beyond the cap

            // Build a chain: 0.css @import 1.css, 1.css @import 2.css, etc.
            for (int i = 0; i <= limit; i++) {
                int next = i + 1;
                Write($"{i}.css", $"@import \"{next}.css\"; .d{i} {{ }}");
            }
            Write($"{limit + 1}.css", ".terminal { }");

            string topPath = Path.Combine(tempDir, "0.css");
            string result = null;
            Assert.DoesNotThrow(() => {
                result = Flatten(File.ReadAllText(topPath), topPath);
            }, "depth-cap must not throw, just stop recursing");

            Assert.That(result, Does.Contain(".d0 {"),
                "rules from the root file must be preserved");
            Assert.That(result, Does.Not.Contain(".terminal"),
                "the file beyond the depth cap must not be inlined");
        }

        // ---------------------------------------------------------------
        // Remote hrefs pass through unchanged
        // ---------------------------------------------------------------

        [Test]
        public void Remote_import_is_passed_through_unchanged() {
            string mainPath = Write("main.css",
                "@import url(\"https://cdn.example/reset.css\"); .local { }");

            string result = Flatten(File.ReadAllText(mainPath), mainPath);

            // Remote @import must remain in the output for AtImportLoader's
            // runtime warn-and-drop path to fire.
            Assert.That(result, Does.Contain("@import"),
                "remote @import must be left in the output");
            Assert.That(result, Does.Contain("cdn.example"),
                "the remote href must be preserved");
            Assert.That(result, Does.Contain(".local { }"),
                "local rules must still be in the output");
        }

        // ---------------------------------------------------------------
        // No @import — identity
        // ---------------------------------------------------------------

        [Test]
        public void Css_without_import_is_returned_unchanged() {
            const string css = ".card { width: 100px; } .btn { background: blue; }";
            string mainPath = Write("main.css", css);

            string result = Flatten(css, mainPath);

            Assert.That(result, Is.EqualTo(css),
                "CSS without any @import must come back byte-identical");
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        static int CountOccurrences(string haystack, string needle) {
            int count = 0;
            int idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
