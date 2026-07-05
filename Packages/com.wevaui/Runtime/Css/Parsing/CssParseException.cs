using System;

namespace Weva.Css {
    public sealed class CssParseException : Exception {
        public int Line { get; }
        public int Column { get; }

        public CssParseException(string message, int line, int column)
            : base($"{message} (line {line}, col {column})") {
            Line = line;
            Column = column;
        }
    }
}
