using System;
using System.Collections.Generic;
using Weva.Dom;

namespace Weva.HotReload {
    // Conservative tree diff for HTML hot-reload.
    //
    // Invariants:
    //   * Pure data — no I/O, no parsing. Caller hands us two parallel DOM
    //     trees (the "live" one whose Element identities must survive, and
    //     the "fresh" one parsed from the new on-disk source) and we mutate
    //     the live tree in place to match the fresh one.
    //   * Identity preservation: when two children at the same position
    //     match by (TagName, key) where key is `id` or `data-key`, the LIVE
    //     element is kept, its attributes diff-applied, and the fresh
    //     element discarded. Form state (input value, scroll position)
    //     therefore survives because it lives on the same Element instance.
    //   * Positional fallback: when no key matches at a position, children
    //     are matched pairwise by index and recursed into.
    //   * Children that exist in fresh but not live are appended in fresh
    //     order. Children present in live but not fresh are removed.
    //   * Text nodes diff by string equality on their Data property; a
    //     change replaces only the Data field so any consumer holding a
    //     reference to the live TextNode keeps that reference.
    //
    // What this differ deliberately does NOT do:
    //   * Cross-position move detection. If a child moves from index 2 to
    //     index 5, we treat it as a remove + insert (Element identity is
    //     not preserved for the moved subtree). Authors keying with `id`
    //     or `data-key` get identity preservation regardless of position
    //     since key-matched children are extracted and re-inserted.
    //   * Whitespace normalization. The HTML parser is the source of truth
    //     for what counts as a text node.
    //   * Type changes. A live <p> being replaced by a <div> at the same
    //     position is a remove + insert; the live <p> is detached.
    public static class DomDiffer {
        // Apply the difference between the live document's root subtree and
        // the freshly-parsed document's root subtree, mutating the live one
        // in place. Returns true if any mutation was performed.
        //
        // Both arguments are full Documents — top-level children of `live`
        // are reconciled against top-level children of `fresh`.
        public static bool ApplyDocumentDiff(Document live, Document fresh) {
            if (live == null || fresh == null) return false;
            return DiffChildren(live, fresh);
        }

        static bool DiffChildren(Node liveParent, Node freshParent) {
            bool mutated = false;
            var liveChildren = new List<Node>(liveParent.Children);
            var freshChildren = new List<Node>(freshParent.Children);

            // Phase 1: pull keyed live elements out of position so they can be
            // matched anywhere in the fresh order. The reordering pass below
            // re-inserts them in the order fresh dictates.
            var keyedLive = new Dictionary<string, Element>(StringComparer.Ordinal);
            for (int i = 0; i < liveChildren.Count; i++) {
                if (liveChildren[i] is Element le) {
                    string k = KeyOf(le);
                    if (k != null) keyedLive[k] = le;
                }
            }

            // Phase 2: walk fresh in order; for each fresh child decide whether
            // to (a) reuse a keyed live match, (b) reuse the positional live
            // match, or (c) create a new node.
            var newOrder = new List<Node>(freshChildren.Count);
            for (int i = 0; i < freshChildren.Count; i++) {
                var fc = freshChildren[i];
                Node target = null;

                if (fc is Element fe) {
                    string fk = KeyOf(fe);
                    if (fk != null && keyedLive.TryGetValue(fk, out var keyMatch)
                        && string.Equals(keyMatch.TagName, fe.TagName, StringComparison.Ordinal)) {
                        target = keyMatch;
                        keyedLive.Remove(fk);
                    }
                }

                if (target == null && i < liveChildren.Count) {
                    var lc = liveChildren[i];
                    if (TypeAndKeyAlign(lc, fc)) {
                        target = lc;
                    }
                }

                if (target == null) {
                    newOrder.Add(fc);
                    mutated = true;
                    continue;
                }

                // Reusable target — diff into it.
                if (target is Element te && fc is Element fe2) {
                    if (DiffElement(te, fe2)) mutated = true;
                } else if (target is TextNode tt && fc is TextNode ft) {
                    if (tt.Data != ft.Data) {
                        tt.Data = ft.Data;
                        mutated = true;
                    }
                }
                newOrder.Add(target);
            }

            // Phase 3: detach any live child not chosen for reuse.
            var newSet = new HashSet<Node>(newOrder);
            for (int i = 0; i < liveChildren.Count; i++) {
                if (!newSet.Contains(liveChildren[i])) {
                    liveParent.RemoveChild(liveChildren[i]);
                    mutated = true;
                }
            }

            // Phase 4: rebuild order. We compare the current child sequence on
            // liveParent against newOrder, and only AppendChild fresh nodes
            // that aren't already attached. For now we accept "no move
            // detection" — if a kept child needs to slide we detach + re-append.
            for (int i = 0; i < newOrder.Count; i++) {
                var want = newOrder[i];
                Node cur = i < liveParent.Children.Count ? liveParent.Children[i] : null;
                if (ReferenceEquals(cur, want)) continue;
                if (want.Parent == liveParent) {
                    liveParent.RemoveChild(want);
                }
                liveParent.AppendChild(want);
                mutated = true;
            }

            return mutated;
        }

        static bool TypeAndKeyAlign(Node a, Node b) {
            if (a is Element ae && b is Element be) {
                if (!string.Equals(ae.TagName, be.TagName, StringComparison.Ordinal)) return false;
                string ka = KeyOf(ae);
                string kb = KeyOf(be);
                if (ka == null && kb == null) return true;
                return string.Equals(ka, kb, StringComparison.Ordinal);
            }
            if (a is TextNode && b is TextNode) return true;
            return false;
        }

        static string KeyOf(Element e) {
            string id = e.GetAttribute("id");
            if (!string.IsNullOrEmpty(id)) return "#" + id;
            string dk = e.GetAttribute("data-key");
            if (!string.IsNullOrEmpty(dk)) return "k:" + dk;
            return null;
        }

        static bool DiffElement(Element live, Element fresh) {
            bool mutated = false;
            // Attributes: replace or add fresh-side, then drop live-side
            // attributes that aren't on fresh.
            var freshAttrs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in fresh.Attributes) {
                freshAttrs.Add(kv.Key);
                var existing = live.GetAttribute(kv.Key);
                if (existing != kv.Value) {
                    live.SetAttribute(kv.Key, kv.Value);
                    mutated = true;
                }
            }
            var liveAttrNames = new List<string>();
            foreach (var kv in live.Attributes) liveAttrNames.Add(kv.Key);
            for (int i = 0; i < liveAttrNames.Count; i++) {
                if (!freshAttrs.Contains(liveAttrNames[i])) {
                    live.RemoveAttribute(liveAttrNames[i]);
                    mutated = true;
                }
            }

            if (DiffChildren(live, fresh)) mutated = true;
            return mutated;
        }
    }
}
