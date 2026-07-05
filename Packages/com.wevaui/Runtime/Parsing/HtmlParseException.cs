using System;

namespace Weva.Parsing {
    public sealed class HtmlParseException : Exception {
        public int Line { get; }
        public int Column { get; }

        public HtmlParseException(string message, int line, int column)
            : base($"{message} (line {line}, col {column})") {
            Line = line;
            Column = column;
        }
    }
}
