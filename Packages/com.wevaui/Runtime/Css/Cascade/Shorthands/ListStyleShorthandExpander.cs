using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Lists 3 §3.4: `list-style` is a shorthand for `list-style-type`,
    // `list-style-position`, and `list-style-image`. Tokens may appear in
    // any order; `none` is special — it applies to BOTH list-style-type
    // AND list-style-image (so `list-style: none` suppresses the marker
    // glyph regardless of which longhand the author would have set
    // otherwise, per the spec's "if none is given exactly once, …" rule).
    //
    // Classification per token:
    //   - "inside" / "outside"        → list-style-position
    //   - url(...) / linear-gradient → list-style-image
    //   - "none"                      → applies to both type and image
    //   - anything else (identifier)  → list-style-type
    public sealed class ListStyleShorthandExpander : IShorthandExpander {
        public string ShorthandName => "list-style";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;

            string type = null;
            string position = null;
            string image = null;
            int noneCount = 0;
            foreach (var raw in tokens) {
                var t = CssStringUtil.ToLowerInvariantOrSame(raw);
                if (t == "none") {
                    // Per CSS Lists 3 §3.4: `none` may apply to either type
                    // or image. If it appears once with another type value,
                    // it sets image to none; if both `none` and an image
                    // URL are given, the later wins for image. We treat any
                    // `none` as forcing both unless explicitly overridden.
                    noneCount++;
                    continue;
                }
                if (t == "inside" || t == "outside") { position = t; continue; }
                if (ShorthandTokens.IsImageValue(raw)) { image = raw; continue; }
                if (type == null) type = raw;
            }

            // Emit `list-style-type`: explicit type wins, else if none was
            // seen we set type to none, else leave alone (which lets the
            // cascade keep whatever the previous declaration / inherited /
            // initial value resolved to). To avoid leaking earlier author
            // declarations through the shorthand, we MUST emit a value
            // when no type token but a position/image is present — emit
            // the initial "disc" so the shorthand resets the longhand.
            if (type != null) {
                yield return new KeyValuePair<string, string>("list-style-type", type);
            } else if (noneCount > 0) {
                yield return new KeyValuePair<string, string>("list-style-type", "none");
            } else {
                yield return new KeyValuePair<string, string>("list-style-type", "disc");
            }

            // Position defaults to `outside` per CSS Lists 3 §3.1.
            yield return new KeyValuePair<string, string>("list-style-position",
                position ?? "outside");

            // Image defaults to `none`. Explicit image wins over a bare
            // `none` token, since the author plainly typed the URL.
            if (image != null) {
                yield return new KeyValuePair<string, string>("list-style-image", image);
            } else {
                yield return new KeyValuePair<string, string>("list-style-image", "none");
            }
        }
    }
}
