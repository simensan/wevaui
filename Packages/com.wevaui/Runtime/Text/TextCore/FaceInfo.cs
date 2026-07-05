using System;

namespace Weva.Text.TextCore {
    // FaceInfo identifies a single resolved font face. It carries enough information
    // for the backend to load + query glyphs without depending on UnityEngine types
    // in headless code paths. The Path field is interpreted by the active backend:
    // UnityFontEngineBackend treats it as an absolute system path (or a path
    // returned from FontEngine.TryGetSystemFontReferences); the StubBackend ignores
    // it and uses the family name only.
    public readonly struct FaceInfo : IEquatable<FaceInfo> {
        public readonly string Family;
        public readonly string Path;
        public readonly int Weight;
        public readonly int StyleFlags;

        public const int StyleNormal = 0;
        public const int StyleItalic = 1;
        public const int StyleOblique = 2;

        public FaceInfo(string family, string path, int weight, int styleFlags) {
            Family = family;
            Path = path;
            Weight = weight;
            StyleFlags = styleFlags;
        }

        public bool IsValid => !string.IsNullOrEmpty(Family);

        public bool Equals(FaceInfo other) {
            return Family == other.Family
                && Path == other.Path
                && Weight == other.Weight
                && StyleFlags == other.StyleFlags;
        }

        public override bool Equals(object obj) => obj is FaceInfo other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = Family != null ? Family.GetHashCode() : 0;
                h = (h * 397) ^ (Path != null ? Path.GetHashCode() : 0);
                h = (h * 397) ^ Weight;
                h = (h * 397) ^ StyleFlags;
                return h;
            }
        }

        public static bool operator ==(FaceInfo a, FaceInfo b) => a.Equals(b);
        public static bool operator !=(FaceInfo a, FaceInfo b) => !a.Equals(b);

        public static FaceInfo Empty => new FaceInfo(null, null, 400, StyleNormal);
    }
}
