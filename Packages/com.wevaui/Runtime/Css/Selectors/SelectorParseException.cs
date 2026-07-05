using System;

namespace Weva.Css.Selectors {
    public sealed class SelectorParseException : Exception {
        public int Line { get; }
        public int Column { get; }

        public SelectorParseException(string message, int column)
            : this(message, 1, column) { }

        public SelectorParseException(string message, int line, int column)
            : base($"{message} (line {line}, col {column})") {
            Line = line;
            Column = column;
        }
    }
}
