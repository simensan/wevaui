using System.Collections.Generic;

namespace Weva.Layout.Flex {
    internal sealed class FlexLine {
        public readonly List<int> ItemIndices = new List<int>();
        public double MainSize;
        public double CrossSize;
        public double CrossOffset;
    }
}
