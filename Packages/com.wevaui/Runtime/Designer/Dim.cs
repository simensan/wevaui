using System;

namespace Weva.Designer
{
    /// <summary>
    /// A tokenizable dimension: either a literal px value or a reference to a named
    /// token (spacing / radius / type scale). This is how the editor offers "Spacing M"
    /// instead of "16px" while still supporting raw values behind an advanced affordance.
    ///
    /// Implicitly converts from <see cref="double"/> so literal call sites
    /// (<c>node.Gap = 16</c>) stay ergonomic; use <see cref="Token"/> for a token ref.
    /// </summary>
    public readonly struct Dim : IEquatable<Dim>
    {
        /// <summary>Literal px (used when <see cref="TokenName"/> is null).</summary>
        public readonly double Px;
        /// <summary>Token name (without braces), or null for a literal.</summary>
        public readonly string TokenName;

        Dim(double px, string token)
        {
            Px = px;
            TokenName = token;
        }

        public static Dim Of(double px) => new Dim(px, null);
        public static Dim Token(string name) => new Dim(0, name);

        public static implicit operator Dim(double px) => new Dim(px, null);

        public bool HasToken => TokenName != null;
        /// <summary>A literal zero — nothing to emit. Token dims are never "zero".</summary>
        public bool IsZero => TokenName == null && Px == 0;

        /// <summary>Resolve to a CSS value: <c>var(--category-name)</c> for a token, else px.</summary>
        public string Resolve(DesignTokens tokens, string category)
        {
            return TokenName != null
                ? tokens.ResolveDimToken(category, TokenName)
                : DesignCssText.Px(Px);
        }

        public bool Equals(Dim other) => Px.Equals(other.Px) && string.Equals(TokenName, other.TokenName, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is Dim d && Equals(d);
        public override int GetHashCode() => (TokenName?.GetHashCode() ?? 0) * 397 ^ Px.GetHashCode();
        public static bool operator ==(Dim a, Dim b) => a.Equals(b);
        public static bool operator !=(Dim a, Dim b) => !a.Equals(b);

        public override string ToString() => TokenName != null ? "{" + TokenName + "}" : Px.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
