using System;
using System.Collections.Generic;
using System.IO;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Components {
    // Resolves authoring-time component imports before ComponentRegistry scans
    // the document. This intentionally keeps the runtime component model the
    // same: imports only materialize ordinary <template id="..."> definitions.
    internal static class ComponentTemplateImporter {
        const int MaxDepth = 16;

        // Player builds pass BakedTemplateHrefs/Html as the bake fallback.
        // Resolution order mirrors the linked-stylesheet baker:
        //   1. Disk file (editor only — DocumentPath is null in players).
        //   2. Bake array (player builds; also used as disk-fallback when the
        //      file was moved between bake and load time).
        // When both DocumentPath and bake arrays are absent the importer warns
        // once and returns, producing a document with no expanded templates.
        public static void Resolve(Document doc, string documentPath, ParseOptions options,
            IList<string> importedPaths = null,
            IReadOnlyList<string> bakedTemplateHrefs = null,
            IReadOnlyList<string> bakedTemplateHtml = null) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (!ContainsTemplateImport(doc)) return;

            bool haveDisk = !string.IsNullOrEmpty(documentPath);
            bool haveBaked = bakedTemplateHrefs != null && bakedTemplateHtml != null
                && bakedTemplateHrefs.Count == bakedTemplateHtml.Count
                && bakedTemplateHrefs.Count > 0;

            if (!haveDisk && !haveBaked) {
                UICssDiagnostics.Warn("template-import",
                    "<template src=\"...\"> requires UIDocumentBuilder.DocumentPath (editor: disk resolve) " +
                    "or BakedTemplateHrefs/Html (player: build-time bake) so external templates can be resolved.");
                return;
            }

            string fullPath = haveDisk ? NormalizePath(documentPath) : null;
            var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fullPath != null) stack.Add(fullPath);
            ResolveNode(doc, fullPath, options ?? new ParseOptions(), stack, 0, inTemplate: false,
                importedPaths, bakedTemplateHrefs, bakedTemplateHtml);
        }

        // Build-tooling seam: returns the src hrefs of every <template src="...">
        // found in the document in document order (no dedup — the bake bakes each
        // distinct href once). Used by the editor's LinkedStylesheetBaker.
        public static List<string> CollectTemplateHrefs(string htmlSource) {
            var hrefs = new List<string>();
            if (string.IsNullOrEmpty(htmlSource)) return hrefs;
            var doc = HtmlParser.Parse(htmlSource, new ParseOptions { ThrowOnError = false });
            CollectTemplateHrefsFromNode(doc, hrefs);
            return hrefs;
        }

        static void CollectTemplateHrefsFromNode(Node node, List<string> hrefs) {
            if (node is Element el) {
                if (IsTemplate(el)) {
                    string src = el.GetAttribute("src");
                    if (!string.IsNullOrWhiteSpace(src)) {
                        if (!hrefs.Contains(src)) hrefs.Add(src);
                        // A src template's body is REPLACED on resolution —
                        // nothing inside it survives to need baking.
                        return;
                    }
                    // <template id> DEFINITION: descend so nested
                    // <template src> imports inside the body get baked too
                    // (the resolver now descends the same way).
                    for (int i = 0; i < el.Children.Count; i++)
                        CollectTemplateHrefsFromNode(el.Children[i], hrefs);
                    return;
                }
                for (int i = 0; i < el.Children.Count; i++)
                    CollectTemplateHrefsFromNode(el.Children[i], hrefs);
                return;
            }
            for (int i = 0; i < node.Children.Count; i++)
                CollectTemplateHrefsFromNode(node.Children[i], hrefs);
        }

        static void ResolveNode(Node node, string ownerPath, ParseOptions options, HashSet<string> stack, int depth, bool inTemplate,
            IList<string> importedPaths,
            IReadOnlyList<string> bakedHrefs, IReadOnlyList<string> bakedHtml) {
            var snapshot = new List<Node>(node.Children);
            for (int i = 0; i < snapshot.Count; i++) {
                var child = snapshot[i];
                if (child.Parent != node) continue;
                if (child is Element el) {
                    bool isTemplate = IsTemplate(el);
                    if (isTemplate) {
                        if (TryResolveTemplate(el, ownerPath, options, stack, depth,
                            importedPaths, bakedHrefs, bakedHtml)) {
                            continue;
                        }
                        // <template id> DEFINITION (no src, or src failed):
                        // descend into its body so NESTED <template src>
                        // imports resolve at import time and ride along with
                        // every expansion of the definition. The old
                        // !inTemplate gate left them unresolved on both the
                        // disk and bake paths (the residual limitation noted
                        // when A-LINK-PLAYER-TEMPLATES closed).
                        ResolveNode(el, ownerPath, options, stack, depth, inTemplate: true,
                            importedPaths, bakedHrefs, bakedHtml);
                        continue;
                    }
                    ResolveNode(el, ownerPath, options, stack, depth, inTemplate: false,
                        importedPaths, bakedHrefs, bakedHtml);
                }
            }
        }

        static bool TryResolveTemplate(Element target, string ownerPath, ParseOptions options,
            HashSet<string> stack, int depth, IList<string> importedPaths,
            IReadOnlyList<string> bakedHrefs, IReadOnlyList<string> bakedHtml) {
            string src = target.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(src)) return false;
            if (depth >= MaxDepth) {
                UICssDiagnostics.Warn("template-import", $"Maximum import depth exceeded at '{src}'.");
                return false;
            }

            // Attempt disk resolution first (editor path, always wins over bake).
            string source = null;
            string importedPath = null;
            bool loadedFromDisk = false;

            if (!string.IsNullOrEmpty(ownerPath)) {
                string candidate = ResolvePath(ownerPath, src);
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) {
                    importedPath = NormalizePath(candidate);
                    if (stack.Contains(importedPath)) {
                        UICssDiagnostics.Warn("template-import", $"Cyclic template import skipped for '{src}'.");
                        return false;
                    }
                    try {
                        source = File.ReadAllText(importedPath);
                        loadedFromDisk = true;
                    } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                        UICssDiagnostics.Warn("template-import", $"Could not read template import '{src}': {ex.Message}");
                        return false;
                    }
                }
            }

            // Bake fallback: used in player builds (no disk) or when disk file is missing.
            if (!loadedFromDisk) {
                source = LookupBaked(src, bakedHrefs, bakedHtml);
                if (source == null) {
                    UICssDiagnostics.Warn("template-import",
                        $"Could not resolve template import '{src}' (no disk file and not baked at build time).");
                    return false;
                }
                // Baked templates have no on-disk path; use null so nested imports
                // also fall through to the bake array rather than attempting File I/O.
                importedPath = null;
            } else {
                AddImportedPath(importedPaths, importedPath);
            }

            if (importedPath != null) stack.Add(importedPath);
            var imported = HtmlParser.Parse(source, options);
            // Recurse with the imported template's ownerPath (null for baked).
            ResolveNode(imported, importedPath, options, stack, depth + 1, inTemplate: false,
                importedPaths, bakedHrefs, bakedHtml);
            if (importedPath != null) stack.Remove(importedPath);

            var contentRoot = ImportContentRoot(imported);
            var topLevelTemplates = TopLevelTemplates(contentRoot);
            bool targetHasId = !string.IsNullOrEmpty(target.GetAttribute("id"));

            if (!targetHasId && topLevelTemplates.Count > 0 && target.Parent != null) {
                ReplacePlaceholderWithTemplates(target, topLevelTemplates);
                return true;
            }

            Element importedTemplate = topLevelTemplates.Count == 1 ? topLevelTemplates[0] : null;
            if (importedTemplate != null) {
                CopyMissingTemplateAttributes(importedTemplate, target);
                ReplaceChildren(target, importedTemplate.Children);
            } else {
                ReplaceChildren(target, contentRoot.Children);
                if (string.IsNullOrEmpty(target.GetAttribute("id"))) {
                    UICssDiagnostics.Warn("template-import",
                        $"Imported '{src}' into a <template> without id; it will not register as a component.");
                }
            }
            target.RemoveAttribute("src");
            return true;
        }

        // Returns the baked HTML for the given src href, or null if not found.
        static string LookupBaked(string src, IReadOnlyList<string> bakedHrefs, IReadOnlyList<string> bakedHtml) {
            if (bakedHrefs == null || bakedHtml == null) return null;
            for (int i = 0; i < bakedHrefs.Count; i++) {
                if (string.Equals(bakedHrefs[i], src, StringComparison.Ordinal)) {
                    return string.IsNullOrEmpty(bakedHtml[i]) ? null : bakedHtml[i];
                }
            }
            return null;
        }

        static void ReplacePlaceholderWithTemplates(Element placeholder, List<Element> templates) {
            var parent = placeholder.Parent;
            if (parent == null) return;
            for (int i = 0; i < templates.Count; i++) {
                templates[i].RemoveAttribute("src");
                parent.InsertBefore(templates[i], placeholder);
            }
            parent.RemoveChild(placeholder);
        }

        static void ReplaceChildren(Element target, IReadOnlyList<Node> importedChildren) {
            while (target.Children.Count > 0) {
                target.RemoveChild(target.Children[target.Children.Count - 1]);
            }
            var snapshot = new List<Node>(importedChildren);
            for (int i = 0; i < snapshot.Count; i++) {
                target.AppendChild(snapshot[i]);
            }
        }

        static void CopyMissingTemplateAttributes(Element source, Element target) {
            foreach (var kv in source.Attributes) {
                if (string.Equals(kv.Key, "src", StringComparison.OrdinalIgnoreCase)) continue;
                if (!target.HasAttribute(kv.Key)) target.SetAttribute(kv.Key, kv.Value);
            }
        }

        static void AddImportedPath(IList<string> importedPaths, string path) {
            if (importedPaths == null || string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < importedPaths.Count; i++) {
                if (string.Equals(importedPaths[i], path, StringComparison.OrdinalIgnoreCase)) return;
            }
            importedPaths.Add(path);
        }

        static Node ImportContentRoot(Document imported) {
            if (imported.Children.Count == 1
                && imported.Children[0] is Element html
                && string.Equals(html.TagName, "html", StringComparison.OrdinalIgnoreCase)) {
                for (int i = 0; i < html.Children.Count; i++) {
                    if (html.Children[i] is Element body
                        && string.Equals(body.TagName, "body", StringComparison.OrdinalIgnoreCase)) {
                        return body;
                    }
                }
            }
            return imported;
        }

        static List<Element> TopLevelTemplates(Node root) {
            var result = new List<Element>();
            for (int i = 0; i < root.Children.Count; i++) {
                if (root.Children[i] is Element el && IsTemplate(el)) result.Add(el);
            }
            return result;
        }

        static bool ContainsTemplateImport(Node node) {
            if (node is Element el && IsTemplate(el) && !string.IsNullOrWhiteSpace(el.GetAttribute("src"))) {
                return true;
            }
            for (int i = 0; i < node.Children.Count; i++) {
                if (ContainsTemplateImport(node.Children[i])) return true;
            }
            return false;
        }

        static bool IsTemplate(Element el) =>
            el != null && string.Equals(el.TagName, "template", StringComparison.OrdinalIgnoreCase);

        static string ResolvePath(string ownerPath, string src) {
            try {
                if (Path.IsPathRooted(src)) return src;
                var dir = Path.GetDirectoryName(ownerPath);
                return string.IsNullOrEmpty(dir) ? src : Path.Combine(dir, src);
            } catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) {
                // EC13: by-design swallow — Path.IsPathRooted / GetDirectoryName
                // / Combine throw on malformed paths (e.g. invalid chars). The
                // caller already warns "Could not resolve template import" but
                // that message conflates "bad path syntax" with "file missing";
                // surface the actual exception so the author can distinguish.
                WarnResolvePathFailure(src, ex);
                return null;
            }
        }

        // EC13 test seam: lets the regression test drive the catch branch
        // directly so we don't need to rely on the BCL Path methods throwing
        // for a specific invalid char (which varies across .NET / Mono /
        // IL2CPP). Not part of the production contract.
        internal static string SimulateResolvePathFailureForTests(string src, Exception ex) {
            WarnResolvePathFailure(src, ex);
            return null;
        }

        // EC13 — dedupe per offending src so a malformed path in a frequently-
        // re-instantiated template doesn't spam the console. Process-static
        // mirrors the ColorResolver DD2/DD3 pattern.
        static readonly HashSet<string> s_WarnedResolveKeys = new HashSet<string>();

        static void WarnResolvePathFailure(string src, Exception ex) {
            string key = "EC13:" + (src ?? "");
            lock (s_WarnedResolveKeys) {
                if (!s_WarnedResolveKeys.Add(key)) return;
            }
            UICssDiagnostics.Warn(
                "template-import",
                "EC13: malformed import path '" + (src ?? "") + "' (" +
                (ex?.GetType().Name ?? "Exception") +
                "); template import will be dropped.");
        }

        // Test hook — wipes the dedupe set so a re-running test can observe a
        // warning that was already emitted by an earlier test in the same
        // session. Not part of the production contract.
        internal static void ResetWarnings_TestOnly() {
            lock (s_WarnedResolveKeys) s_WarnedResolveKeys.Clear();
            UICssDiagnostics.ResetForTests();
        }

        static string NormalizePath(string path) {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }
    }
}
