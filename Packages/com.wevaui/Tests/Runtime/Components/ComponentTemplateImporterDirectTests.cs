using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Components;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Components {
    // TG23 — direct unit coverage for ComponentTemplateImporter that exercises
    // the four behaviors called out in CODE_AUDIT_FINDINGS.md and which are
    // NOT covered by ComponentTemplateImportTests (high-level builder smoke)
    // or ComponentTemplateImporterEC13DiagnosticTests (the malformed-path
    // catch branch):
    //
    //   * relative-path normalisation, including "../sibling.html" climbing
    //     out of a subdirectory
    //   * cycle detection (parent->child->parent) emitting the documented
    //     "Cyclic template import skipped" diagnostic without recursing forever
    //   * MaxDepth=16 cap firing on a deeper chain with the documented
    //     "Maximum import depth exceeded" diagnostic
    //   * the missing-DocumentPath fallback diagnostic
    //   * recursive resolution (depth-2 chain inlines the leaf correctly)
    //
    // Implementation reality: ResolveNode does NOT descend INTO an element it
    // identified as <template> — once IsTemplate is true the recursion
    // continues at sibling level. That means a chain only deepens when each
    // imported file's TOP-LEVEL contains the next <template src="...">, not
    // when one template's body contains the next. Tests use that flat shape.
    //
    // Tests drive ComponentTemplateImporter.Resolve directly rather than
    // routing through UIDocumentBuilder so the behavior under test isn't
    // entangled with ComponentRegistry/ComponentExpander side effects.
    //
    // Note on diagnostics: UICssDiagnostics.Warn is gated on
    // UNITY_EDITOR || DEVELOPMENT_BUILD. Under the headless TestVerifyAll
    // runner neither symbol is defined, so HasEmittedForTests will return
    // false for every emission. The diagnostic-assertion tests here are
    // expected to pass in the Unity Editor test runner (the canonical
    // environment) — they share that environment requirement with
    // ComponentTemplateImporterEC13DiagnosticTests.
    public class ComponentTemplateImporterDirectTests {
        string tempDir;

        [SetUp]
        public void SetUp() {
            tempDir = Path.Combine(
                Path.GetTempPath(),
                "weva-tg23-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            // Clear the diagnostics dedupe set so warnings from earlier tests
            // in the session don't suppress assertions here.
            ComponentTemplateImporter.ResetWarnings_TestOnly();
        }

        [TearDown]
        public void TearDown() {
            if (tempDir != null && Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        // --- helpers ---------------------------------------------------------

        static Document Parse(string html) =>
            HtmlParser.Parse(html, new ParseOptions { ThrowOnError = false });

        string WriteFile(string relativeName, string contents) {
            string full = Path.Combine(tempDir, relativeName);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, contents);
            return full;
        }

        // Locate the only <main> in the document — HtmlParser wraps fragments
        // in synthetic <html><body>, so direct indexing of doc.Children would
        // hit the <html>, not the user's <main>.
        static Element MainOf(Document doc) =>
            doc.GetElementsByTagName("main").Single();

        // --- tests -----------------------------------------------------------

        [Test]
        public void Simple_template_src_inlines_child_content() {
            string mainPath = Path.Combine(tempDir, "menu.html");
            WriteFile("card.html",
                "<template id=\"card\"><article class=\"k\"><slot></slot></article></template>");

            var doc = Parse("<main><template src=\"card.html\"></template></main>");
            ComponentTemplateImporter.Resolve(doc, mainPath, new ParseOptions());

            var main = MainOf(doc);
            Assert.That(main.Children.Count, Is.EqualTo(1));
            var tpl = (Element)main.Children[0];
            Assert.That(tpl.TagName, Is.EqualTo("template"));
            Assert.That(tpl.GetAttribute("id"), Is.EqualTo("card"),
                "Imported template's id should be copied onto the placeholder.");
            Assert.That(tpl.HasAttribute("src"), Is.False,
                "src attribute should be stripped after a successful import.");
            var article = (Element)tpl.Children[0];
            Assert.That(article.TagName, Is.EqualTo("article"));
            Assert.That(article.GetAttribute("class"), Is.EqualTo("k"));
        }

        [Test]
        public void Recursive_depth2_chain_inlines_leaf_into_root() {
            // ResolveNode doesn't descend through a <template>'s children
            // (templates are inert content), so each layer of the chain must
            // be expressed as a TOP-LEVEL <template src="..."> in its file.
            // mid.html therefore is a flat sibling-level template that
            // imports leaf.html.
            string rootPath = Path.Combine(tempDir, "root.html");
            WriteFile("mid.html",
                "<template src=\"leaf.html\"></template>");
            WriteFile("leaf.html",
                "<template id=\"leaf\"><span class=\"leaf-mark\">leaf</span></template>");

            var doc = Parse("<main><template src=\"mid.html\"></template></main>");
            ComponentTemplateImporter.Resolve(doc, rootPath, new ParseOptions());

            // After both layers resolve, the leaf marker should be reachable
            // from the resolved tree. The exact wrapper template depth is
            // not part of the contract under test — surfacing the leaf is.
            var leafSpans = doc.GetElementsByTagName("span")
                .Where(e => e.GetAttribute("class") == "leaf-mark")
                .ToList();
            Assert.That(leafSpans.Count, Is.EqualTo(1),
                "depth-2 import chain (root -> mid -> leaf) should surface " +
                "exactly one leaf <span> in the resolved tree.");

            // And the leaf template's id should reach the resolved subtree as
            // a registered top-level template, confirming the chain didn't
            // truncate at the mid layer.
            var leafTemplate = doc.GetElementsByTagName("template")
                .FirstOrDefault(e => e.GetAttribute("id") == "leaf");
            Assert.That(leafTemplate, Is.Not.Null,
                "Leaf template id must propagate through the depth-2 chain.");
        }

        [Test]
        public void Relative_path_in_child_resolves_relative_to_child_not_root() {
            // Layout:
            //   tempDir/root.html        — main doc (only its path is used)
            //   tempDir/nested/mid.html  — imports "../sibling.html"
            //   tempDir/sibling.html     — the actual leaf
            // If the importer mistakenly normalised "../sibling.html" against
            // root.html's directory it would resolve to tempDir/../sibling.html
            // (outside tempDir) and File.Exists would fail. Flat shape per
            // the note above: mid.html is a top-level <template src=...>.
            string rootPath = Path.Combine(tempDir, "root.html");
            WriteFile("nested/mid.html",
                "<template src=\"../sibling.html\"></template>");
            WriteFile("sibling.html",
                "<template id=\"sibling\"><b class=\"sib-mark\">ok</b></template>");

            var doc = Parse("<main><template src=\"nested/mid.html\"></template></main>");
            ComponentTemplateImporter.Resolve(doc, rootPath, new ParseOptions());

            var hits = doc.GetElementsByTagName("b")
                .Where(e => e.GetAttribute("class") == "sib-mark")
                .ToList();
            Assert.That(hits.Count, Is.EqualTo(1),
                "Relative src in the child should resolve against the child's own directory, " +
                "letting '../sibling.html' climb out of /nested/ and find /sibling.html.");
        }

        [Test]
        public void Cycle_parent_imports_child_imports_parent_emits_diagnostic_and_does_not_recurse_forever() {
            // The OUTER Resolve(doc, parentPath, ...) seeds the recursion
            // stack with parentPath. parent's <main> contains
            //   <template src="child.html">.
            // child.html top-level is <template src="parent.html"> (flat,
            // not nested per the recursion-skip note above). When child's
            // imported document is walked at depth+1 the inner template
            // tries to import parent.html — that normalized path is already
            // in the stack -> "Cyclic template import skipped".
            string parentPath = Path.Combine(tempDir, "parent.html");
            WriteFile("child.html",
                "<template src=\"parent.html\"></template>");
            // We also write a stub parent.html on disk so File.Exists returns
            // true and we actually exercise the cycle branch (not the
            // "Could not resolve" branch).
            WriteFile("parent.html", "<main></main>");

            LogAssert.Expect(LogType.Warning,
                new Regex(@"template-import.*Cyclic template import skipped for 'parent\.html'"));

            var doc = Parse("<main><template src=\"child.html\"></template></main>");
            // Must not loop forever, must not stack-overflow, and must surface
            // the documented diagnostic.
            Assert.DoesNotThrow(() =>
                ComponentTemplateImporter.Resolve(doc, parentPath, new ParseOptions()),
                "Cycle detection must short-circuit recursion, not stack-overflow.");

            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "template-import",
                    "Cyclic template import skipped for 'parent.html'."),
                Is.True,
                "Cycle must surface the documented 'Cyclic template import skipped' warning.");
        }

        [Test]
        public void MaxDepth_chain_of_20_levels_stops_at_cap_with_diagnostic() {
            // Build lvl1..lvl20 where each lvl{n} is a flat top-level
            // <template src="lvl{n+1}.html">. The deepest level inlines a
            // marker span and has no further import. With MaxDepth=16 the
            // chain must cap before lvl20 is inlined.
            const int Levels = 20;
            for (int i = 1; i <= Levels; i++) {
                string contents;
                if (i < Levels) {
                    contents = "<template src=\"lvl" + (i + 1) + ".html\"></template>";
                } else {
                    contents = "<template id=\"lvl" + i + "\"><span class=\"deepest\"/></template>";
                }
                WriteFile("lvl" + i + ".html", contents);
            }
            string rootPath = Path.Combine(tempDir, "root.html");

            // Match any lvlN past the cap — the exact N depends on the
            // off-by-one between "depth of the call site" and "depth of the
            // file being imported", which is an implementation detail rather
            // than a public contract.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"template-import.*Maximum import depth exceeded at 'lvl\d+\.html'"));

            var doc = Parse("<main><template src=\"lvl1.html\"></template></main>");
            Assert.DoesNotThrow(() =>
                ComponentTemplateImporter.Resolve(doc, rootPath, new ParseOptions()),
                "Depth cap must short-circuit recursion, not stack-overflow.");

            // The marker span from lvl20 must be absent from the resolved
            // tree — the cap stopped the chain before that file was inlined.
            var deepest = doc.GetElementsByTagName("span")
                .Where(e => e.GetAttribute("class") == "deepest")
                .ToList();
            Assert.That(deepest.Count, Is.EqualTo(0),
                "Depth cap must stop the chain before the 20th file is inlined.");

            // Probe the documented diagnostic shape for any lvlN past the cap.
            bool anyCapWarning = false;
            for (int i = 1; i <= Levels; i++) {
                if (UICssDiagnostics.HasEmittedForTests(
                        "template-import",
                        "Maximum import depth exceeded at 'lvl" + i + ".html'.")) {
                    anyCapWarning = true;
                    break;
                }
            }
            Assert.That(anyCapWarning, Is.True,
                "At least one 'Maximum import depth exceeded' diagnostic must surface.");
        }

        [Test]
        public void Missing_DocumentPath_with_template_import_emits_fallback_diagnostic() {
            // Per the implementation: when the doc CONTAINS a <template src=...>
            // but no DocumentPath was supplied, Resolve early-outs with a
            // single explanatory warning. Without a src= the early-out is
            // skipped entirely (no warning needed), so the fixture must
            // contain at least one import for the diagnostic to fire.
            LogAssert.Expect(LogType.Warning,
                new Regex(
                    @"template-import.*<template src=""\.\.\.""> requires UIDocumentBuilder\.DocumentPath"));

            var doc = Parse("<main><template src=\"whatever.html\"></template></main>");
            ComponentTemplateImporter.Resolve(doc, documentPath: null, options: new ParseOptions());

            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "template-import",
                    "<template src=\"...\"> requires UIDocumentBuilder.DocumentPath (editor: disk resolve) " +
                    "or BakedTemplateHrefs/Html (player: build-time bake) so external templates can be resolved."),
                Is.True,
                "Missing DocumentPath must surface the documented fallback diagnostic.");

            // And no part of the tree should have been mutated — the original
            // <template src="whatever.html"> stub should still carry its src.
            var tpl = (Element)MainOf(doc).Children[0];
            Assert.That(tpl.HasAttribute("src"), Is.True);
            Assert.That(tpl.GetAttribute("src"), Is.EqualTo("whatever.html"));
        }

        [Test]
        public void Missing_DocumentPath_without_any_import_is_silent_noop() {
            // Belt-and-braces: the early `ContainsTemplateImport(doc)` guard
            // means a doc with NO <template src=...> shouldn't emit anything,
            // even when DocumentPath is null. This locks in the documented
            // fast path so a future refactor can't accidentally turn every
            // imageless doc into a warning.
            var doc = Parse("<main><div>plain</div></main>");
            ComponentTemplateImporter.Resolve(doc, documentPath: null, options: new ParseOptions());

            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "template-import",
                    "<template src=\"...\"> requires UIDocumentBuilder.DocumentPath (editor: disk resolve) " +
                    "or BakedTemplateHrefs/Html (player: build-time bake) so external templates can be resolved."),
                Is.False);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
