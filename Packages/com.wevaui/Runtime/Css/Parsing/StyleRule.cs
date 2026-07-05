using System.Collections.Generic;

namespace Weva.Css {
    public sealed class StyleRule : Rule {
        public List<string> Selectors = new();
        public List<Declaration> Declarations = new();
        // CSS Nesting Module — when parser encounters a rule inside another rule
        // body, the nested rule lands here. NestingExpander.Expand flattens the
        // tree into top-level StyleRules at parse-completion.
        public List<Rule> NestedRules = new();
    }
}
