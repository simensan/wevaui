using System.Collections.Generic;

namespace Weva.Css {
    public sealed class KeyframesRule : Rule {
        public string Name;
        public List<KeyframeBlock> Blocks = new();
    }
}
