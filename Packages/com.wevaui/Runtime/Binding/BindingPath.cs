using System;

namespace Weva.Binding {
    public readonly struct BindingPath : IEquatable<BindingPath> {
        readonly string[] segments;

        public string[] Segments => segments ?? System.Array.Empty<string>();
        public int Count => segments == null ? 0 : segments.Length;
        public bool IsEmpty => segments == null || segments.Length == 0;

        BindingPath(string[] segs) {
            segments = segs;
        }

        public static BindingPath Parse(string raw) {
            if (raw == null) {
                throw new BindingException("Binding path is null.");
            }
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) {
                throw new BindingException("Binding path is empty.");
            }

            var parts = trimmed.Split('.');
            var result = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++) {
                var seg = parts[i].Trim();
                if (seg.Length == 0) {
                    throw new BindingException($"Invalid binding path '{raw}': empty segment.");
                }
                if (!IsValidIdentifier(seg)) {
                    throw new BindingException($"Invalid binding path '{raw}': segment '{seg}' is not a valid identifier.");
                }
                result[i] = seg;
            }
            return new BindingPath(result);
        }

        static bool IsValidIdentifier(string s) {
            if (s.Length == 0) return false;
            if (s == "$index") return true;
            char first = s[0];
            if (!(char.IsLetter(first) || first == '_')) return false;
            for (int i = 1; i < s.Length; i++) {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }

        public override string ToString() {
            if (segments == null || segments.Length == 0) return string.Empty;
            return string.Join(".", segments);
        }

        public bool Equals(BindingPath other) {
            var a = segments;
            var b = other.segments;
            if (a == null && b == null) return true;
            if (a == null || b == null) {
                int la = a == null ? 0 : a.Length;
                int lb = b == null ? 0 : b.Length;
                return la == lb;
            }
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is BindingPath bp && Equals(bp);

        public override int GetHashCode() {
            if (segments == null) return 0;
            unchecked {
                int hash = 17;
                for (int i = 0; i < segments.Length; i++) {
                    hash = hash * 31 + (segments[i] != null ? segments[i].GetHashCode() : 0);
                }
                return hash;
            }
        }

        public static bool operator ==(BindingPath a, BindingPath b) => a.Equals(b);
        public static bool operator !=(BindingPath a, BindingPath b) => !a.Equals(b);
    }
}
