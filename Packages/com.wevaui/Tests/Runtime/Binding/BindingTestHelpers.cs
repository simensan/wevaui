using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Binding {
    internal sealed class BindingFakeHitTester : IHitTester {
        readonly struct Rect {
            public readonly double X, Y, W, H;
            public readonly Element E;
            public readonly int Order;
            public Rect(double x, double y, double w, double h, Element e, int o) {
                X = x; Y = y; W = w; H = h; E = e; Order = o;
            }
            public bool Contains(double px, double py) {
                return px >= X && px < X + W && py >= Y && py < Y + H;
            }
        }
        readonly List<Rect> rects = new();
        int counter;

        public void Add(Element e, double x, double y, double w, double h) {
            rects.Add(new Rect(x, y, w, h, e, counter++));
        }

        public Element HitTest(double x, double y) {
            Element best = null;
            int bestOrder = -1;
            foreach (var r in rects) {
                if (!r.Contains(x, y)) continue;
                if (r.Order > bestOrder) { bestOrder = r.Order; best = r.E; }
            }
            return best;
        }
    }
}
