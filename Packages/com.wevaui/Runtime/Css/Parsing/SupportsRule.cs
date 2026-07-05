using System.Collections.Generic;

namespace Weva.Css {
    // CSS Conditional 3 §3 — `@supports` rule. The parser retains the
    // condition verbatim; Cascade.SupportsEvaluator decides whether the
    // nested rules enter the cascade based on Weva's actual support.
    public sealed class SupportsRule : Rule {
        public string ConditionText;
        public List<Rule> Rules = new();
    }
}
