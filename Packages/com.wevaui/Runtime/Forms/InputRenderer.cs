using System;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Forms {
    // InputRenderer — emits the form-control specific decorations on top of the
    // generic box decoration the BoxToPaintConverter already paints.
    //
    // Decoupled from BoxToPaintConverter so the converter doesn't grow a
    // form-control switch statement. Callers (e.g. the document lifecycle's
    // post-paint hook) walk the box tree, call AppendOverlays for every
    // form-control box, and append the resulting commands to the main paint
    // list. This is the additive integration path called for in the brief.
    //
    // The renderer is intentionally text-engine-agnostic: caret and selection
    // rectangles use the box's reported font size as the cursor height; X
    // offsets within the text are computed via a caller-supplied
    // metrics function (so unit tests can use a monospace stub and the real
    // pipeline can plug in TextCore later). Without metrics we still draw the
    // checkbox/radio glyphs, which need no text shaping.
    public static class InputRenderer {
        public delegate double TextWidthFunc(string text, double fontSize);

        // PF2: the password bullet mask was minted TWICE per repaint of a
        // focused password field (`new string('•', n)` in the caret hook AND
        // the overlay painter) — per blink flip and per keystroke. One slot
        // suffices: both callers ask for the same length within a frame, and
        // the mask changes by one char per edit. Main-thread only.
        static string s_BulletCache = "";

        public static string BulletMask(int length) {
            if (length <= 0) return "";
            var cached = s_BulletCache;
            if (cached.Length == length) return cached;
            cached = new string('•', length);
            s_BulletCache = cached;
            return cached;
        }

        // Resolver invoked by the renderer to retrieve a pseudo-element's
        // cascaded ComputedStyle for the given host. Document lifecycle
        // wires this to CascadeEngine.ComputePlaceholder / ComputeSelection
        // so author rules flow into placeholder color and selection
        // background. Returning null means "no rule matched" — the
        // renderer falls back to its UA default.
        public delegate ComputedStyle PseudoStyleResolver(Element host);

        // Slate / indigo palette mirrors the UA defaults in the brief.
        static readonly LinearColor CaretColor = LinearColor.Black;
        static readonly LinearColor SelectionColor = new LinearColor(0.392f, 0.514f, 0.953f, 0.35f); // ~#6391f3 alpha
        static readonly LinearColor CheckColor = new LinearColor(0.090f, 0.196f, 0.671f, 1f);       // indigo-600 ish
        static readonly LinearColor BoxFill = new LinearColor(1f, 1f, 1f, 1f);
        static readonly LinearColor BorderColor = new LinearColor(0.6f, 0.6f, 0.6f, 1f);

        public static void AppendOverlays(Element element, Box box, InputState state, PaintList output, TextWidthFunc widthOf) {
            AppendOverlays(element, box, state, output, widthOf, null, null);
        }

        // Overload that threads the cascade-resolved ::placeholder and
        // ::selection styles into the text overlay path. Either resolver
        // may be null (test code typically passes null and gets the UA
        // default palette + faded-host placeholder color). The document
        // lifecycle wires both to the CascadeEngine.
        public static void AppendOverlays(Element element, Box box, InputState state, PaintList output,
                                          TextWidthFunc widthOf,
                                          PseudoStyleResolver placeholderStyleOf,
                                          PseudoStyleResolver selectionStyleOf) {
            if (element == null || box == null || output == null) return;
            switch (element.TagName) {
                case "input": {
                    var type = element.GetAttribute("type");
                    if (type == null || type == "text" || type == "password" || type == "email" ||
                        type == "search" || type == "tel" || type == "url" || type == "number" || type == "") {
                        DrawTextOverlay(box, state, element, output, widthOf, placeholderStyleOf, selectionStyleOf);
                    } else if (type == "checkbox") {
                        DrawCheckboxGlyph(box, element, output);
                    } else if (type == "radio") {
                        DrawRadioGlyph(box, element, output);
                    } else if (type == "range") {
                        DrawRangeTrack(box, state, element, output);
                    }
                    break;
                }
                case "textarea":
                    DrawTextOverlay(box, state, element, output, widthOf, placeholderStyleOf, selectionStyleOf);
                    break;
                case "select":
                    DrawSelectChrome(box, element, output, widthOf);
                    break;
            }
        }

        // U+2022 BULLET — the canonical masking glyph for <input type="password">
        // matching what every shipping browser uses for the visual placeholder.
        const char PasswordMaskChar = '•';

        static void DrawTextOverlay(Box box, InputState state, Element element, PaintList output, TextWidthFunc widthOf,
                                    PseudoStyleResolver placeholderStyleOf, PseudoStyleResolver selectionStyleOf) {
            double padX = box.PaddingLeft + box.BorderLeft;
            double padY = box.PaddingTop + box.BorderTop;
            double fontSize = ResolveFontSize(box);

            // CSS UI: single-line <input> vertically centers its content within
            // the content box. The text renderer treats bounds.Y as cap-top
            // and places baseline at bounds.Y + ascent, with descenders hanging
            // below — so the visible glyph span is the line-height: normal
            // factor × fontSize (cap-top to descender-bottom for typical
            // sans-serif; CSS Values L4 §6.2). Centering on fontSize alone
            // leaves descenders biasing the optical center downward; factor in
            // the natural-line-height approximation to match what every browser
            // ships for default-line-height inputs. Shared via
            // StyleResolver.DefaultLineHeightFactor so this site cannot drift
            // from BoxToPaintConverter / EllipsisHelper / MonoFontMetrics.
            double contentHeight = box.Height - box.BorderTop - box.BorderBottom
                                   - box.PaddingTop - box.PaddingBottom;
            double glyphSpan = fontSize * Weva.Layout.StyleResolver.DefaultLineHeightFactor;
            double textY = box.Y + padY + System.Math.Max(0, (contentHeight - glyphSpan) * 0.5);

            // Placeholder rendering: when the input has no value and a
            // `placeholder` attribute, paint that string in the cascaded
            // ::placeholder color (or a faded host color when no rule
            // matched). state may be null for a freshly-built control
            // that has no InputState yet — that still counts as "empty"
            // so the placeholder paints.
            string rawValue = state?.Value ?? "";
            if (rawValue.Length == 0 && element != null) {
                string placeholderText = element.GetAttribute("placeholder");
                if (!string.IsNullOrEmpty(placeholderText)) {
                    var placeholderStyle = placeholderStyleOf != null ? placeholderStyleOf(element) : null;
                    var placeholderColor = ResolvePlaceholderColor(box, placeholderStyle);
                    var bounds = new Rect(box.X + padX, textY, box.Width - padX, fontSize);
                    output.Add(new DrawTextCommand(bounds, placeholderText, default, placeholderColor, default));
                }
            }

            if (state == null) return;
            string raw = rawValue;

            // Password masking: the underlying state.Value still holds the
            // user's plaintext (so the model, selection indices, and caret
            // arithmetic all stay in sync), but selection/caret X positions
            // are computed against an all-bullets render so width metrics
            // match what the user sees on screen. Selection length and caret
            // index are unchanged — clicking, arrow-keying, or selecting
            // continues to operate on real character offsets.
            bool masked = element != null
                          && element.TagName == "input"
                          && element.GetAttribute("type") == "password";
            string value = masked ? new string(PasswordMaskChar, raw.Length) : raw;

            // Selection rectangle.
            if (state.HasSelection && widthOf != null) {
                int s = state.SelectionStart;
                int e = state.SelectionEnd;
                double xs = padX + widthOf(value.Substring(0, s), fontSize);
                double xe = padX + widthOf(value.Substring(0, e), fontSize);
                if (xe > xs) {
                    var selRect = new Rect(box.X + xs, textY, xe - xs, fontSize);
                    var selectionStyle = selectionStyleOf != null && element != null ? selectionStyleOf(element) : null;
                    var selBg = ResolveSelectionBackground(box, selectionStyle);
                    output.Add(new FillRectCommand(selRect, Brush.SolidColor(selBg), BorderRadii.Zero));
                }
            }

            // Caret — single-pixel vertical bar at the cursor index. Real DOM
            // browsers blink it; v1 always shows it when the box is the current
            // input and the model has no selection. The visibility decision
            // (focused / not) is the caller's; we draw whenever AppendOverlays
            // is invoked.
            if (widthOf != null) {
                double cx = padX + widthOf(value.Substring(0, state.CursorIndex), fontSize);
                var caret = new Rect(box.X + cx, textY, 1, fontSize);
                output.Add(new FillRectCommand(caret, Brush.SolidColor(ResolveCaretColor(box)), BorderRadii.Zero));
            }
        }

        // Resolves the placeholder text color. Priority:
        //   1. ::placeholder { color: ... } from the cascade — most
        //      explicit author intent.
        //   2. fall back to currentColor of the host scaled by 0.5 so
        //      placeholder reads as a visibly faded version of the
        //      input's text color, matching the audit's "host color × 0.5"
        //      contract.
        // If even the host has no cascaded color we land on faded black,
        // which is what every shipping browser also does as a last resort.
        static LinearColor ResolvePlaceholderColor(Box box, ComputedStyle placeholderStyle) {
            if (placeholderStyle != null) {
                string raw = placeholderStyle.Get(CssProperties.ColorId);
                if (!string.IsNullOrEmpty(raw)) {
                    var hostCurrent = box?.Style != null
                        ? ColorResolver.ResolveCurrentColor(box.Style)
                        : LinearColor.Black;
                    if (ColorResolver.TryResolve(raw, hostCurrent, placeholderStyle, out var c)) return c;
                }
            }
            // Fade the host color so placeholder text reads as muted.
            var host = box?.Style != null
                ? ColorResolver.ResolveCurrentColor(box.Style)
                : LinearColor.Black;
            return new LinearColor(host.R * 0.5f, host.G * 0.5f, host.B * 0.5f, host.A * 0.5f);
        }

        // Resolves the selection rect's background color from a
        // cascaded ::selection style. Falls back to the UA default
        // (the historical hard-coded SelectionColor) when no rule
        // matched or the rule didn't set background-color.
        static LinearColor ResolveSelectionBackground(Box box, ComputedStyle selectionStyle) {
            if (selectionStyle == null) return SelectionColor;
            string raw = selectionStyle.Get(CssProperties.BackgroundColorId);
            if (string.IsNullOrEmpty(raw) || raw == "transparent") return SelectionColor;
            var hostCurrent = box?.Style != null
                ? ColorResolver.ResolveCurrentColor(box.Style)
                : LinearColor.Black;
            if (ColorResolver.TryResolve(raw, hostCurrent, selectionStyle, out var c)) return c;
            return SelectionColor;
        }

        // CSS UI 4 §5.4: `caret-color` overrides the UA-default caret color.
        // The initial / explicit `auto` value lets the UA pick — we use the
        // computed `color` (currentColor) so the caret tracks author text
        // color by default and matches WebKit/Blink behaviour.
        static LinearColor ResolveCaretColor(Box box) {
            if (box?.Style == null) return CaretColor;
            string raw = box.Style.Get(CssProperties.CaretColorId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") {
                return ColorResolver.ResolveCurrentColor(box.Style);
            }
            var current = ColorResolver.ResolveCurrentColor(box.Style);
            if (ColorResolver.TryResolve(raw, current, box.Style, out var c)) return c;
            return current;
        }

        static void DrawCheckboxGlyph(Box box, Element element, PaintList output) {
            if (!element.HasAttribute("checked")) return;
            // Draw a check-mark by filling two thin rotated bars. v1 keeps it
            // simple: a centred filled rectangle inset within the box. The
            // backend can swap this for a glyph later.
            double inset = 2;
            var checkRect = new Rect(box.X + inset, box.Y + inset, box.Width - inset * 2, box.Height - inset * 2);
            output.Add(new FillRectCommand(checkRect, Brush.SolidColor(ResolveAccentColor(box)), BorderRadii.Uniform(1)));
        }

        static void DrawRadioGlyph(Box box, Element element, PaintList output) {
            if (!element.HasAttribute("checked")) return;
            // Inner dot at half size, centred.
            double inset = box.Width * 0.25;
            var dot = new Rect(box.X + inset, box.Y + inset, box.Width - inset * 2, box.Height - inset * 2);
            double r = (box.Width - inset * 2) * 0.5;
            output.Add(new FillRectCommand(dot, Brush.SolidColor(ResolveAccentColor(box)), BorderRadii.Uniform(r)));
        }

        // <input type="range"> UA rendering: a filled track up to the current
        // value plus a round thumb at that position. The element's own
        // background paints the unfilled track; we overlay the accent-colored
        // fill and the thumb. The thumb is a fixed minimum size (≥14px) so it
        // stays visible even on a thin (e.g. height:6px) custom track — matching
        // browsers, which draw a large thumb on a thin track.
        static void DrawRangeTrack(Box box, InputState state, Element element, PaintList output) {
            double min = ParseAttrDouble(element, "min", 0.0);
            double max = ParseAttrDouble(element, "max", 100.0);
            if (max <= min) max = min + 1.0;
            double value = ParseRangeValue(state, element, min, max);
            double frac = (value - min) / (max - min);
            // `!(frac >= 0)` also catches NaN (a belt-and-suspenders guard on top
            // of the finite-value parsing above) so geometry can never be NaN.
            if (!(frac >= 0)) frac = 0; else if (frac > 1) frac = 1;

            double left = box.X + box.PaddingLeft + box.BorderLeft;
            double right = box.X + box.Width - box.PaddingRight - box.BorderRight;
            double trackW = right - left;
            if (trackW <= 0) return;
            double top = box.Y + box.PaddingTop + box.BorderTop;
            double contentH = box.Height - box.PaddingTop - box.PaddingBottom - box.BorderTop - box.BorderBottom;
            if (contentH <= 0) contentH = box.Height;
            double cy = top + contentH * 0.5;

            var accent = ResolveAccentColor(box);
            // A thin centred groove with a round knob sized to FIT the content box,
            // so the knob never clips (a fixed 14px thumb overhanging a short box
            // would be cut off). The UA default height (18px) yields a 6px rail +
            // a 14px knob — a distinct handle; the knob reads as a handle whenever
            // the box is taller than the rail.
            double railH = System.Math.Min(contentH, 6.0);
            double thumbD = System.Math.Max(railH, System.Math.Min(contentH, 14.0));
            double railTop = cy - railH * 0.5;
            // Inset the thumb travel so the knob never overflows the rail ends.
            double usable = System.Math.Max(0.0, trackW - thumbD);
            double cx = left + thumbD * 0.5 + frac * usable;

            // Unfilled groove (full width) — a muted accent so the track reads even
            // when the input has no background of its own.
            var groove = new LinearColor(accent.R, accent.G, accent.B, accent.A * 0.3f);
            output.Add(new FillRectCommand(new Rect(left, railTop, trackW, railH),
                Brush.SolidColor(groove), BorderRadii.Uniform(railH * 0.5)));
            // Filled portion (rail start → thumb centre).
            double fillW = cx - left;
            if (fillW > 0) {
                output.Add(new FillRectCommand(new Rect(left, railTop, fillW, railH),
                    Brush.SolidColor(accent), BorderRadii.Uniform(railH * 0.5)));
            }
            // Round thumb knob (centred on the rail; sized to fit the box).
            output.Add(new FillRectCommand(new Rect(cx - thumbD * 0.5, cy - thumbD * 0.5, thumbD, thumbD),
                Brush.SolidColor(accent), BorderRadii.Uniform(thumbD * 0.5)));
        }

        static double ParseAttrDouble(Element element, string attr, double fallback) {
            string raw = element?.GetAttribute(attr);
            // double.TryParse accepts "NaN"/"Infinity" — reject non-finite so a
            // malformed authored attribute can't poison the slider geometry.
            if (!string.IsNullOrEmpty(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)
                && !double.IsNaN(v) && !double.IsInfinity(v)) {
                return v;
            }
            return fallback;
        }

        // Current range value: the live InputState wins (reflects drags/steps),
        // else the `value` attribute, else the midpoint of [min,max] (HTML's
        // default when no value is supplied).
        static double ParseRangeValue(InputState state, Element element, double min, double max) {
            string raw = state?.Value;
            if (string.IsNullOrEmpty(raw)) raw = element?.GetAttribute("value");
            if (!string.IsNullOrEmpty(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)
                && !double.IsNaN(v) && !double.IsInfinity(v)) {
                return v;
            }
            return (min + max) * 0.5;
        }

        // CSS UI 4 §5.5: `accent-color` tints UA-drawn form-control accents.
        // The initial / explicit `auto` keeps the platform default — here, the
        // hard-coded indigo CheckColor. Authors may set any color value; we
        // delegate parsing to ColorResolver (named, hex, rgb/rgba, hsl, …).
        // Parse failures also fall back to the platform default rather than
        // black so a typo doesn't produce a jarringly-different glyph.
        static LinearColor ResolveAccentColor(Box box) {
            if (box?.Style == null) return CheckColor;
            string raw = box.Style.Get(CssProperties.AccentColorId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return CheckColor;
            var current = ColorResolver.ResolveCurrentColor(box.Style);
            if (ColorResolver.TryResolve(raw, current, box.Style, out var c)) return c;
            return CheckColor;
        }

        static void DrawSelectChrome(Box box, Element element, PaintList output, TextWidthFunc widthOf) {
            // The caret arrow is drawn as a small filled triangle approximated by
            // a thin horizontal rectangle with rounded corners; a real renderer
            // would emit a path. v1: a 6×3 indigo bar 8px from the right edge.
            double margin = 8;
            double w = 6;
            double h = 3;
            var bar = new Rect(box.X + box.Width - margin - w, box.Y + (box.Height - h) * 0.5, w, h);
            output.Add(new FillRectCommand(bar, Brush.SolidColor(BorderColor), BorderRadii.Uniform(1)));
        }

        static double ResolveFontSize(Box box) {
            if (box?.Style == null) return 14.0;
            string raw = box.Style.Get(CssProperties.FontSizeId);
            if (string.IsNullOrEmpty(raw)) return 14.0;
            if (raw.EndsWith("px")) {
                if (double.TryParse(raw.AsSpan(0, raw.Length - 2),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v)) return v;
            }
            return 14.0;
        }
    }
}
