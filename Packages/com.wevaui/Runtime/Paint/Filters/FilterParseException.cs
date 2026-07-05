using System;

namespace Weva.Paint.Filters {
    public sealed class FilterParseException : Exception {
        public FilterParseException(string message) : base(message) { }
        public FilterParseException(string message, Exception inner) : base(message, inner) { }
    }
}
