using System;

namespace Weva.Css.Container {
    public sealed class ContainerQueryParseException : Exception {
        public int Column { get; }

        public ContainerQueryParseException(string message, int column)
            : base($"{message} (col {column})") {
            Column = column;
        }
    }
}
