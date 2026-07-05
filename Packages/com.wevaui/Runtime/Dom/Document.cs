using System;
using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Dom {
    public sealed class Document : Node {
        public Document() {
            OwnerDocument = this;
        }

        public Element GetElementById(string id) {
            return FindFirstElement(this, e => e.GetAttribute("id") == id);
        }

        public IEnumerable<Element> GetElementsByTagName(string tagName) {
            var lowered = CssStringUtil.ToLowerInvariantOrSame(tagName);
            return FindElements(this, e => e.TagName == lowered);
        }

        public IEnumerable<Element> GetElementsByClassName(string className) {
            return FindElements(this, e => HasClassToken(e.ClassName, className));
        }

        // Whitespace-separated class-token membership check that doesn't
        // allocate Element.ClassList's yield iterator on every test.
        // Reused for the public query API above; the cascade has its own
        // copy keyed for hashing (HashClassTokens in CascadeEngine).
        static bool HasClassToken(string classAttr, string token) {
            if (string.IsNullOrEmpty(classAttr) || string.IsNullOrEmpty(token)) return false;
            int len = classAttr.Length;
            int tokLen = token.Length;
            int i = 0;
            while (i < len) {
                while (i < len && IsWs(classAttr[i])) i++;
                int start = i;
                while (i < len && !IsWs(classAttr[i])) i++;
                int n = i - start;
                if (n != tokLen) continue;
                bool eq = true;
                for (int j = 0; j < n; j++) {
                    if (classAttr[start + j] != token[j]) { eq = false; break; }
                }
                if (eq) return true;
            }
            return false;
        }

        static bool IsWs(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        static Element FindFirstElement(Node node, Func<Element, bool> pred) {
            foreach (var c in node.Children) {
                if (c is Element e && pred(e)) return e;
                var found = FindFirstElement(c, pred);
                if (found != null) return found;
            }
            return null;
        }

        static IEnumerable<Element> FindElements(Node node, Func<Element, bool> pred) {
            foreach (var c in node.Children) {
                if (c is Element e && pred(e)) yield return e;
                foreach (var sub in FindElements(c, pred)) yield return sub;
            }
        }
    }
}
