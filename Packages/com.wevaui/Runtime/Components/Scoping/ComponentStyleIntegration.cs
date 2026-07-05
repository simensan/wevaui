using System.Collections.Generic;
using Weva.Css.Cascade;

namespace Weva.Components.Scoping {
    public static class ComponentStyleIntegration {
        public static IEnumerable<OriginatedStylesheet> RewrittenStylesheets(ComponentRegistry registry) {
            if (registry == null) yield break;
            foreach (var kv in registry.AllStylesheets) {
                if (kv.Value == null || kv.Value.Rewritten == null) continue;
                yield return OriginatedStylesheet.Author(kv.Value.Rewritten);
            }
        }
    }
}
