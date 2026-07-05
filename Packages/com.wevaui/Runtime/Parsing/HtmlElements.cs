using System.Collections.Generic;

namespace Weva.Parsing {
    public static class HtmlElements {
        static readonly HashSet<string> voidElements = new() {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

        // Block-level start tags that implicitly close an open <p> per the
        // HTML Living Standard "Optional tags" section.
        static readonly HashSet<string> closesOpenP = new() {
            "p", "div", "section", "article", "header", "footer", "nav",
            "aside", "main", "ul", "ol", "table", "form", "h1", "h2", "h3",
            "h4", "h5", "h6", "pre", "address", "blockquote", "hr"
        };

        // Elements whose end tag is optional per the HTML Living Standard
        // "Optional tags" section. Browsers auto-insert the implicit close
        // when an enclosing element closes, so the parser must not reject a
        // mismatched-end-tag sequence that's actually well-formed under those
        // rules (e.g. `<section><li>x</section>` is valid HTML).
        static readonly HashSet<string> optionalCloseElements = new() {
            "li", "p", "dd", "dt", "td", "th", "tr", "tbody", "thead",
            "tfoot", "option", "optgroup", "colgroup", "caption", "rt", "rp",
            "html", "head", "body"
        };

        public static bool IsVoid(string tagName) => voidElements.Contains(tagName);

        // True when the tag's end tag is optional per the HTML spec. Used by
        // HtmlParser to silently absorb mismatched end tags rather than throw,
        // matching real-browser tolerance for AI-authored / hand-written HTML.
        public static bool IsOptionalClose(string tagName) {
            return !string.IsNullOrEmpty(tagName) && optionalCloseElements.Contains(tagName);
        }

        // HTML Living Standard "Optional tags": certain start tags implicitly
        // close the currently-open element. v1 covers the common cases:
        //   <p> closes on any block-level start tag
        //   <li> closes on another <li>
        //   <dt>/<dd> close on each other or another <dt>/<dd>
        //   <tr> closes on another <tr>
        //   <td>/<th> close on each other or <tr>
        //   <option> closes on another <option>
        public static bool ShouldImplicitlyClose(string currentOpenTag, string newStartTag) {
            if (string.IsNullOrEmpty(currentOpenTag) || string.IsNullOrEmpty(newStartTag)) return false;
            switch (currentOpenTag) {
                case "p":
                    return closesOpenP.Contains(newStartTag);
                case "li":
                    return newStartTag == "li";
                case "dt":
                case "dd":
                    return newStartTag == "dt" || newStartTag == "dd";
                case "tr":
                    return newStartTag == "tr";
                case "td":
                case "th":
                    return newStartTag == "td" || newStartTag == "th" || newStartTag == "tr";
                case "option":
                    return newStartTag == "option";
                default:
                    return false;
            }
        }
    }
}
