using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Dom;
using Weva.Layout.Scrolling;
using Weva.Reactive;

namespace Weva.Forms {
    // VirtualListController — fills a scrollable host element with a windowed
    // slice of a large data source. Only items whose y-range intersects the
    // viewport (plus a small overscan buffer) are present in the DOM at any
    // time, so a 100k-row source keeps roughly Screen.Height / ItemHeight
    // children in the live tree instead of all 100k.
    //
    // How it works:
    //   * Two `position: relative; height: <bumper>` spacer divs are inserted
    //     before and after the visible window so the scroll container's
    //     intrinsic content size matches `ItemCount * ItemHeight` — that
    //     keeps the scrollbar / scroll math unchanged from a non-virtual
    //     list. The first spacer's height equals the offset of the first
    //     rendered item; the second spacer pads to the total list height.
    //   * `Tick()` reads the current ScrollY from the layout's ScrollContainer
    //     and recomputes the visible window. When the window changes, the
    //     controller swaps the rendered children — items leaving the window
    //     are removed, items entering are constructed via the user-supplied
    //     `itemTemplate(index, data)` callback.
    //   * The controller assumes a uniform item height, supplied once at
    //     construction. Variable-height lists need a measure function and
    //     a different bumper layout — out of scope for v1.
    //
    // Threading: all calls are main-thread (matches the rest of Weva's
    // single-threaded contract). Tick is cheap when the visible window
    // hasn't moved.
    public sealed class VirtualListController<T> {
        public Element Host { get; }
        public double ItemHeight { get; }
        public int Overscan { get; set; } = 4;
        public Func<int, T, Element> ItemTemplate { get; set; }
        public IList<T> Source { get; set; }

        readonly Element topSpacer;
        readonly Element bottomSpacer;
        readonly InvalidationTracker tracker;
        readonly Func<Element, Layout.Boxes.Box> elementToBox;
        readonly ScrollContainer scrollContainer;

        // Live items currently in the DOM, indexed by source position.
        readonly Dictionary<int, Element> live = new();
        int firstLiveIndex = -1;
        int lastLiveIndex = -1;
        double lastViewportTop = double.NaN;
        double lastViewportBottom = double.NaN;
        int lastSourceCount = -1;

        public VirtualListController(Element host, double itemHeight,
                                     Func<Element, Layout.Boxes.Box> elementToBox,
                                     ScrollContainer scrollContainer,
                                     InvalidationTracker tracker) {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (itemHeight <= 0) throw new ArgumentException("ItemHeight must be > 0", nameof(itemHeight));
            Host = host;
            ItemHeight = itemHeight;
            this.elementToBox = elementToBox;
            this.scrollContainer = scrollContainer;
            this.tracker = tracker;
            // Seed with one spacer at top and bottom so the host's content
            // height starts correct on first paint.
            topSpacer = MakeSpacer(0);
            bottomSpacer = MakeSpacer(0);
            host.AppendChild(topSpacer);
            host.AppendChild(bottomSpacer);
        }

        // Forces the controller to fully rebuild on the next Tick. Call after
        // mutating the data source's count or replacing items in bulk so the
        // window resizes and re-templates from scratch.
        public void Invalidate() {
            firstLiveIndex = lastLiveIndex = -1;
            lastViewportTop = lastViewportBottom = double.NaN;
        }

