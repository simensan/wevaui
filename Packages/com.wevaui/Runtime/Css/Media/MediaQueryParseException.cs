using System;

namespace Weva.Css.Media {
    public sealed class MediaQueryParseException : Exception {
        public int Column { get; }

        public MediaQueryParseException(string message, int column)
            : base($"{message} (col {column})") {
            Column = column;
        }
    }
}
