using System;
using System.Globalization;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Binding {
    public sealed class ClassBinding {
        public Element Target { get; }
        public string ClassName { get; }
        public BindingPath Path { get; }

        bool lastApplied;
        bool hasApplied;

        public ClassBinding(Element target, string className, BindingPath path) {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(className)) throw new ArgumentException("Class name is required.", nameof(className));
            Target = target;
            ClassName = className.Trim();
            Path = path;
        }

        public bool Update(object context) {
            return Update(context, null);
        }

        public bool Update(object context, InvalidationTracker tracker) {
            bool enabled = false;
            if (BindingResolver.TryResolve(context, Path, out var value)) {
                enabled = IsTruthy(value);
            }
            if (hasApplied && enabled == lastApplied && HasClass(Target, ClassName) == enabled) return false;
            bool changed = SetClass(Target, ClassName, enabled);
            lastApplied = enabled;
            hasApplied = true;
            if (changed && tracker != null) {
                // Do NOT mark Structure here. A class flip that changes
                // `display: none ↔ shown` is handled by CascadeEngine:
                // ComputeOrHit detects the display transition on its next
                // pass and calls NoteStructureDirtyFromCascade, which
                // ApplyLayoutInvalidation drains as Structure onto the
                // tracker BEFORE Layout() is called. Marking Structure
                // preemptively from ClassBinding caused TryLayoutSubtree
                // to bail unconditionally (its first check is `if
                // (tracker.HasAny(Structure)) return false`), preventing
                // the incremental subtree-skip path from ever firing —
                // even for paint-only class changes like border-color or
                // box-shadow toggles (REACT-1).
                //
                // Layout-affecting property changes (padding, flex-grow,
                // width, etc.) that cross a class boundary are detected by
                // CascadeEngine.LayoutAffectingPropertyChanged and land as
                // Layout (not Structure) on the tracker via ApplyLayout-
                // Invalidation. TryLayoutSubtree handles the geometry-
                // change case: SameOuterGeometry returns false, which
                // falls back to the full-layout path and re-runs flex/grid
                // distribution. The previously documented "challenges panel
                // halved its height" case is covered by this fallback.
                tracker.MarkDirty(Target,
                    InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
            }
            return changed;
        }

        static bool IsTruthy(object value) {
            if (value == null) return false;
            if (value is bool b) return b;
            if (value is string s) {
                if (string.IsNullOrEmpty(s)) return false;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            if (value is IConvertible c) {
                try { return Math.Abs(c.ToDouble(CultureInfo.InvariantCulture)) > double.Epsilon; }
                catch (FormatException) { return true; }
                catch (InvalidCastException) { return true; }
            }
            return true;
        }

        static bool HasClass(Element el, string className) {
            // Direct iteration of the raw class attribute string avoids
            // Element.ClassList's yield-return iterator allocation
            // (~40-50B per call). ClassBinding.Apply runs on every click
            // through every bound element, so on a typical interactive
            // UI this is the difference between alloc-free and ~per-
            // click GC pressure.
            return ContainsToken(el.GetAttribute("class"), className);
        }

        static bool ContainsToken(string classAttr, string token) {
            if (string.IsNullOrEmpty(classAttr) || string.IsNullOrEmpty(token)) return false;
            int len = classAttr.Length;
            int tokLen = token.Length;
            int i = 0;
            while (i < len) {
                while (i < len && IsWhitespace(classAttr[i])) i++;
                int start = i;
                while (i < len && !IsWhitespace(classAttr[i])) i++;
                int n = i - start;
                if (n == tokLen) {
                    bool eq = true;
                    for (int j = 0; j < tokLen; j++) {
                        if (classAttr[start + j] != token[j]) { eq = false; break; }
                    }
                    if (eq) return true;
                }
            }
            return false;
        }

        static bool IsWhitespace(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        static bool SetClass(Element el, string className, bool enabled) {
            var current = el.GetAttribute("class") ?? string.Empty;
            // ContainsToken does the membership check without allocating
            // the parts[] array that string.Split would, avoiding ~80-200B
            // per Apply call.
            bool found = ContainsToken(current, className);
            if (enabled && found) return false;
            if (!enabled && !found) return false;

            if (enabled) {
                el.SetAttribute("class", string.IsNullOrEmpty(current) ? className : current + " " + className);
                return true;
            }

            // Removing a class: walk the raw attribute and emit non-
            // matching tokens to a StringBuilder. Keeps the alloc to the
            // single new-class-string output + the StringBuilder's
            // internal chunks (which the framework pools per-thread).
            var next = new System.Text.StringBuilder(current.Length);
            int len = current.Length;
            int i = 0;
            while (i < len) {
                while (i < len && IsWhitespace(current[i])) i++;
                int start = i;
                while (i < len && !IsWhitespace(current[i])) i++;
                int n = i - start;
                if (n == 0) continue;
                if (n == className.Length) {
                    bool eq = true;
                    for (int j = 0; j < n; j++) {
                        if (current[start + j] != className[j]) { eq = false; break; }
                    }
                    if (eq) continue;
                }
                if (next.Length > 0) next.Append(' ');
                next.Append(current, start, n);
            }
            el.SetAttribute("class", next.ToString());
            return true;
        }
    }
}
