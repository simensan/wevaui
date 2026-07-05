using System;

namespace Weva.Binding {
    public sealed class BindingException : Exception {
        public int Line { get; }
        public int Column { get; }

        public BindingException(string message)
            : base(message) {
            Line = -1;
            Column = -1;
        }

        public BindingException(string message, int line, int column)
            : base(FormatMessage(message, line, column)) {
            Line = line;
            Column = column;
        }

        public BindingException(string message, int line, int column, Exception inner)
            : base(FormatMessage(message, line, column), inner) {
            Line = line;
            Column = column;
        }

        static string FormatMessage(string message, int line, int column) {
            if (line <= 0 && column <= 0) return message;
            return $"{message} (line {line}, col {column})";
        }
    }
}
