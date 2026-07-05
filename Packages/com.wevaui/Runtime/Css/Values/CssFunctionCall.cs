using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Values {
    public sealed class CssFunctionCall : CssValue {
        public string Name { get; }
        public IReadOnlyList<CssValue> Arguments { get; }

        public override CssValueKind Kind => CssValueKind.FunctionCall;

        public CssFunctionCall(string name, IReadOnlyList<CssValue> arguments) {
            // ToLowerInvariantOrSame returns the same instance when the input
            // is already lowercase — the common case for parser-emitted
            // function names like `linear-gradient`, `rgba`, `translate`,
            // saving a per-CssFunctionCall string allocation.
            Name = name == null ? "" : CssStringUtil.ToLowerInvariantOrSame(name);
            Arguments = arguments;
            Raw = BuildRaw(Name, arguments);
        }

        public CssFunctionCall(string name, IReadOnlyList<CssValue> arguments, string raw) {
            Name = name == null ? "" : CssStringUtil.ToLowerInvariantOrSame(name);
            Arguments = arguments;
            Raw = raw;
        }

        // Lazy raw-string materialisation. Animation typed overlays construct
        // CssFunctionCall with raw=null and mutate inner args in place every
        // Tick, so the Raw built at construction would go stale. Falling back
        // to BuildRaw on demand keeps consumers that ultimately need a string
        // (DevTools serialisation, ComputedStyle.Get lazy materialisation)
        // honest. The hot path goes through SetParsed and never hits this.
        public override string ToString() {
            return Raw ?? BuildRaw(Name, Arguments);
        }

        static string BuildRaw(string name, IReadOnlyList<CssValue> args) {
            var sb = new StringBuilder();
            sb.Append(name);
            sb.Append('(');
            for (int i = 0; i < args.Count; i++) {
                if (i > 0) sb.Append(", ");
                sb.Append(args[i].Raw ?? args[i].ToString());
            }
            sb.Append(')');
            return sb.ToString();
        }
    }
}
