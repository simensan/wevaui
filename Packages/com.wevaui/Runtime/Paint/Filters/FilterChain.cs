using System;
using System.Collections.Generic;
using System.Text;

namespace Weva.Paint.Filters {
    public sealed class FilterChain {
        static readonly FilterFunction[] EmptyArray = Array.Empty<FilterFunction>();

        public IReadOnlyList<FilterFunction> Functions { get; }

        public bool IsEmpty => Functions.Count == 0;

        public static FilterChain Empty { get; } = new FilterChain(EmptyArray);

        public FilterChain(IReadOnlyList<FilterFunction> functions) {
            Functions = functions ?? EmptyArray;
        }

        public FilterChain(IEnumerable<FilterFunction> functions) {
            if (functions == null) {
                Functions = EmptyArray;
                return;
            }
            var list = new List<FilterFunction>();
            foreach (var f in functions) {
                if (f != null) list.Add(f);
            }
            Functions = list;
        }

        public string ToText() {
            if (Functions.Count == 0) return "none";
            var sb = new StringBuilder();
            for (int i = 0; i < Functions.Count; i++) {
                if (i > 0) sb.Append(' ');
                sb.Append(Functions[i].ToText());
            }
            return sb.ToString();
        }

        public override string ToString() => ToText();

        public override bool Equals(object obj) {
            if (!(obj is FilterChain other)) return false;
            if (other.Functions.Count != Functions.Count) return false;
            for (int i = 0; i < Functions.Count; i++) {
                if (!Equals(Functions[i], other.Functions[i])) return false;
            }
            return true;
        }

        public override int GetHashCode() {
            unchecked {
                int h = 17;
                for (int i = 0; i < Functions.Count; i++) {
                    h = (h * 397) ^ (Functions[i]?.GetHashCode() ?? 0);
                }
                return h;
            }
        }
    }
}
