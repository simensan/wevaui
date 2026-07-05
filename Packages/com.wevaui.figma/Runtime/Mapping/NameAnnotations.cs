using System.Collections.Generic;

namespace Weva.Figma.Mapping
{
    public sealed class EachDirective
    {
        public string Collection;
        public string Item = "item";
        public string Key;
    }

    /// <summary>
    /// Directives a designer encodes in a Figma layer name to bridge the static
    /// design to Weva's dynamic markup. A frozen frame can't express loops,
    /// bindings, or interaction, so the author annotates intent in the name:
    ///
    /// <list type="bullet">
    ///   <item><c>{{ Expr }}</c> — bound text (on a TEXT node) e.g. <c>Player {{ PlayerName }}</c></item>
    ///   <item><c>#my-id</c> — element id</item>
    ///   <item><c>&lt;button&gt;</c> — semantic tag override</item>
    ///   <item><c>@click=OnPlay</c> — event hook → <c>on-click="OnPlay"</c></item>
    ///   <item><c>.selected?IsSelected</c> — conditional class → <c>data-class-selected="IsSelected"</c></item>
    ///   <item><c>*each=Stages:stage:Number</c> — repeat → wrap the first child in
    ///         <c>&lt;template data-each="Stages as stage" data-key="Number"&gt;</c></item>
    /// </list>
    ///
    /// Everything not recognized as a directive stays in <see cref="CleanName"/>,
    /// which drives the generated class name. Tokens that start with a sigil but
    /// don't match the full grammar are treated as plain name text, to limit
    /// false positives.
    /// </summary>
    public sealed class NameAnnotations
    {
        public string CleanName = "";
        public string Binding;   // "{{ Expr }}" including braces, or null
        public string Id;
        public string Tag;
        public readonly List<KeyValuePair<string, string>> Events = new List<KeyValuePair<string, string>>();
        public readonly List<KeyValuePair<string, string>> ClassToggles = new List<KeyValuePair<string, string>>();
        public EachDirective Each;

        /// <summary>Raw extra attributes injected by a round-trip overlay (not parsed from a layer name).</summary>
        public readonly List<KeyValuePair<string, string>> ExtraAttributes = new List<KeyValuePair<string, string>>();

        public bool HasDirectives =>
            Binding != null || Id != null || Tag != null || Each != null
            || Events.Count > 0 || ClassToggles.Count > 0;

        static readonly char[] Whitespace = { ' ', '\t', '\n', '\r' };

        public static NameAnnotations Parse(string name)
        {
            var a = new NameAnnotations();
            string work = name ?? "";

            // Pull out the first {{ ... }} (may contain spaces) before tokenizing.
            int open = work.IndexOf("{{", System.StringComparison.Ordinal);
            if (open >= 0)
            {
                int close = work.IndexOf("}}", open + 2, System.StringComparison.Ordinal);
                if (close >= 0)
                {
                    a.Binding = work.Substring(open, close + 2 - open).Trim();
                    work = work.Remove(open, close + 2 - open);
                }
            }

            var cleanTokens = new List<string>();
            foreach (string token in work.Split(Whitespace, System.StringSplitOptions.RemoveEmptyEntries))
                if (!TryDirective(token, a))
                    cleanTokens.Add(token);

            a.CleanName = string.Join(" ", cleanTokens).Trim();
            return a;
        }

        static bool TryDirective(string token, NameAnnotations a)
        {
            if (token.Length < 2) return false;
            switch (token[0])
            {
                case '#':
                    a.Id = token.Substring(1);
                    return true;

                case '<':
                    if (token.Length > 2 && token[token.Length - 1] == '>')
                    {
                        a.Tag = token.Substring(1, token.Length - 2);
                        return true;
                    }
                    return false;

                case '@':
                {
                    int sep = NameAnnotations.IndexOfAny(token, 1, '=', ':');
                    if (sep > 1 && sep < token.Length - 1)
                    {
                        a.Events.Add(new KeyValuePair<string, string>(token.Substring(1, sep - 1), token.Substring(sep + 1)));
                        return true;
                    }
                    return false;
                }

                case '.':
                {
                    int sep = NameAnnotations.IndexOfAny(token, 1, '?', '=');
                    if (sep > 1 && sep < token.Length - 1)
                    {
                        string cls = CssText.SanitizeIdent(token.Substring(1, sep - 1));
                        if (cls.Length == 0) return false;
                        a.ClassToggles.Add(new KeyValuePair<string, string>(cls, token.Substring(sep + 1)));
                        return true;
                    }
                    return false;
                }

                case '*':
                    if (token.StartsWith("*each=", System.StringComparison.Ordinal))
                    {
                        string rest = token.Substring(6);
                        if (rest.Length == 0) return false;
                        string[] parts = rest.Split(':');
                        var e = new EachDirective { Collection = parts[0] };
                        if (parts.Length > 1 && parts[1].Length > 0) e.Item = parts[1];
                        if (parts.Length > 2 && parts[2].Length > 0) e.Key = parts[2];
                        a.Each = e;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        // First of `a`/`b` at or after `start`.
        static int IndexOfAny(string s, int start, char a, char b)
        {
            for (int i = start; i < s.Length; i++)
                if (s[i] == a || s[i] == b) return i;
            return -1;
        }
    }
}