        // Recomputes the visible window and swaps DOM children to match.
        // Cheap O(1) when scroll hasn't moved enough to change which items
        // intersect the viewport; O(window-size) when it has. Lifecycle.Update
        // is the natural Tick driver.
        public void Tick() {
            var data = Source;
            if (ItemTemplate == null || data == null) return;
            int count = data.Count;
            var box = elementToBox?.Invoke(Host);
            if (box == null) return;
            var state = scrollContainer?.Get(box);
            double viewportH = box.Height - box.PaddingTop - box.PaddingBottom - box.BorderTop - box.BorderBottom;
            if (viewportH <= 0) return;
            double scrollY = state != null ? state.ScrollY : 0;
            double viewportTop = scrollY;
            double viewportBottom = scrollY + viewportH;

            // Skip the rebuild when the viewport rectangle in content space
            // hasn't moved by even a fraction of an item — the rendered set
            // is still correct.
            if (count == lastSourceCount &&
                Math.Abs(viewportTop - lastViewportTop) < 0.5 &&
                Math.Abs(viewportBottom - lastViewportBottom) < 0.5) return;
            lastViewportTop = viewportTop;
            lastViewportBottom = viewportBottom;
            lastSourceCount = count;

            int desiredFirst = (int)Math.Floor(viewportTop / ItemHeight) - Overscan;
            int desiredLast = (int)Math.Ceiling(viewportBottom / ItemHeight) + Overscan - 1;
            if (desiredFirst < 0) desiredFirst = 0;
            if (desiredLast >= count) desiredLast = count - 1;

            // Drop items that left the window.
            var stale = new List<int>();
            foreach (var kv in live) {
                if (kv.Key < desiredFirst || kv.Key > desiredLast) stale.Add(kv.Key);
            }
            for (int i = 0; i < stale.Count; i++) {
                int key = stale[i];
                var elem = live[key];
                Host.RemoveChild(elem);
                live.Remove(key);
            }
            // Add items that entered the window. Maintain DOM order:
            // insert each new item between topSpacer and bottomSpacer in
            // the correct sorted slot. A single AppendChild always lands
            // before bottomSpacer (which we explicitly keep last after the
            // pass via reorder).
            for (int i = desiredFirst; i <= desiredLast; i++) {
                if (live.ContainsKey(i)) continue;
                var elem = ItemTemplate(i, data[i]);
                if (elem == null) continue;
                Host.AppendChild(elem);
                live[i] = elem;
            }
            // Restore canonical order: [topSpacer, items in ascending index,
            // bottomSpacer]. Cheap to do unconditionally because the list
            // length is the visible-window count not the data count.
            ReorderChildren(desiredFirst, desiredLast);

            firstLiveIndex = desiredFirst;
            lastLiveIndex = desiredLast;

            // Update spacer heights so scrollHeight matches the full data set.
            double topHeight = desiredFirst * ItemHeight;
            double bottomHeight = (count - 1 - desiredLast) * ItemHeight;
            if (bottomHeight < 0) bottomHeight = 0;
            SetSpacerHeight(topSpacer, topHeight);
            SetSpacerHeight(bottomSpacer, bottomHeight);
            tracker?.MarkDirty(Host, InvalidationKind.Layout | InvalidationKind.Paint);
        }

        void ReorderChildren(int first, int last) {
            // Detach everything we manage and re-attach in the desired order.
            // Cheap because we only manage live items + 2 spacers.
            Host.RemoveChild(topSpacer);
            for (int i = first; i <= last; i++) {
                if (live.TryGetValue(i, out var elem)) Host.RemoveChild(elem);
            }
            Host.RemoveChild(bottomSpacer);
            Host.AppendChild(topSpacer);
            for (int i = first; i <= last; i++) {
                if (live.TryGetValue(i, out var elem)) Host.AppendChild(elem);
            }
            Host.AppendChild(bottomSpacer);
        }

        static Element MakeSpacer(double height) {
            var el = new Element("div");
            el.SetAttribute("class", "ui-virtual-spacer");
            el.SetAttribute("style", BuildSpacerStyle(height));
            return el;
        }

        static void SetSpacerHeight(Element spacer, double height) {
            spacer.SetAttribute("style", BuildSpacerStyle(height));
        }

        static string BuildSpacerStyle(double height) {
            return $"width:100%;height:{height.ToString("R", CultureInfo.InvariantCulture)}px;flex-shrink:0;pointer-events:none";
        }
    }
}
