using System;
using System.Collections.Generic;

namespace Weva.Animation {
    public sealed class KeyframeAnimation {
        public string Name { get; }
        public List<Keyframe> Keyframes { get; }

        public KeyframeAnimation(string name, IEnumerable<Keyframe> keyframes) {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (keyframes == null) throw new ArgumentNullException(nameof(keyframes));

            var sorted = new List<Keyframe>(keyframes);
            sorted.Sort((a, b) => a.Position.CompareTo(b.Position));

            bool hasZero = sorted.Count > 0 && sorted[0].Position <= 0;
            bool hasOne = sorted.Count > 0 && sorted[sorted.Count - 1].Position >= 1;

            if (!hasZero) {
                sorted.Insert(0, new Keyframe(0, new Dictionary<string, string>()));
            }
            if (!hasOne) {
                sorted.Add(new Keyframe(1, new Dictionary<string, string>()));
            }

            Keyframes = sorted;
        }
    }
}
