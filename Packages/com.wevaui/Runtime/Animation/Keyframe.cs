using System;
using System.Collections.Generic;

namespace Weva.Animation {
    public sealed class Keyframe {
        public double Position { get; }
        public Dictionary<string, string> Properties { get; }

        public Keyframe(double position, Dictionary<string, string> properties) {
            Position = position;
            Properties = properties ?? throw new ArgumentNullException(nameof(properties));
        }
    }
}
