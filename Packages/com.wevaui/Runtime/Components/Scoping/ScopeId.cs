using System;
using System.Text;

namespace Weva.Components.Scoping {
    public readonly struct ScopeId : IEquatable<ScopeId> {
        public string Value { get; }

        ScopeId(string value) {
            Value = value;
        }

        public bool IsEmpty => string.IsNullOrEmpty(Value);

        public static ScopeId None => default;

        public static ScopeId From(string value) {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("ScopeId value required", nameof(value));
            return new ScopeId(value);
        }

        // Stable, deterministic id derived from the component name. Same name yields
        // the same id every call so test runs and recompiles don't shuffle stylesheet
        // identity. Hash is FNV-1a 32-bit over UTF-8 bytes; collisions across the
        // small set of components in any single registry are vanishingly unlikely.
        public static ScopeId Generate(string componentName) {
            if (string.IsNullOrEmpty(componentName)) throw new ArgumentException("componentName required", nameof(componentName));
            string sanitized = Sanitize(componentName);
            uint hash = Fnv1a32(componentName);
            return new ScopeId("uui-sc-" + sanitized + "-" + hash.ToString("x8"));
        }

        static string Sanitize(string name) {
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++) {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                sb.Append(ok ? char.ToLowerInvariant(c) : '-');
            }
            return sb.ToString();
        }

        static uint Fnv1a32(string s) {
            const uint prime = 16777619u;
            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                hash ^= (byte)(c & 0xFF);
                hash *= prime;
                hash ^= (byte)((c >> 8) & 0xFF);
                hash *= prime;
            }
            return hash;
        }

        public bool Equals(ScopeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ScopeId s && Equals(s);
        public override int GetHashCode() => Value == null ? 0 : Value.GetHashCode();
        public override string ToString() => Value ?? "";

        public static bool operator ==(ScopeId a, ScopeId b) => a.Equals(b);
        public static bool operator !=(ScopeId a, ScopeId b) => !a.Equals(b);
    }
}
