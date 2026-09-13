using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    internal sealed class FakeHitTester : IHitTester {
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

    internal static class EventTestHelpers {
        public static Document Html(string s) => HtmlParser.Parse(s);

        public static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        public static Element ById(Document doc, string id) => doc.GetElementById(id);
    }
}
