using System;
using System.Collections.Generic;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Css.Cascade {
    // Version is bumped each time the cascade engine produces a new ComputedStyle
    // for an element. The cache uses (input version tuple) -> ComputedStyle, and the
    // child's cache key embeds the parent's Version so a parent change naturally
    // invalidates descendants.
    //
    // Storage layout (v0.9): registered properties live in `values`, indexed by
    // CssProperties.GetId(name). Custom properties (`--foo`) and unknown keys
    // spill to the lazily-allocated `customProps` dictionary. The string-keyed
    // API path resolves the integer id once via GetId then dispatches; hot
    // callers cache the id and use the int-keyed overloads. Set bumps Version
    // only on actual value change.
    public sealed class ComputedStyle {
        string[] values;
        // Per-slot occupancy bitset complementing `values`. We need to
        // distinguish "set to null" from "never set", and we need an
        // allocation-free way to count populated slots and to iterate them
        // for Enumerate(). Stored as a parallel bool[] sized identically.
        bool[] occupied;
        // PA5: parallel ulong[] mirror of `occupied`, packed 64 bits per word.
        // Maintained in lockstep with `occupied` on every Set / Reset /
        // EnsureCapacity. Read by CascadeEngine.FillInherited via the
        // OccupiedBits accessor so it can iterate
        // `(parent.bits & ~child.bits & CssProperties.GetInheritedMask())`
        // in O(words × popcount-of-difference) instead of scanning every
        // registered id. We keep the bool[] in addition to the bitset because
        // existing fast-path readers (Get(int), Contains(int), Enumerate(),
        // ValueEquals, etc.) all do a single `occupied[i]` load that's faster
        // than a `(bits[i >> 6] >> (i & 63)) & 1` decode — and the per-Set cost
        // of also flipping one bit is negligible compared to those readers.
        ulong[] occupiedBits;
        internal ulong[] OccupiedBits => occupiedBits;
        int registeredSetCount;
        Dictionary<string, string> customProps;

        // Lazy per-property parsed-CssValue cache. GetParsed(propId) returns
        // the typed parse tree for the slot's raw string, building it on
        // first read and reusing it for subsequent reads until Set bumps the
        // slot. Different from the global CssValue.TryParse cache (which
        // memoizes string→parse tree across the WHOLE document): this is
        // per-style + per-property, so steady-state reads are an O(1) array
        // lookup with zero dictionary probes. Stays null until first
        // GetParsed call so styles that only ever consume raw strings don't
        // pay the extra array allocation.
        CssValue[] parsedValues;
        // 0 = not yet parsed (parse on next read); 1 = parsed & cached; 2 =
        // parsed & failed (don't retry until Set re-stamps the slot). Same
        // length as `values` once allocated.
        byte[] parsedState;
        const byte ParsedNotYet = 0;
        const byte ParsedOk = 1;
        const byte ParsedFailed = 2;

        public Element Element { get; private set; }
        public long Version { get; internal set; }

        // Recursive matched-properties cache key. Encodes (parent's
        // MatchedKey, this element's matched declaration set, state digest).
        // Two elements with the same MatchedKey produce identical computed
        // values (modulo element-specific deps like attr() — those callers
        // mark the key with 0 to bypass the cache). Set by CascadeEngine's
        // ComputeFor; read by sibling/descendant lookups to share style
        // results. Zero = "no key" (do not cache).
        public ulong MatchedKey { get; internal set; }

        // Decoration-only version: bumps on every property change EXCEPT
        // the paint-wrapper properties (transform, opacity, filter). Used by
        // PaintBoxCache.IsValid so a transform-only animation tick (which
        // bumps Version every frame) doesn't bust the cached decoration
        // commands — only the wrapper push commands need to re-resolve.
        // See WrapperPropertyIds below for the exact exclusion set.
        public long DecorationVersion { get; internal set; }

        // Sticky flag — true if any wrapper property (transform / opacity /
        // filter / transform-origin) has ever been set on this instance.
        // EmitWrappersFresh reads it as an O(1) early-out so plain boxes
        // skip three resolver calls per frame. Sticky because we don't
        // track which wrapper specifically was set: setting back to the
        // default still leaves the flag true and we re-resolve, which is
        // strictly correct (just no longer free). Reset() clears it.
        public bool HasWrapperProperties { get; internal set; }

        // Same shape as HasWrapperProperties but tracking the decoration-
        // emitting half of the paint pass: any of box-shadow / background-* /
        // border-* / border-image-* / outline-* set to a non-default value.
        // EmitVisibleDecorations short-circuits when false — saves the
        // five resolver calls (shadow, background, border-image, border,
        // outline) on layout-only boxes. On the demo's 269-box tree,
        // ~half the boxes are anonymous wrappers / flex containers with
        // no decoration, all of which can hit this fast path. Cold-path
        // win primarily; cached path is already gated by PaintBoxCache.
        public bool HasDecorationProperties { get; internal set; }

        // True when any resolved property value references viewport units
        // (`vw`, `vh`, `vmin`, `vmax`). Layout uses this as a narrow cache-key
        // dependency: a viewport resize should invalidate these boxes, but it
        // should not poison every fixed-size descendant whose containing block
        // dimensions stayed the same.
        public bool HasViewportRelativeValues { get; internal set; }

        // Tracks property ids whose winning cascade declaration carried
        // !important. Per CSS Cascading L4 §6.4.1, animation declarations
        // sit BELOW !important author declarations in the cascade order,
        // so CssAnimationRunner.Compose must skip overlay on any property
        // whose base value won via !important. Stays null until the first
        // important-write to avoid the alloc on the common all-normal case.
        // Cleared in Reset; CopyFrom propagates from the source style so
        // a composed-style snapshot retains the same overlay restrictions.
        HashSet<int> importantSet;
        public bool IsImportant(int propertyId) =>
            importantSet != null && importantSet.Contains(propertyId);
        internal void MarkImportant(int propertyId, bool important) {
            if (important) {
                if (importantSet == null) importantSet = new HashSet<int>();
                importantSet.Add(propertyId);
            } else if (importantSet != null) {
                importantSet.Remove(propertyId);
            }
        }

        // Property ids whose changes affect the paint WRAPPER stack
        // (filter/transform/opacity push commands) but NOT the decoration
        // commands (shadow/background/border/outline). A Set on one of
        // these bumps Version (so cascade dependents notice) but leaves
        // DecorationVersion intact so PaintBoxCache can take the wrapper-
        // only refresh path. transform-origin is included because it
        // re-resolves with transform without affecting decorations.
        static readonly System.Collections.Generic.HashSet<int> wrapperPropertyIds = BuildWrapperPropertyIds();
        // B16: clip-path is NOT in wrapperPropertyIds (its PushClipPath
        // command is part of the cached DECORATION list, so a clip-path
        // write must keep bumping DecorationVersion). But the GPU path()
        // implementation injects a synthetic coverage-mask layer inside
        // EmitWrappersFresh, so a `clip-path: path(...)` value must ALSO
        // flip HasWrapperProperties — see the Set overloads.
        static readonly int clipPathPropertyId = CssProperties.GetId("clip-path");
        // Initial-value index for the four wrapper properties so the
        // HasWrapperProperties flag is only flipped when the cascade writes
        // an ACTUAL non-default value. Without this, the cascade's
        // FillInherited pass sets `transform: none` / `opacity: 1` etc. on
        // every styled element and the flag would always be true — making
        // the EmitWrappersFresh fast path a no-op on a real document.
        static readonly System.Collections.Generic.Dictionary<int, string> wrapperPropertyInitial = BuildWrapperInitial();
        static System.Collections.Generic.HashSet<int> BuildWrapperPropertyIds() {
            var set = new System.Collections.Generic.HashSet<int>();
            int id;
            if ((id = CssProperties.GetId("transform")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("transform-origin")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("opacity")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("filter")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-image")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-mode")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-repeat")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-position")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-size")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-origin")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-clip")) >= 0) set.Add(id);
            if ((id = CssProperties.GetId("mask-composite")) >= 0) set.Add(id);
            // CSS Compositing 1 §6 — mix-blend-mode is a paint-only wrapper:
            // it affects how this element composites against its backdrop but
            // never touches layout / decoration. Plumbed through the paint
            // pipeline as Push/PopMixBlendMode commands (B3b).
            if ((id = CssProperties.GetId("mix-blend-mode")) >= 0) set.Add(id);
            return set;
        }
        static System.Collections.Generic.Dictionary<int, string> BuildWrapperInitial() {
            var d = new System.Collections.Generic.Dictionary<int, string>(4);
            void Add(string name) {
                int id = CssProperties.GetId(name);
                if (id >= 0) d[id] = CssProperties.InitialValueOf(name);
            }
            Add("transform");
            Add("transform-origin");
            Add("opacity");
            Add("filter");
            Add("mask");
            Add("mask-image");
            Add("mask-mode");
            Add("mask-repeat");
            Add("mask-position");
            Add("mask-size");
            Add("mask-origin");
            Add("mask-clip");
            Add("mask-composite");
            Add("mix-blend-mode");
            return d;
        }
        // True iff a Set/SetParsed on this wrapper property would change
        // the box's paint stack — i.e. the value differs from initial.
        static bool IsNonDefaultWrapperValue(int propertyId, string value) {
            if (!wrapperPropertyInitial.TryGetValue(propertyId, out var initial)) return false;
            return !string.Equals(value, initial);
        }

        // Decoration-property registry. Mirrors wrapperPropertyInitial but
        // for the EmitVisibleDecorations fast path. Includes every
        // longhand / shorthand the converter consults inside that block.
        // border-radius is included so a box with rounded corners + a
        // visible background actually emits — the radii drive the rounded
        // SDF rendering.
        static readonly System.Collections.Generic.Dictionary<int, string> decorationPropertyInitial = BuildDecorationInitial();
        static System.Collections.Generic.Dictionary<int, string> BuildDecorationInitial() {
            var d = new System.Collections.Generic.Dictionary<int, string>(48);
            void Add(string name) {
                int id = CssProperties.GetId(name);
                if (id >= 0) d[id] = CssProperties.InitialValueOf(name);
            }
            // Box-shadow
            Add("box-shadow");
            // Backdrop / clip-path affect painting even when the element has
            // no ordinary background or border.
            Add("backdrop-filter");
            Add("clip-path");
            // Background longhands + shorthand
            Add("background");
            Add("background-color");
            Add("background-image");
            Add("background-position");
            Add("background-size");
            Add("background-repeat");
            Add("background-attachment");
            Add("background-origin");
            Add("background-clip");
            // Border longhands + shorthand
            Add("border");
            Add("border-top"); Add("border-right"); Add("border-bottom"); Add("border-left");
            Add("border-width"); Add("border-color"); Add("border-style");
            Add("border-top-width"); Add("border-right-width"); Add("border-bottom-width"); Add("border-left-width");
            Add("border-top-color"); Add("border-right-color"); Add("border-bottom-color"); Add("border-left-color");
            Add("border-top-style"); Add("border-right-style"); Add("border-bottom-style"); Add("border-left-style");
            // Border-radius (drives the SDF corners that any rendered fill samples)
            Add("border-radius");
            Add("border-top-left-radius"); Add("border-top-right-radius");
            Add("border-bottom-left-radius"); Add("border-bottom-right-radius");
            // Border-image
            Add("border-image");
            Add("border-image-source"); Add("border-image-slice");
            Add("border-image-width"); Add("border-image-outset"); Add("border-image-repeat");
            // Outline
            Add("outline");
            Add("outline-color"); Add("outline-style"); Add("outline-width"); Add("outline-offset");
            // Column rule (CSS Multicol §4 — paints a vertical line in each column gap)
            Add("column-rule");
            Add("column-rule-width"); Add("column-rule-style"); Add("column-rule-color");
            return d;
        }
        static bool IsNonDefaultDecorationValue(int propertyId, string value) {
            if (!decorationPropertyInitial.TryGetValue(propertyId, out var initial)) return false;
            return !string.Equals(value, initial);
        }
        // Test / cascade hook so external callers can ask "would changing
        // this property require a decoration rebuild?" without depending
        // on the private set above.
        public static bool IsWrapperOnlyProperty(int propertyId) => wrapperPropertyIds.Contains(propertyId);

        // Rebinds this instance to a new Element and resets its property
        // bag. Used by the cascade's ComputedStyle pool — the values[] +
        // occupied[] arrays stay allocated so a fresh compute pass fills
        // them in without paying the ~1.7 KB \`new ComputedStyle\` alloc.
        // Only the engine itself calls this on a style it just removed
        // from its cache (no external references); the public Element
        // setter stays private.
        internal void RebindForReuse(Element element) {
            Element = element;
            Reset();
        }

        public ComputedStyle(Element element) : this(element, 0) { }

        // capacityHint is retained for ABI compatibility with prior callers
        // (CascadePools, tests). It no longer pre-sizes a dictionary; the
        // values array is sized off CssProperties.RegisteredCount and the
        // custom-properties dictionary is allocated lazily.
        public ComputedStyle(Element element, int capacityHint) {
            Element = element;
            Version = ComputedStyleVersion.Next();
            DecorationVersion = Version;
            int size = CssProperties.RegisteredCount;
            if (size <= 0) size = 1;
            values = new string[size];
            occupied = new bool[size];
            occupiedBits = new ulong[(size + 63) >> 6];
        }

        public string Get(string property) {
            if (property == null) return null;
            int id = CssProperties.GetId(property);
            if (id >= 0) {
                // Route through Get(int): it carries the lazy-materialisation
                // fallback for SetParsed-only slots (values[id] is null but
                // parsedValues[id] holds a typed value whose .ToString() is
                // the raw form). Without this branch, animations that emit
                // CssValue via the typed flow (rotate-overlay / opacity-
                // overlay / transform-overlay) make Get(string) return null
                // even though Get(int) returns the right string. Direct
                // callers (tests, BackgroundResolver string path, etc.) hit
                // this overload.
                return Get(id);
            }
            if (customProps != null && customProps.TryGetValue(property, out var v)) return v;
            return null;
        }

        public bool TryGet(string property, out string value) {
            if (property == null) { value = null; return false; }
            int id = CssProperties.GetId(property);
            if (id >= 0) {
                if (id >= values.Length || !occupied[id]) { value = null; return false; }
                value = values[id];
                return true;
            }
            if (customProps != null && customProps.TryGetValue(property, out value)) return true;
            value = null;
            return false;
        }

        // Hot-path id-keyed read. Caller is responsible for passing a valid
        // CssProperties.*Id constant; out-of-range ids return null defensively
        // so the caller can treat them as unset without an extra check.
        public string Get(int propertyId) {
            if ((uint)propertyId >= (uint)values.Length) return null;
            if (!occupied[propertyId]) return null;
            string raw = values[propertyId];
            if (raw != null) return raw;
            if (parsedValues != null && propertyId < parsedValues.Length
                && parsedState[propertyId] == ParsedOk) {
                var v = parsedValues[propertyId];
                if (v != null) {
                    raw = v.Raw ?? v.ToString();
                    values[propertyId] = raw;
                    return raw;
                }
            }
            return null;
        }

        public bool TryGet(int propertyId, out string value) {
            if ((uint)propertyId >= (uint)values.Length || !occupied[propertyId]) {
                value = null;
                return false;
            }
            value = Get(propertyId);
            return value != null;
        }

        /// <summary>
        /// Write the raw string value for <paramref name="property"/>.
        /// A null <paramref name="property"/> name is a silent no-op (the
        /// cascade routinely probes optional property names).
        ///
        /// <para>NG6: a null <paramref name="value"/> is the documented
        /// "clear" form — it is stored verbatim in the slot, marks the
        /// property as occupied, and propagates as null through
        /// <see cref="Get(int)"/> / <see cref="TryGet(int, out string)"/>
        /// (which reports occupied=true with value=null). Downstream
        /// readers (stub-property scan, viewport-unit scan, wrapper /
        /// decoration default-comparisons) all tolerate null. Callers
        /// who want to revert a property to its initial value should set
        /// the initial-value string explicitly, not null.</para>
        /// </summary>
        public void Set(string property, string value) {
            if (property == null) return;
            int id = CssProperties.GetId(property);
            if (id >= 0) {
                EnsureCapacity(id);
                if (occupied[id] && string.Equals(values[id], value)) return;
                if (!occupied[id]) {
                    occupied[id] = true;
                    occupiedBits[id >> 6] |= 1UL << (id & 63);
                    registeredSetCount++;
                }
                values[id] = value;
                // Invalidate parsed cache for this slot — see Set(int, string).
                if (parsedState != null && id < parsedState.Length) {
                    parsedState[id] = ParsedNotYet;
                    if (parsedValues != null && id < parsedValues.Length) {
                        parsedValues[id] = null;
                    }
                }
                // Only warn when an author actually USES a stub property.
                // FillInherited / cascade fills initial values for every
                // registered property on every recompute, which would
                // otherwise spam the console with a one-shot warning per
                // stub property on the first frame. If the value being set
                // equals the initial value, the cascade is just rebuilding
                // defaults — silent. If it differs, an author wrote a real
                // declaration like `clip-path: inset(10px)` and we want them
                // to know the rendering is a no-op.
                if (CssProperties.IsStubProperty(property)
                    && !string.Equals(value, CssProperties.InitialValueOf(property))) {
                    Weva.Diagnostics.UICssDiagnostics.Warn(
                        "CssProperties",
                        property + " is not implemented");
                }
                Version = ComputedStyleVersion.Next();
                if (wrapperPropertyIds.Contains(id)) {
                    if (IsNonDefaultWrapperValue(id, value)) HasWrapperProperties = true;
                } else {
                    DecorationVersion = Version;
                    if (IsNonDefaultDecorationValue(id, value)) HasDecorationProperties = true;
                    // B16: clip-path: path(...) / shape(...) is a DECORATION
                    // property (the PushClipPath command lives in the cached
                    // decoration list, so the DecorationVersion bump above must
                    // stay), but its GPU implementation also needs the WRAPPER
                    // pass: EmitWrappersFresh injects the synthetic coverage-
                    // mask layer there, and its !HasWrapperProperties fast path
                    // would otherwise skip the injection entirely for a box
                    // styled with only background + clip-path. Both path() and
                    // shape() (CSS Shapes 2) resolve to the coverage-mask path.
                    if (id == clipPathPropertyId && value != null
                            && (value.Contains("path(") || value.Contains("shape("))) {
                        HasWrapperProperties = true;
                    }
                }
                // PI1: viewport-unit scan is a per-char string sweep. The
                // flag is sticky for the lifetime of this style (cleared
                // only by Reset() / pool recycle), so once set we can skip
                // the scan on every subsequent Set on this instance.
                if (!HasViewportRelativeValues && ContainsViewportUnit(value)) HasViewportRelativeValues = true;
                return;
            }
            // Custom or unknown property — spill to side dictionary.
            // Custom properties (`--*`) are first-class CSS Variables and the
            // spill is intentional; only warn on truly unknown bare names so
            // authors notice typos like `colour: red` without being spammed
            // by every legitimate `--brand-color` declaration.
            if (!CssProperties.IsCustomProperty(property)) {
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "CssProperties",
                    "unknown property '" + property + "' skipped");
            }
            if (customProps == null) customProps = new Dictionary<string, string>();
            if (customProps.TryGetValue(property, out var existing) && string.Equals(existing, value)) return;
            customProps[property] = value;
            Version = ComputedStyleVersion.Next();
            DecorationVersion = Version;
            // PI1: see note above — sticky flag, skip rescan once set.
            if (!HasViewportRelativeValues && ContainsViewportUnit(value)) HasViewportRelativeValues = true;
        }

        /// <summary>
        /// Id-keyed twin of <see cref="Set(string, string)"/>. Negative
        /// <paramref name="propertyId"/> is a silent no-op (mirrors the
        /// string overload's tolerance for unknown property names).
        ///
        /// <para>NG6: a null <paramref name="value"/> is stored as-is —
        /// see the string-overload doc for the "clear" semantics.</para>
        /// </summary>
        public void Set(int propertyId, string value) {
            if (propertyId < 0) return;
            EnsureCapacity(propertyId);
            if (occupied[propertyId] && string.Equals(values[propertyId], value)) return;
            if (!occupied[propertyId]) {
                occupied[propertyId] = true;
                occupiedBits[propertyId >> 6] |= 1UL << (propertyId & 63);
                registeredSetCount++;
            }
            values[propertyId] = value;
            // Invalidate the parsed-value cache for this slot so the next
            // GetParsed re-parses against the new string.
            if (parsedState != null && propertyId < parsedState.Length) {
                parsedState[propertyId] = ParsedNotYet;
                if (parsedValues != null && propertyId < parsedValues.Length) {
                    parsedValues[propertyId] = null;
                }
            }
            Version = ComputedStyleVersion.Next();
            if (wrapperPropertyIds.Contains(propertyId)) {
                if (IsNonDefaultWrapperValue(propertyId, value)) HasWrapperProperties = true;
            } else {
                DecorationVersion = Version;
                if (IsNonDefaultDecorationValue(propertyId, value)) HasDecorationProperties = true;
                // B16: see Set(string) — clip-path: path(...) / shape(...)
                // stays a decoration property but must also enable the wrapper
                // pass for the synthetic coverage-mask injection.
                if (propertyId == clipPathPropertyId && value != null
                        && (value.Contains("path(") || value.Contains("shape("))) {
                    HasWrapperProperties = true;
                }
            }
            // PI1: see Set(string) — sticky flag, skip the per-char rescan
            // once set. Cleared only by Reset() / pool recycle.
            if (!HasViewportRelativeValues && ContainsViewportUnit(value)) HasViewportRelativeValues = true;
        }

        // Typed-value setter: writes a CssValue directly into the per-style
        // parsed cache without materialising a raw string. Used by the
        // animation Compose path so per-frame transform updates don't pay
        // for sb.ToString() + Format(double) + Set(string-equality check).
        // The raw-string slot is cleared (lazy-materialised on the next
        // string-keyed Get if a non-typed reader asks).
        //
        // NG6: a null `value` clears the slot — parsedState flips to
        // ParsedFailed, parsedValues[id] is left null, the raw-string
        // slot is also nulled, and downstream readers see the property
        // as occupied but unresolved. The version bump still fires so
        // dependent caches invalidate.
        //
        // Version bumps every call — callers who hand the SAME CssValue
        // reference each frame still need downstream PaintBoxCache /
        // composed-cache invalidation to know paint must rerun. Use the
        // explicit overload below to skip the bump when you know nothing
        // changed.
        public void SetParsed(int propertyId, CssValue value) {
            SetParsed(propertyId, value, bumpVersion: true);
        }

        public void SetParsed(int propertyId, CssValue value, bool bumpVersion) {
            if (propertyId < 0) return;
            EnsureCapacity(propertyId);
            EnsureParsedCapacity();
            // PI2: short-circuit when the slot already holds the same typed
            // value. Animation engines re-emit the same CssValue reference
            // each frame for paused / steady-state transitions; without this
            // guard the Version + DecorationVersion bumps below would cause
            // downstream paint-cache invalidations on every Tick despite no
            // value change. We use reference equality only — CssValue and
            // its subclasses do not override Equals, so a hypothetical
            // structural-equality fallback would degenerate into the same
            // ReferenceEquals check. Slot must already be occupied (a fresh
            // SetParsed(null) on an unoccupied slot still needs to flip the
            // occupied bit + bump registeredSetCount).
            if (occupied[propertyId]
                    && parsedState[propertyId] != ParsedNotYet
                    && ReferenceEquals(parsedValues[propertyId], value)) {
                return;
            }
            if (!occupied[propertyId]) {
                occupied[propertyId] = true;
                occupiedBits[propertyId >> 6] |= 1UL << (propertyId & 63);
                registeredSetCount++;
            }
            // Clear the raw — Get(int) will lazy-materialise from the typed
            // value on demand (see Get below). This avoids the per-Tick
            // string allocation that the legacy Set(string) path required.
            values[propertyId] = null;
            parsedValues[propertyId] = value;
            parsedState[propertyId] = value != null ? ParsedOk : ParsedFailed;
            if (bumpVersion) {
                Version = ComputedStyleVersion.Next();
                if (wrapperPropertyIds.Contains(propertyId)) HasWrapperProperties = true;
                else {
                    DecorationVersion = Version;
                    // SetParsed receives a typed CssValue from the animator —
                    // a non-null value on a decoration property is by
                    // definition non-default, so flip the flag (matches the
                    // wrapper branch above).
                    if (value != null && decorationPropertyInitial.ContainsKey(propertyId)) HasDecorationProperties = true;
                }
                // PI1: see Set(string) — sticky flag, skip rescan once set.
                if (!HasViewportRelativeValues && ContainsViewportUnit(value)) HasViewportRelativeValues = true;
            } else if (wrapperPropertyIds.Contains(propertyId)) {
                // Even without a version bump, record that this style now
                // carries a wrapper value — otherwise the converter's fast
                // path would skip resolving it.
                HasWrapperProperties = true;
                // PI1: sticky flag short-circuit.
                if (!HasViewportRelativeValues && ContainsViewportUnit(value)) HasViewportRelativeValues = true;
            }
        }

        static bool ContainsViewportUnit(CssValue value) {
            if (value == null) return false;
            if (value is CssLength l) {
                return IsViewportUnit(l.Unit);
            }
            if (value is CssCalc calc) return ContainsViewportUnit(calc.Expression);
            if (value is CssValueList list) {
                var items = list.Items;
                for (int i = 0; i < items.Count; i++) {
                    if (ContainsViewportUnit(items[i])) return true;
                }
                return false;
            }
            if (value is CssFunctionCall fn) {
                var args = fn.Arguments;
                for (int i = 0; i < args.Count; i++) {
                    if (ContainsViewportUnit(args[i])) return true;
                }
                return ContainsViewportUnit(value.Raw);
            }
            return ContainsViewportUnit(value.Raw);
        }

        static bool IsViewportUnit(CssLengthUnit unit) {
            return unit == CssLengthUnit.Vw
                || unit == CssLengthUnit.Vh
                || unit == CssLengthUnit.Vmin
                || unit == CssLengthUnit.Vmax;
        }

        static bool ContainsViewportUnit(CalcNode node) {
            switch (node) {
                case CalcLengthNode l:
                    return l.Length != null && IsViewportUnit(l.Length.Unit);
                case CalcBinaryNode b:
                    return ContainsViewportUnit(b.Left) || ContainsViewportUnit(b.Right);
                case CalcMathNode m: {
                    var args = m.Args;
                    for (int i = 0; i < args.Count; i++) {
                        if (ContainsViewportUnit(args[i])) return true;
                    }
                    return false;
                }
                case CalcVariableNode v:
                    return ContainsViewportUnit(v.Variable);
                default:
                    return false;
            }
        }

        static bool ContainsViewportUnit(string value) {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (c != 'v' && c != 'V') continue;
                int remaining = value.Length - i;
                if (remaining >= 2 && IsIdentUnitBoundary(value, i, 2)
                    && (UnitEquals(value, i, "vw") || UnitEquals(value, i, "vh"))) {
                    return true;
                }
                if (remaining >= 4 && IsIdentUnitBoundary(value, i, 4)
                    && (UnitEquals(value, i, "vmin") || UnitEquals(value, i, "vmax"))) {
                    return true;
                }
            }
            return false;
        }

        static bool UnitEquals(string value, int start, string unit) {
            if (start + unit.Length > value.Length) return false;
            for (int i = 0; i < unit.Length; i++) {
                char a = value[start + i];
                char b = unit[i];
                if (a == b) continue;
                if (char.ToLowerInvariant(a) != b) return false;
            }
            return true;
        }

        static bool IsIdentUnitBoundary(string value, int start, int len) {
            int before = start - 1;
            int after = start + len;
            bool beforeOk = before < 0
                || !IsCssIdentChar(value[before])
                || char.IsDigit(value[before])
                || value[before] == '.';
            bool afterOk = after >= value.Length || !IsCssIdentChar(value[after]);
            return beforeOk && afterOk;
        }

        static bool IsCssIdentChar(char c) {
            return char.IsLetterOrDigit(c) || c == '-' || c == '_';
        }

        // Returns the parsed CssValue for `propertyId`, lazily building it on
        // first read and caching it for subsequent reads. Returns null when
        // the slot is unset, empty, or its raw string failed to parse.
        // Result is cached at the slot level (per ComputedStyle), so the
        // hottest cascade-driven readers (TextRunResolver, BackgroundResolver,
        // ColorResolver, …) get an O(1) array lookup instead of a dictionary
        // probe against the global CssValue.TryParse cache.
        // Diagnostic counters for the per-style cache. Aggregate hit/miss
        // count across all live ComputedStyle instances on this thread so
        // a profiler can read steady-state cache efficacy without per-call
        // instrumentation. Reset via ClearCacheCounters().
        public static long ParsedCacheHits;
        public static long ParsedCacheMisses;
        public static long ParsedCacheFailedHits;

        public static void ClearCacheCounters() {
            ParsedCacheHits = 0;
            ParsedCacheMisses = 0;
            ParsedCacheFailedHits = 0;
        }

        public CssValue GetParsed(int propertyId) {
            if ((uint)propertyId >= (uint)values.Length) return null;
            if (!occupied[propertyId]) return null;
            EnsureParsedCapacity();
            byte state = parsedState[propertyId];
            if (state == ParsedOk) {
                ParsedCacheHits++;
                return parsedValues[propertyId];
            }
            if (state == ParsedFailed) {
                ParsedCacheFailedHits++;
                return null;
            }
            ParsedCacheMisses++;
            string raw = values[propertyId];
            if (string.IsNullOrEmpty(raw)) {
                parsedState[propertyId] = ParsedFailed;
                return null;
            }
            // TryParseSilent: the per-style cache is filled opportunistically
            // by hot-path readers that already have downstream raw-string
            // fallbacks for parser-unsupported value shapes (e.g.
            // TransformResolver's rotate(<angle>) path, FilterResolver's
            // hue-rotate). Routing through the warning-emitting TryParse caused
            // 60Hz warning spam on animated transforms because each frame's
            // angle ("337.94295deg", "339.381444deg") was a new string the
            // failedCache hadn't memoized yet.
            if (CssValue.TryParseSilent(raw, out var parsed) && parsed != null) {
                // CssLength / CssNumber / CssPercentage instances may be POOL-
                // OWNED (CssValuePool rents and resets them between layout
                // passes). The per-style cache outlives every pool scope, so a
                // pooled ref stored here would be mutated to a wrong value on
                // the next pass. StableCopy walks the parse tree and clones
                // any pool-mutable leaf into a fresh non-pooled instance so the
                // cached reference stays stable for the ComputedStyle's
                // lifetime. CssKeyword / CssIdentifier / CssColor / CssUrl /
                // CssString are immutable; CssCalc / CssFunctionCall /
                // CssValueList are containers whose children we recurse into.
                var stable = CssValueStableCopy.Of(parsed);
                parsedValues[propertyId] = stable;
                parsedState[propertyId] = ParsedOk;
                return stable;
            }
            parsedState[propertyId] = ParsedFailed;
            return null;
        }

        void EnsureParsedCapacity() {
            int n = values.Length;
            if (parsedValues == null || parsedValues.Length < n) {
                parsedValues = new CssValue[n];
                parsedState = new byte[n];
            }
        }

        public bool Contains(string property) {
            if (property == null) return false;
            int id = CssProperties.GetId(property);
            if (id >= 0) return id < values.Length && occupied[id];
            return customProps != null && customProps.ContainsKey(property);
        }

        public bool Contains(int propertyId) {
            return (uint)propertyId < (uint)values.Length && occupied[propertyId];
        }

        public IEnumerable<KeyValuePair<string, string>> Enumerate() {
            for (int i = 0; i < values.Length; i++) {
                if (!occupied[i]) continue;
                string name = CssProperties.GetName(i);
                if (name == null) continue;
                yield return new KeyValuePair<string, string>(name, values[i]);
            }
            if (customProps != null) {
                foreach (var kv in customProps) yield return kv;
            }
        }

        // PA: returns the underlying customs dict for direct foreach without
        // the yield-iterator allocation that Enumerate() pays. Returns null
        // when no custom property has been written — callers must null-check.
        // The cascade engine's custom-property fan-in loops use this; do not
        // mutate the returned dictionary from outside ComputedStyle.
        internal Dictionary<string, string> CustomPropertiesOrNull => customProps;

        public int Count => registeredSetCount + (customProps != null ? customProps.Count : 0);

        // Wipes every property entry but preserves the backing array so a
        // subsequent fill avoids reallocation. Used by the pooling path on
        // rent — the freshly-rented style is empty, ready to accept a fresh
        // cascade pass without paying for a fresh allocation.
        public void Reset() {
            if (registeredSetCount > 0) {
                Array.Clear(values, 0, values.Length);
                Array.Clear(occupied, 0, occupied.Length);
                // PA5: mirror bitset shares lifetime with occupied[].
                if (occupiedBits != null) Array.Clear(occupiedBits, 0, occupiedBits.Length);
                registeredSetCount = 0;
            }
            customProps?.Clear();
            // Wipe parsed cache so a freshly-rented style doesn't return
            // stale parsed values from its previous owner's slots.
            if (parsedValues != null) Array.Clear(parsedValues, 0, parsedValues.Length);
            if (parsedState != null) Array.Clear(parsedState, 0, parsedState.Length);
            Version = ComputedStyleVersion.Next();
            DecorationVersion = Version;
            HasWrapperProperties = false;
            HasDecorationProperties = false;
            HasViewportRelativeValues = false;
            importantSet?.Clear();
        }

        internal void FillInitials(int count) {
            EnsureCapacity(count - 1);
            for (int id = 0; id < count; id++) {
                if (occupied[id]) continue;
                string iv = CssProperties.GetInitialValue(id);
                if (iv == null) continue;
                values[id] = iv;
                occupied[id] = true;
                occupiedBits[id >> 6] |= 1UL << (id & 63);
                registeredSetCount++;
            }
        }

        public void CopyFrom(ComputedStyle other) {
            if (other == null) return;
            // PA9: skip Reset's Array.Clear over values / occupied / occupied
            // Bits — the Array.Copy below overwrites every populated slot
            // from the source. Only the side-dictionary state (customProps,
            // parsedValues, parsedState, importantSet) and version stamps
            // genuinely need wiping; those are handled inline. The trailing
            // slots beyond other.values.Length are cleared explicitly after
            // the copy if our buffer is wider, so we don't leak stale slots
            // from a previous owner with more properties registered.
            //
            // On the realistic-cold benchmark (~1500 cascade elements where
            // most hit the matched-properties cache and CopyFrom), eliding
            // the per-CopyFrom Array.Clear pass saves ~100-150µs.
            registeredSetCount = 0;
            customProps?.Clear();
            if (parsedValues != null) Array.Clear(parsedValues, 0, parsedValues.Length);
            if (parsedState != null) Array.Clear(parsedState, 0, parsedState.Length);
            importantSet?.Clear();
            HasWrapperProperties = false;
            HasDecorationProperties = false;
            HasViewportRelativeValues = false;

            int srcLen = other.values.Length;
            EnsureCapacity(srcLen - 1);
            // Wholesale copy of the registered-property arrays. Cheaper than
            // iterating set entries because we already paid for the contiguous
            // allocation; a memcpy of N booleans + N references beats a
            // Dictionary<string,string> deep-copy for any non-trivial fill.
            Array.Copy(other.values, values, srcLen);
            Array.Copy(other.occupied, occupied, srcLen);
            // Clear any trailing slots: this style's buffer may be wider
            // than the source's (we grew earlier for a different element
            // with more properties registered).
            if (values.Length > srcLen) {
                Array.Clear(values, srcLen, values.Length - srcLen);
                Array.Clear(occupied, srcLen, occupied.Length - srcLen);
            }
            // PA5: copy bitset mirror in lockstep. EnsureCapacity(other.values.Length-1)
            // above guarantees occupiedBits is at least as wide as the source's
            // (both grow off `values.Length`).
            if (other.occupiedBits != null) {
                int srcWords = other.occupiedBits.Length;
                if (occupiedBits == null || occupiedBits.Length < srcWords) {
                    occupiedBits = new ulong[srcWords];
                }
                Array.Copy(other.occupiedBits, occupiedBits, srcWords);
                // If our bitset is wider than the source's (we grew earlier for
                // a different reason), zero the trailing words so we don't keep
                // stale bits from the previous owner of this slot.
                if (occupiedBits.Length > srcWords) {
                    Array.Clear(occupiedBits, srcWords, occupiedBits.Length - srcWords);
                }
            } else if (occupiedBits != null) {
                Array.Clear(occupiedBits, 0, occupiedBits.Length);
            }
            registeredSetCount = other.registeredSetCount;
            if (other.customProps != null && other.customProps.Count > 0) {
                if (customProps == null) customProps = new Dictionary<string, string>(other.customProps.Count);
                foreach (var kv in other.customProps) customProps[kv.Key] = kv.Value;
            }
            Version = ComputedStyleVersion.Next();
            // Inherit the source style's DecorationVersion so a composed
            // style produced by Compose() (CopyFrom base + SetParsed wrapper
            // props) preserves the base's decoration identity. PaintBoxCache
            // compares against this — propagating it means transform-only
            // animations don't bust the decoration cache.
            DecorationVersion = other.DecorationVersion;
            HasWrapperProperties = other.HasWrapperProperties;
            HasDecorationProperties = other.HasDecorationProperties;
            HasViewportRelativeValues = other.HasViewportRelativeValues;
            // Copy !important markers so the cloned style retains the
            // same cascade-axis identity. Compose() relies on this when
            // it overlays animation values onto a CopyFrom'd base.
            if (other.importantSet != null && other.importantSet.Count > 0) {
                if (importantSet == null) importantSet = new HashSet<int>(other.importantSet.Count);
                else importantSet.Clear();
                foreach (var id in other.importantSet) importantSet.Add(id);
            } else {
                importantSet?.Clear();
            }
        }

        // Structural equality on the property bag: same keys, same string
        // values. Order-insensitive. Used to short-circuit cache writes when
        // a recompute produced an unchanged style (the v1 cascade still
        // creates a new instance per miss; the diff lets future paths skip
        // downstream notifications when nothing materially changed).
        public bool ValueEquals(ComputedStyle other) {
            if (ReferenceEquals(this, other)) return true;
            if (other == null) return false;
            if (Count != other.Count) return false;
            int n = Math.Max(values.Length, other.values.Length);
            for (int i = 0; i < n; i++) {
                bool a = i < values.Length && occupied[i];
                bool b = i < other.values.Length && other.occupied[i];
                if (a != b) return false;
                if (a && !string.Equals(values[i], other.values[i])) return false;
            }
            int aCustom = customProps != null ? customProps.Count : 0;
            int bCustom = other.customProps != null ? other.customProps.Count : 0;
            if (aCustom != bCustom) return false;
            if (aCustom == 0) return true;
            foreach (var kv in customProps) {
                if (!other.customProps.TryGetValue(kv.Key, out var ov)) return false;
                if (!string.Equals(kv.Value, ov)) return false;
            }
            return true;
        }

        // Grow the values+occupied arrays to accommodate a newly-registered
        // property id. Called from Set when a property whose id is past the
        // current capacity is written — late-registered properties (e.g. via
        // CssProperties.Register from a feature module) trigger this once.
        void EnsureCapacity(int requiredId) {
            if (requiredId < values.Length) return;
            int newSize = values.Length;
            if (newSize <= 0) newSize = 4;
            while (newSize <= requiredId) newSize *= 2;
            var grownValues = new string[newSize];
            var grownOccupied = new bool[newSize];
            Array.Copy(values, grownValues, values.Length);
            Array.Copy(occupied, grownOccupied, occupied.Length);
            values = grownValues;
            occupied = grownOccupied;
            // PA5: grow the bitset mirror in lockstep.
            int wantWords = (newSize + 63) >> 6;
            if (occupiedBits == null || occupiedBits.Length < wantWords) {
                var grownBits = new ulong[wantWords];
                if (occupiedBits != null) Array.Copy(occupiedBits, grownBits, occupiedBits.Length);
                occupiedBits = grownBits;
            }
        }
    }
}
