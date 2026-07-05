using System;
using System.Collections.Generic;
using System.IO;
using Weva.Components;
using Weva.Components.Scoping;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Documents;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.HotReload {
    // Drains the CssReloadQueue once per frame, re-parses the changed
    // stylesheet files, swaps them into the cascade, and marks the entire
    // document subtree dirty so the next Tick re-cascades / re-lays-out /
    // re-paints. Held by WevaDocument and invoked from
    // UIDocumentLifecycle.Tick (or directly by tests).
    //
    // V1 strategy: hot-reload re-builds the entire CascadeEngine and
    // CssAnimationRunner from the freshly-parsed stylesheet list, then
    // replaces state.Cascade and state.Animator. This is heavier than a
    // surgical rule-replacement but the net cost is ~0.5 ms for the menu
    // demo and the implementation stays simple enough to verify
    // exhaustively. Surgical replacement can ship later as a transparent
    // optimization.
    //
    // Failure handling: if CssParser throws on the new content (the user
    // typed a syntax error mid-edit) the coordinator catches the exception,
    // logs the path + message, and leaves the previous cascade in place.
    // The next save replaces the broken sheet with whatever the user types
    // next.
    public sealed class HotReloadCoordinator {
        readonly UIDocumentState state;
        readonly CssReloadQueue queue;
        readonly Action<string> log;
        // Per-path debounce timestamps. FileSystemWatcher can fire 2-3
        // events per atomic save and we don't want to re-parse 3x. The
        // 50ms window collapses these into one reload while staying well
        // inside the <100ms visible-update budget.
        readonly Dictionary<string, double> lastReloadAt = new(StringComparer.OrdinalIgnoreCase);
        const double DebounceSeconds = 0.05;
        int reloadCount;

        public HotReloadCoordinator(UIDocumentState state, CssReloadQueue queue, Action<string> log = null) {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.log = log;
        }

        public int ReloadCount => reloadCount;

        // Drains the queue and re-applies any pending stylesheet changes.
        // Pass nowSeconds=Time.unscaledTimeAsDouble or any monotonically-
        // increasing clock value; the coordinator uses it to debounce
        // duplicate saves. Returns true if at least one stylesheet was
        // re-applied.
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
                MarkAllElementsDirty();
                reloadCount++;
            }
            return anyApplied;
        }

        bool TryReloadOne(string path) {
            int slot = FindStylesheetSlot(path);
            if (slot < 0) {
                Log($"[weva] hot-reload: '{path}' is not registered with this document; ignoring");
                return false;
            }

            string source;
            try {
                source = File.ReadAllText(path);
            } catch (Exception ex) {
                Log($"[weva] hot-reload: read failed for '{path}': {ex.Message}");
                return false;
            }

            Stylesheet parsed;
            try {
                parsed = CssParser.Parse(source, new ParseOptions { ThrowOnError = false });
            } catch (Exception ex) {
                Log($"[weva] hot-reload: parse failed for '{path}': {ex.Message}; keeping previous styles");
                return false;
            }

            // Replace the parsed sheet at its author-slot index. The
            // AuthorStylesheets list contains UA + form sheets at indices
            // 0..1, then author sheets after that; StylesheetPaths is
            // parallel to the *author* portion only. Compute the absolute
            // index inside AuthorStylesheets.
            int absoluteIndex = AuthorBaseIndex() + slot;
            if (absoluteIndex < 0 || absoluteIndex >= state.AuthorStylesheets.Count) {
                Log($"[weva] hot-reload: slot {slot} out of range");
                return false;
            }

            // Replace the OriginatedStylesheet entry. Parallel keyframe
            // list: same author index minus 0 (KeyframeStylesheets only
            // contains author sheets, no UA/form sheets).
            state.AuthorStylesheets[absoluteIndex] = OriginatedStylesheet.Author(parsed);
            if (slot < state.KeyframeStylesheets.Count) {
                state.KeyframeStylesheets[slot] = parsed;
            } else {
                state.KeyframeStylesheets.Add(parsed);
            }

            RebuildCascade();
            Log($"[weva] hot-reload: applied '{path}'");
            return true;
        }

        void RebuildCascade() {
            // Drop the negative parse cache before re-cascading. An author
            // mid-edit may have fixed a previously-malformed declaration
            // (e.g. `color: #ff` -> `color: #ff0000`); without this clear,
            // the next TryParse for `#ff0000` (if it happens to collide
            // with a still-cached failure key) or — more commonly — the
            // exact original bad token if the author iterated through
            // intermediate states, would keep returning the cached null
            // and the fixed value would silently fail to apply. See DD4
            // in CODE_AUDIT_FINDINGS.md.
            CssValue.InvalidateNegativeCache();

            // Build a fresh cascade engine from the (possibly-mutated) list.
            // Re-attach the animation runner so transitions/animations
            // continue to run; KeyframesResolver picks up newly-added
            // @keyframes from the updated raw sheets. We construct a new
            // animator because the runner's resolver is initialized from
            // the stylesheet list at construction time.
            var combined = new List<OriginatedStylesheet>(state.AuthorStylesheets);
            // Component-scoped author sheets must remain at the tail; the
            // builder appended them after author sheets. We re-append from
            // the registry to keep that ordering intact.
            if (state.Components != null) {
                foreach (var os in ComponentStyleIntegration.RewrittenStylesheets(state.Components)) {
                    combined.Add(os);
                }
            }

            var newCascade = new CascadeEngine(combined, state.MediaContext);
            var clock = state.Clock ?? new SystemUIClock();
            var newAnimator = new CssAnimationRunner(newCascade, state.KeyframeStylesheets, clock) {
                InvalidationTracker = state.Invalidation
            };
            newCascade.AttachAnimationRunner(newAnimator);
            // Carry the box lookup over so @container queries still work
            // until the next layout pass refreshes it.
            newCascade.ElementToBoxLookup = state.Cascade?.ElementToBoxLookup;

            // Release the previous animator's Document.Mutated subscription
            // and element-keyed dictionaries before swapping in the new
            // instance — otherwise the old runner would double-subscribe
            // alongside the new one and keep its eight element-keyed
            // dictionaries (and their Element references) live until the
            // next teardown. Mirrors the MS2 fix in WevaDocument.TearDownPipeline.
            state.Animator?.Dispose();
            // Subscribe the new runner to mutations on the live document so
            // mid-animation element removal compacts its dictionaries from
            // this point forward.
            newAnimator.AttachToDocument(state.Doc);

            state.Cascade = newCascade;
            state.Animator = newAnimator;
        }

        void MarkAllElementsDirty() {
            if (state.Invalidation == null || state.Doc == null) return;
            state.Invalidation.MarkSubtreeDirty(state.Doc, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
            // Force the next Update to rebuild layout from scratch — we
            // dropped the cascade snapshot so SnapshotBoxBuilder needs a
            // fresh pass anyway.
            state.RootBox = null;
            // A stylesheet reload can remove layout animations or pseudo
            // content that had already been retained in paint/batch caches.
            // Clear those retained artifacts immediately so the render pass
            // cannot keep drawing the previous frame's batches while the
            // next lifecycle tick rebuilds layout.
            state.Painter?.InvalidateAll();
            state.PaintInvalidated = true;
            state.HasEmittedPaint = false;
        }

        int FindStylesheetSlot(string path) {
            if (state.StylesheetPaths == null) return -1;
            string normalized;
            try { normalized = Path.GetFullPath(path); } catch { normalized = path; }
            for (int i = 0; i < state.StylesheetPaths.Count; i++) {
                var p = state.StylesheetPaths[i];
                if (string.IsNullOrEmpty(p)) continue;
                string pn;
                try { pn = Path.GetFullPath(p); } catch { pn = p; }
                if (string.Equals(pn, normalized, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        // The author-sheet portion of state.AuthorStylesheets starts after
        // the UA + form-control sheets. UIDocumentBuilder always inserts
        // exactly 2 such sheets at the top.
        const int AuthorBase = 2;
        static int AuthorBaseIndex() => AuthorBase;

        void Log(string msg) {
            if (log != null) log(msg);
        }
    }
}
