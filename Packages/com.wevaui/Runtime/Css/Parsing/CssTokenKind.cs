namespace Weva.Css {
    public enum CssTokenKind {
        Whitespace,
        Ident,
        Function,
        AtKeyword,
        Hash,
        String,
        Number,
        Percentage,
        Dimension,
        Url,
        // PH3 (CSS Syntax §4.3.5/§4.3.6): lenient-mode recovery tokens. A
        // string with an unescaped newline / a malformed url() used to THROW
        // even with ThrowOnError=false — the exception escaped to
        // WevaDocument, which nulled its state: one bad token anywhere (incl.
        // inside an @import) blanked the entire document. The parser treats
        // these like any unexpected token: the containing declaration/rule
        // fails and is skipped, the rest of the sheet survives.
        BadString,
        BadUrl,
        Delim,
        Comma,
        Colon,
        Semicolon,
        LBrace,
        RBrace,
        LParen,
        RParen,
        LBracket,
        RBracket,
        Eof
    }
}
