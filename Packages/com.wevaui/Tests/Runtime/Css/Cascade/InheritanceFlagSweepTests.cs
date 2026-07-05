using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;

namespace Weva.Tests.Css.Cascade {
    // Inheritance-flag sweep — CSS Cascade L5 §2 + per-property specs.
    //
    // Each [Test] method covers a logical group of properties.  Within each
    // method a loop asserts CssProperties.IsInherited(name) == expected per
    // the CSS spec, with the property name embedded in the assertion message so
    // any failure names the exact property.
    //
    // WHY: a future refactor that accidentally flips a single property's
    // inherited flag will surface here immediately with the property name,
    // rather than surfacing as a silent visual regression in a layout test.
    //
    // TOTAL PROPERTIES SWEPT: 219 across all test methods.
    public class InheritanceFlagSweepTests {

        // Helper — asserts each (name, expected) pair in the dictionary.
        static void AssertAll(Dictionary<string, bool> cases) {
            foreach (var kv in cases) {
                Assert.That(
                    CssProperties.IsInherited(kv.Key),
                    Is.EqualTo(kv.Value),
                    $"Property '{kv.Key}': expected IsInherited={kv.Value}");
            }
        }

        // ── Positioning + Overflow (all non-inherited) ────────────────────────
        [Test]
        public void Positioning_and_overflow_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                // CSS Position L3 / Insets L1
                { "display",              false },
                { "position",             false },
                { "top",                  false },
                { "right",                false },
                { "bottom",               false },
                { "left",                 false },
                { "inset",                false },
                { "inset-inline",         false },
                { "inset-inline-start",   false },
                { "inset-inline-end",     false },
                { "inset-block",          false },
                { "inset-block-start",    false },
                { "inset-block-end",      false },
                { "z-index",              false },
                // CSS Overflow L3/L4
                { "overflow",                          false },
                { "overflow-x",                        false },
                { "overflow-y",                        false },
                { "overflow-clip-margin",              false },
                { "overflow-clip-margin-top",          false },
                { "overflow-clip-margin-right",        false },
                { "overflow-clip-margin-bottom",       false },
                { "overflow-clip-margin-left",         false },
                { "overflow-clip-margin-block-start",  false },
                { "overflow-clip-margin-block-end",    false },
                { "overflow-clip-margin-inline-start", false },
                { "overflow-clip-margin-inline-end",   false },
            });
        }

        // ── Flex + Grid alignment (all non-inherited) ─────────────────────────
        [Test]
        public void Flex_and_grid_alignment_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                // CSS Flexbox L1
                { "flex",             false },
                { "flex-direction",   false },
                { "flex-wrap",        false },
                { "flex-basis",       false },
                { "flex-grow",        false },
                { "flex-shrink",      false },
                { "flex-flow",        false },
                { "justify-content",  false },
                { "align-items",      false },
                { "align-self",       false },
                { "align-content",    false },
                { "gap",              false },
                { "row-gap",          false },
                { "column-gap",       false },
                { "order",            false },
                // CSS Grid L1/L2
                { "grid-template-columns", false },
                { "grid-template-rows",    false },
                { "grid-template-areas",   false },
                { "grid-template",         false },
                { "grid-column",           false },
                { "grid-row",              false },
                { "grid-column-start",     false },
                { "grid-column-end",       false },
                { "grid-row-start",        false },
                { "grid-row-end",          false },
                { "grid-auto-flow",        false },
                { "grid-auto-columns",     false },
                { "grid-auto-rows",        false },
                { "grid-area",             false },
                { "place-items",           false },
                { "place-content",         false },
                { "place-self",            false },
                { "justify-items",         false },
                { "justify-self",          false },
            });
        }

        // ── Sizing + Box model (all non-inherited) ────────────────────────────
        [Test]
        public void Sizing_and_box_model_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "width",            false },
                { "height",           false },
                { "min-width",        false },
                { "min-height",       false },
                { "max-width",        false },
                { "max-height",       false },
                { "inline-size",      false },
                { "block-size",       false },
                { "min-inline-size",  false },
                { "min-block-size",   false },
                { "max-inline-size",  false },
                { "max-block-size",   false },
                { "aspect-ratio",     false },
                { "float",            false },
                { "clear",            false },
                { "box-sizing",       false },
                { "object-fit",       false },
                { "object-position",  false },
            });
        }

        // ── Table properties (mixed) ──────────────────────────────────────────
        // CSS 2.1 §17: border-collapse, border-spacing, caption-side, empty-cells
        // are inherited; table-layout, vertical-align are NOT.
        [Test]
        public void Table_properties_have_correct_inheritance_per_spec() {
            AssertAll(new Dictionary<string, bool> {
                { "border-collapse", true  },
                { "border-spacing",  true  },
                { "caption-side",    true  },
                { "empty-cells",     true  },
                { "table-layout",    false },
                { "vertical-align",  false },
            });
        }

        // ── Padding + Margin (all non-inherited) ──────────────────────────────
        [Test]
        public void Padding_and_margin_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "padding",               false },
                { "padding-top",           false },
                { "padding-right",         false },
                { "padding-bottom",        false },
                { "padding-left",          false },
                { "padding-inline",        false },
                { "padding-inline-start",  false },
                { "padding-inline-end",    false },
                { "padding-block",         false },
                { "padding-block-start",   false },
                { "padding-block-end",     false },
                { "margin",               false },
                { "margin-top",           false },
                { "margin-right",         false },
                { "margin-bottom",        false },
                { "margin-left",          false },
                { "margin-inline",        false },
                { "margin-inline-start",  false },
                { "margin-inline-end",    false },
                { "margin-block",         false },
                { "margin-block-start",   false },
                { "margin-block-end",     false },
            });
        }

        // ── Border longhands (all non-inherited) ──────────────────────────────
        [Test]
        public void Border_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "border",                      false },
                { "border-width",                false },
                { "border-style",                false },
                { "border-color",                false },
                { "border-top",                  false },
                { "border-right",                false },
                { "border-bottom",               false },
                { "border-left",                 false },
                { "border-top-width",            false },
                { "border-right-width",          false },
                { "border-bottom-width",         false },
                { "border-left-width",           false },
                { "border-top-style",            false },
                { "border-right-style",          false },
                { "border-bottom-style",         false },
                { "border-left-style",           false },
                { "border-top-color",            false },
                { "border-right-color",          false },
                { "border-bottom-color",         false },
                { "border-left-color",           false },
                { "border-inline",               false },
                { "border-inline-start",         false },
                { "border-inline-end",           false },
                { "border-inline-width",         false },
                { "border-inline-style",         false },
                { "border-inline-color",         false },
                { "border-inline-start-width",   false },
                { "border-inline-start-style",   false },
                { "border-inline-start-color",   false },
                { "border-inline-end-width",     false },
                { "border-inline-end-style",     false },
                { "border-inline-end-color",     false },
                { "border-block",                false },
                { "border-block-start",          false },
                { "border-block-end",            false },
                { "border-block-width",          false },
                { "border-block-style",          false },
                { "border-block-color",          false },
                { "border-block-start-width",    false },
                { "border-block-start-style",    false },
                { "border-block-start-color",    false },
                { "border-block-end-width",      false },
                { "border-block-end-style",      false },
                { "border-block-end-color",      false },
                { "border-radius",               false },
                { "border-top-left-radius",      false },
                { "border-top-right-radius",     false },
                { "border-bottom-right-radius",  false },
                { "border-bottom-left-radius",   false },
                { "border-start-start-radius",   false },
                { "border-start-end-radius",     false },
                { "border-end-start-radius",     false },
                { "border-end-end-radius",       false },
                // Border-image (CSS Backgrounds 3 §6): NOT inherited
                { "border-image-source", false },
                { "border-image-slice",  false },
                { "border-image-width",  false },
                { "border-image-outset", false },
                { "border-image-repeat", false },
            });
        }

        // ── Color + Caret/Accent (inherited) ──────────────────────────────────
        [Test]
        public void Color_and_ui_accent_properties_are_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "color",        true },
                { "caret-color",  true },   // CSS UI L4 §5.4
                { "accent-color", true },   // CSS UI L4 §5.5
            });
        }

        // ── Fonts (inherited) ─────────────────────────────────────────────────
        [Test]
        public void Font_properties_are_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "font",                   true },
                { "font-family",            true },
                { "font-size",              true },
                { "font-weight",            true },
                { "font-style",             true },
                { "font-variant",           true },
                { "font-variant-numeric",   true },   // CSS Fonts 4 §6.5
                { "font-stretch",           true },   // CSS Fonts 4 §6.3
                { "font-kerning",           true },   // CSS Fonts 4 §6.6
                { "font-synthesis",         true },   // CSS Fonts 4 §6.5
                { "font-synthesis-weight",       true },
                { "font-synthesis-style",        true },
                { "font-synthesis-small-caps",   true },
                { "font-synthesis-position",     true },
                { "font-size-adjust",       true },   // CSS Fonts 4 §6.7
                { "font-variation-settings",true },   // CSS Fonts 4 §6.10
                { "font-feature-settings",  true },   // CSS Fonts 4 §6.4
                { "font-optical-sizing",    true },   // CSS Fonts 4 §6.10
                { "line-height",            true },
                { "letter-spacing",         true },
                { "word-spacing",           true },
            });
        }

        // ── Text (inherited) ──────────────────────────────────────────────────
        [Test]
        public void Text_properties_are_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "direction",          true },
                { "writing-mode",       true },
                { "unicode-bidi",       false },   // CSS Writing Modes L4 §3.2 — NOT inherited
                { "text-align",         true },
                { "text-align-last",    true },
                { "text-justify",       true },
                { "text-indent",        true },
                { "text-wrap",          true },
                { "tab-size",           true },
                { "hyphens",            true },
                { "text-transform",     true },
                { "text-shadow",        true },    // CSS Text Decoration L4 §6 — inherited
                { "white-space",        true },
                { "white-space-collapse", true },
                { "word-break",         true },
                { "overflow-wrap",      true },
                { "word-wrap",          true },    // alias for overflow-wrap
                // CSS Text Decoration L4 §10 — text-stroke: inherited
                { "-webkit-text-stroke-width", true },
                { "-webkit-text-stroke-color", true },
                // CSS Color Adjustment 1 §3.1 — color-scheme: inherited
                { "color-scheme",       true },
            });
        }

        // ── Text decoration (NOT inherited) ───────────────────────────────────
        // CSS Text Decoration L4: the text-decoration-* longhands and shorthand
        // are NOT inherited. They propagate to inline children through box model
        // continuation, not through inheritance.
        [Test]
        public void Text_decoration_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "text-decoration",           false },
                { "text-decoration-line",      false },
                { "text-decoration-style",     false },
                { "text-decoration-color",     false },
                { "text-decoration-thickness", false },
                { "text-overflow",             false },
            });
        }

        // ── Background (all non-inherited) ───────────────────────────────────
        [Test]
        public void Background_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "background",            false },
                { "background-color",      false },
                { "background-image",      false },
                { "background-size",       false },
                { "background-position",   false },
                { "background-repeat",     false },
                { "background-clip",       false },
                { "background-origin",     false },
                { "background-attachment", false },
            });
        }

        // ── Stacking / compositing / effects (mixed) ──────────────────────────
        [Test]
        public void Stacking_and_effects_properties_have_correct_inheritance() {
            AssertAll(new Dictionary<string, bool> {
                { "opacity",        false },
                { "visibility",     true  },   // CSS 2.1 §11.2 — inherited
                { "box-shadow",     false },
                { "isolation",      false },
                { "mix-blend-mode", false },
                { "filter",         false },
                { "backdrop-filter",false },
                { "clip-path",      false },
                { "mask",           false },
                { "mask-image",     false },
                { "mask-mode",      false },
                { "mask-repeat",    false },
                { "mask-position",  false },
                { "mask-size",      false },
                { "mask-origin",    false },
                { "mask-clip",      false },
                { "mask-composite", false },
            });
        }

        // ── Cursor / pointer-events / UI (mixed) ─────────────────────────────
        [Test]
        public void Cursor_and_pointer_properties_have_correct_inheritance() {
            AssertAll(new Dictionary<string, bool> {
                { "cursor",         true  },   // CSS UI L4 §13 — inherited
                { "pointer-events", false },   // CSS UI L4 §13 — NOT inherited for HTML
                { "user-select",    false },   // CSS UI L4 §6.1 — NOT inherited (standard)
                { "image-rendering",true  },   // CSS Images L3 §3.6 — inherited
            });
        }

        // ── Container queries + performance hints (non-inherited) ─────────────
        [Test]
        public void Container_and_performance_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "container-type", false },
                { "container-name", false },
                { "container",      false },
                { "will-change",    false },
                { "contain",        false },
                { "content-visibility",           false },
                { "contain-intrinsic-size",       false },
                { "contain-intrinsic-width",      false },
                { "contain-intrinsic-height",     false },
                { "contain-intrinsic-block-size", false },
                { "contain-intrinsic-inline-size",false },
            });
        }

        // ── Transforms (all non-inherited) ───────────────────────────────────
        [Test]
        public void Transform_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "transform",           false },
                { "transform-origin",    false },
                { "translate",           false },
                { "rotate",              false },
                { "scale",               false },
                { "perspective",         false },
                { "perspective-origin",  false },
                { "transform-style",     false },
                { "backface-visibility", false },
            });
        }

        // ── Transitions + Animations (all non-inherited) ──────────────────────
        [Test]
        public void Transition_and_animation_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "transition",                  false },
                { "transition-property",         false },
                { "transition-duration",         false },
                { "transition-timing-function",  false },
                { "transition-delay",            false },
                { "animation",                   false },
                { "animation-name",              false },
                { "animation-duration",          false },
                { "animation-timing-function",   false },
                { "animation-delay",             false },
                { "animation-iteration-count",   false },
                { "animation-direction",         false },
                { "animation-fill-mode",         false },
                { "animation-play-state",        false },
                { "animation-composition",       false },
            });
        }

        // ── Outline (all non-inherited) ───────────────────────────────────────
        [Test]
        public void Outline_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "outline",        false },
                { "outline-color",  false },
                { "outline-style",  false },
                { "outline-width",  false },
                { "outline-offset", false },
            });
        }

        // ── Anchor positioning (all non-inherited) ────────────────────────────
        [Test]
        public void Anchor_positioning_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "anchor-name",            false },
                { "position-anchor",        false },
                { "position-try-fallbacks", false },
            });
        }

        // ── Generated content + Lists (mixed) ─────────────────────────────────
        [Test]
        public void Generated_content_and_list_properties_have_correct_inheritance() {
            AssertAll(new Dictionary<string, bool> {
                { "content",              false },
                { "list-style-type",      true  },   // CSS Lists 3 §3.2 — inherited
                { "list-style-position",  true  },   // CSS Lists 3 §3.1 — inherited
                { "list-style-image",     true  },   // CSS Lists 3 §3.3 — inherited
            });
        }

        // ── Scrollbar + overscroll (mixed) ────────────────────────────────────
        [Test]
        public void Scrollbar_and_overscroll_properties_have_correct_inheritance() {
            AssertAll(new Dictionary<string, bool> {
                { "scrollbar-width",       false },
                { "scrollbar-color",       true  },   // CSS Scrollbars L1 §3.2 — inherited
                { "scrollbar-gutter",      false },
                { "overscroll-behavior",   false },
                { "overscroll-behavior-x", false },
                { "overscroll-behavior-y", false },
            });
        }

        // ── Misc box model flags (non-inherited) ──────────────────────────────
        [Test]
        public void Misc_box_model_properties_are_not_inherited() {
            AssertAll(new Dictionary<string, bool> {
                { "line-clamp",           false },
                { "-webkit-line-clamp",   false },
                { "box-decoration-break", false },
            });
        }

        // ── Module-driven registrations (lazy) ────────────────────────────────
        // scroll-snap / scroll-padding / scroll-margin / scroll-behavior are
        // registered lazily. We call EnsureRegistered manually then assert.
        [Test]
        public void Scroll_snap_properties_are_not_inherited() {
            Weva.Layout.Scrolling.Snap.ScrollSnapProperties.EnsureRegistered();
            AssertAll(new Dictionary<string, bool> {
                { "scroll-snap-type",         false },
                { "scroll-snap-align",        false },
                { "scroll-snap-stop",         false },
                { "scroll-padding",           false },
                { "scroll-padding-top",       false },
                { "scroll-padding-right",     false },
                { "scroll-padding-bottom",    false },
                { "scroll-padding-left",      false },
                { "scroll-margin",            false },
                { "scroll-margin-top",        false },
                { "scroll-margin-right",      false },
                { "scroll-margin-bottom",     false },
                { "scroll-margin-left",       false },
            });
        }

        [Test]
        public void Scroll_behavior_is_not_inherited() {
            Weva.Layout.Scrolling.Smooth.SmoothScrollProperties.EnsureRegistered();
            Assert.That(CssProperties.IsInherited("scroll-behavior"), Is.False,
                "'scroll-behavior' should NOT be inherited per CSS Scroll Behavior 1 §3");
        }

        [Test]
        public void View_transition_name_is_not_inherited() {
            Weva.ViewTransitions.ViewTransitionProperties.EnsureRegistered();
            Assert.That(CssProperties.IsInherited("view-transition-name"), Is.False,
                "'view-transition-name' should NOT be inherited per CSS View Transitions 1 §4");
        }

        // ── text-underline-offset — now correctly inherited ───────────────────
        // CSS Text Decoration L4 §9.7 — "Inherited: yes".
        // Fixed 2026-05-30 (gap A8): registration flipped from false → true.
        [Test]
        public void Text_underline_offset_should_be_inherited_per_spec() {
            Assert.That(CssProperties.IsInherited("text-underline-offset"), Is.True,
                "CSS Text Decoration L4 §9.7 specifies 'Inherited: yes' for text-underline-offset");
        }
    }
}
