using System.Collections.Generic;

namespace Weva.Css {
    public sealed class ContainerRule : Rule {
        public string Name;
        public string ConditionText;
        public List<Rule> Rules = new();
    }
}
