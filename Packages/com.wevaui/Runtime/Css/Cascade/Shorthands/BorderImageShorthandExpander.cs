using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Backgrounds 3 §6.6 — `border-image` shorthand. Grammar:
    //
    //   <'border-image-source'>
    //   || <'border-image-slice'> [ / <'border-image-width'>
    //                              | / <'border-image-width'>? / <'border-image-outset'> ]?
    //   || <'border-image-repeat'>
    //
    // The three top-level slots (source, slice/width/outset triple,
    // repeat) may appear in any order. Slice / width / outset within
    // their triple are separated by `/`. Any slot the author omits
    // resets to its initial value:
    //   source  → none
    //   slice   → 100%
    //   width   → 1 (= 1x border-width)
    //   outset  → 0
    //   repeat  → stretch
    public sealed class BorderImageShorthandExpander : IShorthandExpander {
        public string ShorthandName => "border-image";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value ?? "");
            return ExpandTokens(tokens);
        }

        // Two-pass design:
        //   1. Lift out the unambiguous slots (source, repeat keywords)
        //      that aren't separated by `/` from the slice triple.
        //   2. The remaining tokens form the slice/width/outset triple,
        //      split on `/` (max two slashes per grammar).
        // The slot-or-triple distinction is what makes a single-pass
        // state machine awkward — a leading repeat keyword and a
        // trailing repeat keyword are both legal, with the numeric
        // triple in between.
        static IEnumerable<KeyValuePair<string, string>> ExpandTokens(List<string> tokens) {
            string source = "none";
            string repeat = "stretch";
            bool hasSource = false, hasRepeat = false;
            var tripleTokens = new List<string>();

            if (tokens.Count == 0) yield break;
            foreach (var t in tokens) {
                if (t == "/") {
                    tripleTokens.Add(t);
                    continue;
                }
                if (!hasSource && IsSourceToken(t)) {
                    source = t;
                    hasSource = true;
                    continue;
                }
                if (IsRepeatKeyword(t)) {
                    if (!hasRepeat) {
                        repeat = t;
                        hasRepeat = true;
                    } else {
                        // Two-keyword form (horizontal + vertical).
                        repeat = repeat + " " + t;
                    }
                    continue;
                }
                tripleTokens.Add(t);
            }

            // Split the triple on `/`. Per grammar:
            //   group[0] = slice   (always)
            //   group[1] = width   (optional)
            //   group[2] = outset  (optional)
            var groups = SplitOnSlash(tripleTokens);

            string sliceValue = "100%";
            string widthValue = "1";
            string outsetValue = "0";
            if (groups.Count > 0 && groups[0].Count > 0) sliceValue = ShorthandTokenizer.Join(groups[0]);
            if (groups.Count > 1 && groups[1].Count > 0) widthValue = ShorthandTokenizer.Join(groups[1]);
            if (groups.Count > 2 && groups[2].Count > 0) outsetValue = ShorthandTokenizer.Join(groups[2]);

            yield return new KeyValuePair<string, string>("border-image-source", source);
            yield return new KeyValuePair<string, string>("border-image-slice", sliceValue);
            yield return new KeyValuePair<string, string>("border-image-width", widthValue);
            yield return new KeyValuePair<string, string>("border-image-outset", outsetValue);
            yield return new KeyValuePair<string, string>("border-image-repeat", repeat);
        }

        // Splits the triple tokens on slash. Always returns at least one
        // (possibly empty) group; preserves the slash-only-separator
        // invariant by ignoring trailing/leading empty groups for the
        // higher-arity slots (an explicit empty `/ /` is treated as
        // `width = initial, outset = initial`).
        static List<List<string>> SplitOnSlash(List<string> tokens) {
            var groups = new List<List<string>>();
            var current = new List<string>();
            foreach (var t in tokens) {
                if (t == "/") {
                    groups.Add(current);
                    current = new List<string>();
                    continue;
                }
                current.Add(t);
            }
            groups.Add(current);
            return groups;
        }

        // Source slot tokens: url(...), none, or a CSS gradient function.
        // Reuses the existing image-classifier so future image keywords
        // (image-set, etc) flow through without changes here.
        static bool IsSourceToken(string t) {
            if (string.Equals(t, "none", System.StringComparison.OrdinalIgnoreCase)) return true;
            return ShorthandTokens.IsImageValue(t);
        }

        // Repeat slot keywords. Distinct from background-repeat: the
        // border-image-repeat grammar excludes `no-repeat`/`repeat-x`/
        // `repeat-y` but adds `stretch`.
        static bool IsRepeatKeyword(string t) {
            switch (CssStringUtil.ToLowerInvariantOrSame(t)) {
                case "stretch":
                case "repeat":
                case "round":
                case "space":
                    return true;
                default:
                    return false;
            }
        }
    }
}
