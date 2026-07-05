using System.Collections.Generic;

namespace Weva.Css {
    public sealed class MediaRule : Rule {
        public string ConditionText;
        public List<Rule> Rules = new();
    }
}
