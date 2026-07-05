using System;
using Weva.Dom;

namespace Weva.Css.Container {
    public readonly struct ContainerContext {
        public double InlineSizePx { get; }
        public double BlockSizePx { get; }
        public string Name { get; }
        public ContainerType Type { get; }

        // CON-2: style() queries (`@container style(--foo: bar)`) resolve a
        // custom property on the container element, not on its box geometry.
        // ContainerElement is the resolved container's DOM element and
        // ComputedCustomProperty resolves that element's *computed* custom
        // property value (the box's layout-time ComputedStyle may be a stub
        // that doesn't carry author custom properties — see ContainerResolver).
        // Both are populated by the CascadeEngine after the size-side resolve;
        // size queries ignore them.
        public Element ContainerElement { get; }
        public Func<Element, string, string> ComputedCustomProperty { get; }

        public ContainerContext(double inlineSizePx, double blockSizePx, string name, ContainerType type,
                                Element containerElement = null,
                                Func<Element, string, string> computedCustomProperty = null) {
            InlineSizePx = inlineSizePx;
            BlockSizePx = blockSizePx;
            Name = name;
            Type = type;
            ContainerElement = containerElement;
            ComputedCustomProperty = computedCustomProperty;
        }

        public bool IsEmpty => Type == ContainerType.None;

        public static ContainerContext None => new ContainerContext(0, 0, null, ContainerType.None);
        public static ContainerContext Empty => None;

        public static ContainerContext InlineSize(double inlineSizePx, string name = null, Element element = null) {
            return new ContainerContext(inlineSizePx, 0, name, ContainerType.InlineSize, element);
        }

        public static ContainerContext Size(double inlineSizePx, double blockSizePx, string name = null, Element element = null) {
            return new ContainerContext(inlineSizePx, blockSizePx, name, ContainerType.Size, element);
        }

        // Returns a copy with the style-query custom-property resolver attached.
        // The CascadeEngine owns the resolver (it reads computed styles from its
        // per-element cache), so it threads the delegate in after the size-side
        // resolve built the geometry context.
        public ContainerContext WithStyleResolver(Func<Element, string, string> resolver) {
            return new ContainerContext(InlineSizePx, BlockSizePx, Name, Type, ContainerElement, resolver);
        }
    }
}
