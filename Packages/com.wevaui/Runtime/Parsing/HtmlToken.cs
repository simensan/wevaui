using System.Collections.Generic;

namespace Weva.Parsing {
    public enum HtmlTokenKind {
        StartTag,
        EndTag,
        Text,
        Comment,
        DocType,
        Eof
    }

    public struct HtmlAttribute {
        public string Name;
        public string Value;

        public HtmlAttribute(string name, string value) {
            Name = name;
            Value = value;
        }
    }

    public struct HtmlToken {
        public HtmlTokenKind Kind;
        public string Name;
        public string Text;
        public List<HtmlAttribute> Attributes;
        public bool SelfClosing;
        public int Line;
        public int Column;
    }
}
