using System;

namespace Weva.Components {
    public sealed class ComponentExpansionException : Exception {
        public int Depth { get; }
        public string Tag { get; }

        public ComponentExpansionException(string message, int depth, string tag)
            : base(message) {
            Depth = depth;
            Tag = tag;
        }
    }
}
