using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Animation {
    public sealed class AnimationInstance {
        public KeyframeAnimation Anim { get; }
        public double DurationSeconds { get; }
        public double DelaySeconds { get; }
        public EasingFunction Easing { get; }
        // Use double.PositiveInfinity to indicate an infinite count.
        public double IterationCount { get; }
        public FillMode FillMode { get; }
        public PlaybackDirection Direction { get; }
        public double StartTimeSeconds { get; }

        // Per-keyframe parsed-value cache. The Keyframe API holds raw
        // strings (Dictionary<string,string>) and we don't want to change
        // that public contract, so the cache lives here on the running
        // instance. Each entry is keyed by the Keyframe object identity
        // (KeyframeAnimation.Keyframes is immutable for the lifetime of
        // the animation, modulo MaterializeImplicitKeyframes producing a
        // fresh clone — see CssAnimationRunner — at which point a fresh
        // AnimationInstance + fresh cache is built).
        //
        // Pre-fix: every Tick called ValueInterpolator.Interpolate which
        // re-parsed BOTH endpoint strings through CssValue.TryParse on
        // every frame. With 4 transform-animated tiles at 60Hz that was
        // ~480 parse-dictionary probes / second just to read endpoints
        // that never change for the entire animation.
        Dictionary<Keyframe, Dictionary<string, CssValue>> parsedCache;

        // Reusable result + scratch buffers. Sample (called every Tick during
        // an active animation, i.e. once per animated element per frame for
        // a transform/opacity animation) used to allocate a fresh
        // Dictionary<string,string> + HashSet<string> each call. The keys
        // are stable across Ticks for any given KeyframeAnimation (they
        // come from the static Keyframe.Properties dictionaries), so the
        // cleared-and-refilled reuse stays correct.
        readonly Dictionary<string, string> sampleResult = new Dictionary<string, string>(4);
        readonly HashSet<string> sampleKeys = new HashSet<string>();

        // Parallel typed-result dict. When a per-property interpolation can
        // emit a CssValue without materialising a string (currently the
        // rotate(<angle>) fast path), the typed result is stashed here and
        // CssAnimationRunner.Compose routes the property through
        // ComputedStyle.SetParsed instead of Set(string). This avoids the
        // per-Tick StringBuilder.ToString() + Format(double) allocations
        // — 64 spinning tiles × ~150 B = ~10 KB/frame on the gem demo.
        readonly Dictionary<string, CssValue> typedSampleResult = new Dictionary<string, CssValue>(2);
        public Dictionary<string, CssValue> TypedSample => typedSampleResult;

        // Per-key CssProperties id cache. Populated lazily by SampleAtPhaseOne
        // when a new key first appears in sampleResult / typedSampleResult.
        // Sample keys come from the @keyframes declaration, which is parsed
        // ONCE per animation — so the set is stable across Ticks and a one-
        // time GetId per key + per-frame cached array lookup beats the
        // CssProperties.idByName hashmap probe Compose did every frame for
        // every animated element × every sampled property (P14 in
        // CODE_AUDIT_FINDINGS.md). Value of -1 means the key is a custom
        // property (`--*`) or otherwise unregistered; Compose falls back to
        // the string-keyed path so unknown names still work.
        readonly Dictionary<string, int> sampleKeyIdCache = new Dictionary<string, int>(4);

        // Returns the pre-resolved property id for a sample key (the keys of
        // CurrentSample / TypedSample), caching the result on first call.
        // Hot path: CssAnimationRunner.Compose calls this once per overlay-
        // property per frame instead of CssProperties.GetId(string).
        public int GetSampleKeyId(string key) {
            if (string.IsNullOrEmpty(key)) return -1;
            if (sampleKeyIdCache.TryGetValue(key, out int id)) return id;
            id = CssProperties.GetId(key);
            sampleKeyIdCache[key] = id;
            return id;
        }

        // Per-anim typed transform overlay (rotate(<angle>) fast path only).
        // Built lazily on first matching Sample; the same CssFunctionCall +
        // inner CssAngle are mutated in place every Tick so the composed
        // style's parsedValues[transformId] slot keeps pointing at the
        // same reference graph and only the angle's Value field updates.
        CssFunctionCall rotateOverlayFn;
        CssAngle rotateOverlayAngle;

        // Per-anim typed opacity overlay. Mutated in place every Tick when
        // both endpoints are bare <number> values (the common @keyframes
        // pulse / star-twinkle case). Bypasses the InterpolateParsed →
        // double.ToString() per-Tick string allocation.
        CssNumber opacityOverlayNumber;

        // Per-anim typed multi-fn transform overlay (translate(...) scale(...),
        // matching-shape lists). Owns a CssValueList of CssFunctionCalls whose
        // CssLength/CssNumber/CssAngle args are Reset() in place every Tick —
        // SetParsed sees the same reference graph and only the inner numeric
        // backing fields move. Used as a fallback when TryInterpolateRotateTyped
        // doesn't match (multi-fn or non-rotate-only shapes).
        ValueInterpolator.TransformTypedOverlay transformOverlay;

        public AnimationInstance(
            KeyframeAnimation anim,
            double durationSeconds,
            double delaySeconds,
            EasingFunction easing,
            double iterationCount,
            FillMode fillMode,
            PlaybackDirection direction,
            double startTimeSeconds) {
            Anim = anim ?? throw new ArgumentNullException(nameof(anim));
            DurationSeconds = durationSeconds;
            DelaySeconds = delaySeconds;
            Easing = easing ?? LinearEasing.Instance;
            IterationCount = iterationCount;
            FillMode = fillMode;
            Direction = direction;
            StartTimeSeconds = startTimeSeconds;
        }

        public Dictionary<string, string> Tick(double now) {
            double activeTime = now - StartTimeSeconds - DelaySeconds;
            double totalDuration = double.IsPositiveInfinity(IterationCount)
                ? double.PositiveInfinity
                : DurationSeconds * IterationCount;

            // Before delay completes (i.e. before any iteration has begun).
            if (activeTime < 0) {
                if (FillMode == FillMode.Backwards || FillMode == FillMode.Both) {
                    // CSS Animations L1 §3.4 / Web Animations L1 — the
                    // backwards-fill "before phase" value must be the first
                    // keyframe **in the playback direction**, mirroring the
                    // forwards-fill branch at line 119. For `reverse` /
                    // `alternate-reverse` that's phase 1, not phase 0;
                    // hardcoding 0 made an animation like
                    // `spin 2s 1s reverse backwards` flash the 0% keyframe
                    // during the 1s delay before snapping to 100%.
                    return SampleAtPhaseOne(ApplyDirection(0, 0));
                }
                return null;
            }

            bool finished = !double.IsPositiveInfinity(IterationCount) && activeTime >= totalDuration;

            // After all iterations complete.
            if (finished) {
                if (FillMode == FillMode.Forwards || FillMode == FillMode.Both) {
                    int totalIters = (int)Math.Floor(IterationCount);
                    bool partial = IterationCount > totalIters;
                    int finalIterIndex = partial ? totalIters : totalIters - 1;
                    double finalProgress = partial ? IterationCount - totalIters : 1;
                    double iterPhase = ApplyDirection(finalIterIndex, finalProgress);
                    return SampleAtPhaseOne(iterPhase);
                }
                return null;
            }

            int currentIter = DurationSeconds > 0 ? (int)Math.Floor(activeTime / DurationSeconds) : 0;
            double localProgress = DurationSeconds > 0
                ? (activeTime - currentIter * DurationSeconds) / DurationSeconds
                : 1;
            if (localProgress > 1) localProgress = 1;
            if (localProgress < 0) localProgress = 0;

            double phase = ApplyDirection(currentIter, localProgress);
            return SampleAtPhaseOne(phase);
        }

        double ApplyDirection(int iterIndex, double localProgress) {
            switch (Direction) {
                case PlaybackDirection.Normal:
                    return localProgress;
                case PlaybackDirection.Reverse:
                    return 1 - localProgress;
                case PlaybackDirection.Alternate:
                    return (iterIndex % 2 == 0) ? localProgress : 1 - localProgress;
                case PlaybackDirection.AlternateReverse:
                    return (iterIndex % 2 == 0) ? 1 - localProgress : localProgress;
                default:
                    return localProgress;
            }
        }

        Dictionary<string, string> SampleAtPhaseOne(double phase) {
            // Locate enclosing keyframes for each property independently.
            // CSS keyframes are sparse: an intermediate keyframe may animate
            // only opacity while transform should keep interpolating between
            // 0% and 100%. Bracketing once globally makes omitted properties
            // snap to whichever side is present (confetti-fall hit this with
            // a 15% opacity-only keyframe).
            //
            // The animation timing function is applied to the interval between
            // the chosen keyframes, not to the whole 0..1 animation before
            // selecting keyframes. Global easing makes sparse keyframes cross
            // their stops at the wrong time, so an opacity-only 15% frame can
            // pull transform/opacity out of sync with Chrome.
            var frames = Anim.Keyframes;

            // Reuse pooled buffers: sampleResult is cleared then refilled.
            // The caller stores rec.CurrentSample = Tick(...) — the SAME
            // dictionary reference is returned every frame, so the prior
            // frame's reference IS this one. CssAnimationRunner.TickInternal
            // reads .Keys on the returned dict and Compose iterates it in
            // the same synchronous tick window, so aliasing is safe.
            sampleResult.Clear();
            typedSampleResult.Clear();
            sampleKeys.Clear();
            for (int i = 0; i < frames.Count; i++) {
                foreach (var k in frames[i].Properties.Keys) sampleKeys.Add(k);
            }

            foreach (var k in sampleKeys) {
                Keyframe lo = null;
                Keyframe hi = null;
                for (int i = 0; i < frames.Count; i++) {
                    var frame = frames[i];
                    if (!frame.Properties.ContainsKey(k)) continue;
                    if (frame.Position <= phase) lo = frame;
                    if (frame.Position >= phase) {
                        hi = frame;
                        break;
                    }
                }
                if (lo == null) lo = hi;
                if (hi == null) hi = lo;
                if (lo == null || hi == null) continue;

                double span = hi.Position - lo.Position;
                double localProgress = span > 0 ? (phase - lo.Position) / span : 0;
                if (localProgress < 0) localProgress = 0;
                if (localProgress > 1) localProgress = 1;
                double localT = Easing.Evaluate(localProgress);

                lo.Properties.TryGetValue(k, out string a);
                hi.Properties.TryGetValue(k, out string b);
                if (a == null && b == null) continue;
                if (a == null) {
                    sampleResult[k] = b;
                    continue;
                }
                if (b == null) {
                    sampleResult[k] = a;
                    continue;
                }
                // Typed fast path: transform with both endpoints `rotate(<angle>)`
                // — the common @keyframes spin case. Updates a pre-built
                // CssFunctionCall + CssAngle in-place and records it in
                // typedSampleResult so Compose can hand a CssValue to
                // ComputedStyle.SetParsed instead of paying for sb.ToString()
                // + Format(double) every Tick. sampleResult is left empty
                // for this key — Compose checks the typed dict first.
                if (k == "transform" && TryInterpolateRotateTyped(a, b, localT)) {
                    typedSampleResult[k] = rotateOverlayFn;
                    // sampleResult intentionally left empty for this key —
                    // production Compose reads typedSampleResult first and
                    // bypasses the string read. Legacy callers needing the
                    // string form should read AnimationInstance.TypedSample
                    // directly and ToString() on demand. Saves the per-Tick
                    // StringBuilder + ToString allocations (~200 B / call
                    // × 60 anims / frame = ~12 KB / frame on the gem grid).
                    continue;
                }
                // General typed-multi-fn transform path. Handles translate/scale/
                // rotate/skew lists with matching signatures and bypasses the
                // per-Tick StringBuilder pass + double.ToString per arg. The
                // overlay holds a stable CssValueList graph; only the inner
                // CssLength/CssNumber/CssAngle backing fields change.
                if (k == "transform") {
                    if (transformOverlay == null) transformOverlay = new ValueInterpolator.TransformTypedOverlay();
                    if (ValueInterpolator.TryUpdateTransformTyped(a, b, localT, transformOverlay)) {
                        typedSampleResult[k] = transformOverlay.List;
                        continue;
                    }
                }
                // Typed fast path: opacity with both endpoints `<number>`.
                // ParseDouble is allocation-free; the pre-built CssNumber is
                // mutated in place. Without this path, every Tick allocated
                // a fresh decimal string via the InterpolateParsed → double.
                // ToString flow.
                if (k == "opacity" && TryInterpolateOpacityTyped(a, b, localT)) {
                    typedSampleResult[k] = opacityOverlayNumber;
                    continue;
                }
                // Delegate to the property-aware interpolator with PRE-PARSED
                // endpoints — every subsequent frame this animation runs
                // hits the cache here instead of CssValue.TryParse.
                var kind = PropertyKindRegistry.Of(k);
                var ap = GetParsed(lo, k, a);
                var bp = GetParsed(hi, k, b);
                sampleResult[k] = ValueInterpolator.InterpolateParsed(ap, bp, a, b, localT, kind, InterpolatorContext);
            }
            return sampleResult;
        }

        // Returns true when `from` and `to` parse as plain CSS <number> values
        // (covers opacity keyframes like `0%,100% { opacity: 1 } 50% { opacity:
        // 0.55 }`) and updates the per-anim opacityOverlayNumber in-place.
        // Pure double arithmetic — no string formatting until something asks
        // ComputedStyle.Get for the raw value (which lazy-mats via ToString).
        bool TryInterpolateOpacityTyped(string from, string to, double t) {
            if (!TryParseBareNumber(from, out double fromN)) return false;
            if (!TryParseBareNumber(to, out double toN)) return false;
            double lerped = fromN + (toN - fromN) * t;
            if (opacityOverlayNumber == null) {
                opacityOverlayNumber = new CssNumber(lerped, null);
            } else {
                opacityOverlayNumber.Reset(lerped, null);
            }
            return true;
        }

        // Trimmed bare-number parser — accepts e.g. "1", "0.55", "  0.7  ".
        // Rejects any value carrying a unit suffix (px, %, deg, ...) so we
        // only take the typed fast path when both endpoints are <number>.
        static bool TryParseBareNumber(string s, out double value) {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int start = 0;
            while (start < s.Length && char.IsWhiteSpace(s[start])) start++;
            int end = s.Length;
            while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
            for (int i = start; i < end; i++) {
                char c = s[i];
                if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9')) continue;
                return false;
            }
            return double.TryParse(
                s.AsSpan(start, end - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        // Returns true when `from` and `to` are both `rotate(<angle>)` (or
        // the implicit-from-none-to-rotate variant) and updates the per-anim
        // rotateOverlayFn / rotateOverlayAngle in-place to the lerped value.
        // Side effect: caller reads rotateOverlayFn as the typed result.
        bool TryInterpolateRotateTyped(string from, string to, double t) {
            // Fast-classify: both must be `rotate(<single-angle>)` or `none`
            // → `rotate(...)`. ParseTransformFunctionsCached is a hashtable
            // lookup on the input strings (warm after first frame).
            double fromDeg, toDeg;
            if (!ValueInterpolator.TryReadSingleRotateDeg(from, out fromDeg)) return false;
            if (!ValueInterpolator.TryReadSingleRotateDeg(to, out toDeg)) return false;
            double lerped = fromDeg + (toDeg - fromDeg) * t;
            if (rotateOverlayAngle == null) {
                rotateOverlayAngle = new CssAngle(lerped, CssAngleUnit.Deg, null);
                var args = new List<CssValue>(1) { rotateOverlayAngle };
                rotateOverlayFn = new CssFunctionCall("rotate", args, null);
            } else {
                rotateOverlayAngle.Reset(lerped, CssAngleUnit.Deg, null);
            }
            return true;
        }

        // Returns the parsed CssValue for keyframe `frame`'s `property`,
        // lazily parsing the raw `text` on first read and caching the
        // result on this instance.
        CssValue GetParsed(Keyframe frame, string property, string text) {
            if (parsedCache == null) parsedCache = new Dictionary<Keyframe, Dictionary<string, CssValue>>();
            if (!parsedCache.TryGetValue(frame, out var perFrame)) {
                perFrame = new Dictionary<string, CssValue>();
                parsedCache[frame] = perFrame;
            }
            if (perFrame.TryGetValue(property, out var cached)) return cached;
            CssValue parsed = null;
            if (!string.IsNullOrEmpty(text)) {
                // Silent parse: animation keyframe text can legitimately carry
                // values the parser doesn't fully type yet (e.g. transform
                // with mixed args). The downstream ValueInterpolator falls
                // back to its raw-string path on null, so a parse failure
                // here isn't an author error — don't flood the console with
                // a warning per animated frame.
                CssValue.TryParseSilent(text, out parsed);
            }
            // Negative results are cached too — failed parses are rare on
            // the animated property surface but a hot path mustn't probe
            // CssValue.TryParse repeatedly when the answer is "no".
            perFrame[property] = parsed;
            return parsed;
        }

        // Optional per-instance length context for resolving relative units
        // (em/rem/%, etc.) inside transform/length interpolation. When null
        // the interpolator falls back to LengthContext.Default which is
        // sufficient for the common px-only case (match3's animations).
        public LengthContext InterpolatorContext { get; set; } = LengthContext.Default;
    }
}
