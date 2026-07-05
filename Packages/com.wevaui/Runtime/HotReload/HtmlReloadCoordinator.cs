using System;
using System.Collections.Generic;
using System.IO;
using Weva.Components;
using Weva.Css.Values;
using Weva.Documents;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.HotReload {
    // Drains the HtmlReloadQueue once per frame, re-parses the changed HTML
    // file, and applies a minimal diff to the live document so the rest of
    // the pipeline (cascade, layout, paint) reacts via the InvalidationTracker
    // already attached to the document.
    //
    // The coordinator's MarkAllElementsDirty fallback exists so any test that
    // wants the whole document re-cascaded after an HTML reload (the v0.7
    // semantics for CSS) still gets that behaviour. Real diff-driven dirtying
    // happens via Document.Mutated -> InvalidationTracker on the individual
    // SetAttribute / AppendChild / RemoveChild calls inside DomDiffer.
    //
    // Failure handling mirrors HotReloadCoordinator: read or parse error
    // logs and bails, leaving the previous DOM intact.
    public sealed class HtmlReloadCoordinator {
        readonly UIDocumentState state;
        readonly HtmlReloadQueue queue;
        readonly Action<string> log;
        readonly Dictionary<string, double> lastReloadAt = new(StringComparer.OrdinalIgnoreCase);
        const double DebounceSeconds = 0.05;
        int reloadCount;

        public HtmlReloadCoordinator(UIDocumentState state, HtmlReloadQueue queue, Action<string> log = null) {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.log = log;
        }

        public int ReloadCount => reloadCount;

        public bool Tick(double nowSeconds) {
            if (queue.Count == 0) return false;
            var pending = queue.Drain();
            if (pending.Count == 0) return false;

            bool anyApplied = false;
            for (int i = 0; i < pending.Count; i++) {
                var path = pending[i];
                if (string.IsNullOrEmpty(path)) continue;
                if (lastReloadAt.TryGetValue(path, out var prev) && (nowSeconds - prev) < DebounceSeconds) {
                    continue;
                }
                lastReloadAt[path] = nowSeconds;
                if (TryReloadOne(path)) {
                    anyApplied = true;
                }
            }

            if (anyApplied) {
                reloadCount++;
            }
            return anyApplied;
        }

        bool TryReloadOne(string path) {
            if (string.IsNullOrEmpty(state.DocumentPath)) {
                Log($"[weva] html-hot-reload: '{path}' is not registered with this document; ignoring");
                return false;
            }
            string normalizedRegistered;
            try { normalizedRegistered = Path.GetFullPath(state.DocumentPath); } catch { normalizedRegistered = state.DocumentPath; }
            string normalizedIncoming;
            try { normalizedIncoming = Path.GetFullPath(path); } catch { normalizedIncoming = path; }
            if (!IsRegisteredHtmlPath(normalizedIncoming, normalizedRegistered)) {
                Log($"[weva] html-hot-reload: '{path}' is not registered with this document; ignoring");
                return false;
            }

            string source;
            try {
                source = File.ReadAllText(state.DocumentPath);
            } catch (Exception ex) {
                Log($"[weva] html-hot-reload: read failed for '{state.DocumentPath}': {ex.Message}");
                return false;
            }

            Document fresh;
            try {
                fresh = HtmlParser.Parse(source, new ParseOptions { ThrowOnError = false });
            } catch (Exception ex) {
                Log($"[weva] html-hot-reload: parse failed for '{path}': {ex.Message}; keeping previous DOM");
                return false;
            }

            // Component expansion runs on the fresh parsed tree so the diff
            // operates against the same expanded shape the live tree already has.
            try {
                var importedPaths = new List<string>();
                ComponentTemplateImporter.Resolve(
                    fresh,
                    state.DocumentPath,
                    new ParseOptions { ThrowOnError = false },
                    importedPaths);
                if (state.Components != null) {
                    var components = RebuildComponentsForFreshDocument(fresh);
                    new ComponentExpander(components).Expand(fresh);
                    state.Components = components;
                }
                RefreshTemplateImportWatchers(importedPaths);
                state.ComponentTemplatePaths = importedPaths;
            } catch (Exception ex) {
                Log($"[weva] html-hot-reload: component expansion failed: {ex.Message}");
                return false;
            }

            bool mutated = DomDiffer.ApplyDocumentDiff(state.Doc, fresh);
            if (mutated) {
                // Clear any cached layout root so the next Update rebuilds.
                // The cascade engine's Document.Mutated subscription has
                // already flipped its snapshotDirty flag during the diff.
                state.RootBox = null;
                state.Painter?.InvalidateAll();
                state.PaintInvalidated = true;
                state.HasEmittedPaint = false;
                // Component-scoped stylesheets may have been swapped in
                // RebuildComponentsForFreshDocument above; mirror the
                // CSS-reload negative-cache invalidation so a previously
                // malformed declaration in a component sheet that the
                // author has now fixed re-parses on the next pass.
                // See DD4 in CODE_AUDIT_FINDINGS.md.
                CssValue.InvalidateNegativeCache();
            }
            Log($"[weva] html-hot-reload: applied '{path}' (mutated={mutated})");
            return true;
        }

        bool IsRegisteredHtmlPath(string normalizedIncoming, string normalizedDocumentPath) {
            if (string.Equals(normalizedDocumentPath, normalizedIncoming, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            var imports = state.ComponentTemplatePaths;
            if (imports == null) return false;
            for (int i = 0; i < imports.Count; i++) {
                string normalizedImport;
                try { normalizedImport = Path.GetFullPath(imports[i]); } catch { normalizedImport = imports[i]; }
                if (string.Equals(normalizedImport, normalizedIncoming, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        void RefreshTemplateImportWatchers(List<string> importedPaths) {
            var watcher = state.HtmlWatcher;
            if (watcher == null) return;
            var previous = state.ComponentTemplatePaths;
            if (previous != null) {
                for (int i = 0; i < previous.Count; i++) {
                    if (!ContainsPath(importedPaths, previous[i])) watcher.Unwatch(previous[i]);
                }
            }
            if (importedPaths != null) {
                for (int i = 0; i < importedPaths.Count; i++) watcher.Watch(importedPaths[i]);
            }
            if (!string.IsNullOrEmpty(state.DocumentPath)) watcher.Watch(state.DocumentPath);
        }

        static bool ContainsPath(List<string> paths, string path) {
            if (paths == null || string.IsNullOrEmpty(path)) return false;
            string normalized;
            try { normalized = Path.GetFullPath(path); } catch { normalized = path; }
            for (int i = 0; i < paths.Count; i++) {
                string p;
                try { p = Path.GetFullPath(paths[i]); } catch { p = paths[i]; }
                if (string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        ComponentRegistry RebuildComponentsForFreshDocument(Document fresh) {
            var next = new ComponentRegistry();
            next.RegisterAllFromDocument(fresh);

            var previous = state.Components;
            if (previous == null) return next;

            foreach (var name in previous.RegisteredNames) {
                previous.TryGet(name, out var previousTemplate);
                previous.TryGetStylesheet(name, out var previousStylesheet);

                if (next.TryGet(name, out var freshTemplate)) {
                    if (previousStylesheet != null) {
                        next.Register(name, freshTemplate, previousStylesheet.Original);
                    }
                    continue;
                }

                // Preserve code-registered components whose templates are not
                // part of the HTML document. Document-owned templates are dropped
                // when absent from the fresh parse so stale definitions cannot
                // expand newly reloaded markup.
                if (previousTemplate != null && previousTemplate.Parent == null) {
                    if (previousStylesheet != null) {
                        next.Register(name, previousTemplate, previousStylesheet.Original);
                    } else {
                        next.Register(name, previousTemplate);
                    }
                }
            }

            return next;
        }

        void Log(string msg) {
            if (log != null) log(msg);
        }
    }
}
