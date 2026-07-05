using System;

namespace Weva.Css.Values {
    public sealed class CssValueParseException : Exception {
        public int Column { get; }

        public CssValueParseException(string message, int column)
            : base($"{message} (col {column})") {
            Column = column;
        }
    }
}
