using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    public interface IShorthandExpander {
        string ShorthandName { get; }
        IEnumerable<KeyValuePair<string, string>> Expand(string value);
    }
}
