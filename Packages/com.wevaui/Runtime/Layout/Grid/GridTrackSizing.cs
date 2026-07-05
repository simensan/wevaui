using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.Grid {
    // CSS Grid track sizing (spec §11), simplified for v1.
    //
    // Two-pass algorithm:
    //   Phase 1 — Resolve intrinsic sizes:
    //     For each track, compute base size from minimum size function.
    //     For each item spanning intrinsic tracks, contribute its min/max-content
    //     size to the spanned tracks (split evenly across spanned non-flexible
    //     tracks). v1 approximation: min-content = 0, max-content = item's
    //     pre-grid Width/Height (the BlockLayout result).
    //   Phase 2 — Distribute remaining free space to fr tracks:
    //     remaining = container size − (sum of fixed/intrinsic-resolved tracks)
    //                 − (track gaps)
    //     fr tracks receive shares proportional to fr value.
    //
    // auto-fill / auto-fit:
    //   When a repeat(auto-fill | auto-fit, ...) is in the template, expand the
    //   pattern to fill the available container size. auto-fit then collapses
    //   tracks that have no items to zero size (and removes their adjacent gaps).
    internal static class GridTrackSizing {
        public sealed class SizingItem {
            public BlockBox Box;
            public GridArea Area;
            public double IntrinsicMain;
            public double IntrinsicCross;
            // CSS Grid L1 s6.6 automatic minimum: the item's MIN-content
            // inline contribution. Floors bare-fr track bases in DEFINITE
            // containers (s7.2.4: <flex> = minmax(auto, <flex>)) — without
            // it a shrinking window squashed glass.html's player card to
            // its fr share while Chrome held ~min-content. Deliberately
            // separate from IntrinsicMain (max-content): flooring at max
            // would re-introduce the auto-1fr-auto 100vh overflow the
            // definite-container rule in Phase 1b exists to prevent.
            public double IntrinsicMainMin;
        }

        public sealed class SizedTracks {
            public double[] Sizes;
            public double[] Positions;
            // For auto-fit: flags for tracks that should collapse (zero size).
            public bool[] Collapsed;
        }

        public static (GridTrackSize[] tracks, IReadOnlyList<IReadOnlyList<string>> lineNames) MaterializeAutoRepeat(
            GridTemplate template, double containerSize, double gap) {
            if (template == null || template.Tracks == null || !(template.IsAutoFill || template.IsAutoFit)) {
                return (ToArray(template?.Tracks), template?.LineNames);
            }
            // Compute fixed size of one auto-repeat pattern repetition.
            // CSS Grid L1 §7.2.2.3: the repetition count is computed from the
            // available inline size divided by the track size (plus gaps). A
            // `<percentage>` track must resolve against the container's inline
            // size at THIS step — otherwise `repeat(auto-fill, 25%)` would
            // treat the track as 0 and `per` would collapse to a pure-gap
            // value, yielding reps=1 regardless of container width. When the
            // container is indefinite (containerSize <= 0) percentages still
            // resolve to 0 and the auto-fill fallback below (`reps = 1`)
            // applies — that matches the spec's indefinite-track-list rule.
            var pattern = template.AutoRepeatPattern;
            var patternLines = template.AutoRepeatLineNames;
            double patternFixed = 0;
            for (int i = 0; i < pattern.Count; i++) {
                patternFixed += FixedTrackSize(pattern[i], containerSize);
            }
            int patternTrackCount = pattern.Count;
            // Solve for max n such that n * patternFixed + (n * patternTrackCount - 1) * gap <= containerSize.
            int reps = 1;
            if (patternFixed > 0 && containerSize > 0) {
                double per = patternFixed + gap * patternTrackCount;
                if (per <= 0) reps = 1;
                else {
                    reps = (int)((containerSize + gap) / per);
                    if (reps < 1) reps = 1;
                }
            }
            var outTracks = new List<GridTrackSize>(pattern.Count * reps);
            var outNames = new List<List<string>>();
            outNames.Add(new List<string>());
            for (int rep = 0; rep < reps; rep++) {
                outNames[outNames.Count - 1].AddRange(patternLines[0]);
                for (int t = 0; t < pattern.Count; t++) {
                    outTracks.Add(pattern[t]);
                    var l = new List<string>();
                    foreach (var n in patternLines[t + 1]) l.Add(n);
                    outNames.Add(l);
                }
            }
            var ro = new List<IReadOnlyList<string>>(outNames.Count);
            foreach (var l in outNames) ro.Add(l);
            return (outTracks.ToArray(), ro);
        }

        static double FixedTrackSize(GridTrackSize t, double containerSize) {
            switch (t.Kind) {
                case GridTrackKind.Length: return t.Value;
                // Percentage tracks resolve against the container's inline
                // size for the auto-fill repetition count (CSS Grid L1
                // §7.2.2.3). When the container is indefinite (containerSize
                // <= 0) percentages contribute 0 here, which falls into the
                // caller's `patternFixed > 0` guard → reps stays at 1.
                case GridTrackKind.Percentage:
                    return containerSize > 0 ? containerSize * t.Value * 0.01 : 0;
                case GridTrackKind.Auto:
                case GridTrackKind.MinContent:
                case GridTrackKind.MaxContent:
                case GridTrackKind.Fr:
                // fit-content has no minimum fixed contribution — its base is
                // 0 and the limit only caps the content contribution.
                case GridTrackKind.FitContent: return 0;
                case GridTrackKind.Minmax:
                    var min = t.MinChild();
                    if (min.Kind == GridTrackKind.Length) return min.Value;
                    if (min.Kind == GridTrackKind.Percentage)
                        return containerSize > 0 ? containerSize * min.Value * 0.01 : 0;
                    var max = t.MaxChild();
                    if (max.Kind == GridTrackKind.Length) return max.Value;
                    if (max.Kind == GridTrackKind.Percentage)
                        return containerSize > 0 ? containerSize * max.Value * 0.01 : 0;
                    return 0;
            }
            return 0;
        }

        public static SizedTracks Resolve(GridTrackSize[] tracks,
                                          GridTrackSize[] autoTracks,
                                          int totalTrackCount,
                                          double containerSize,
                                          double gap,
                                          List<SizingItem> items,
                                          bool isColumnAxis,
                                          bool autoFitCollapseEmpty,
                                          bool stretchAutoTracksOnRemainder = true) {
            // Materialize the actual track list extending past the explicit grid
            // with auto-tracks (cycled).
            var trackList = new List<GridTrackSize>(totalTrackCount);
            int explicit_ = tracks?.Length ?? 0;
            for (int i = 0; i < totalTrackCount; i++) {
                if (i < explicit_) trackList.Add(tracks[i]);
                else if (autoTracks != null && autoTracks.Length > 0) trackList.Add(autoTracks[(i - explicit_) % autoTracks.Length]);
                else trackList.Add(GridTrackSize.Auto);
            }

            int n = trackList.Count;
            var sizes = new double[n];
            var maxes = new double[n];
            var collapsed = new bool[n];

            // Phase 1a: resolve fixed/percentage minimums.
            for (int i = 0; i < n; i++) {
                var t = trackList[i];
                ResolveBaseAndMax(t, containerSize, out double baseSize, out double maxSize);
                sizes[i] = baseSize;
                maxes[i] = maxSize;
                // For minmax(<concrete-min>, <length|%>) the track has a
                // definite max that is not flexible — grow the base toward
                // that max so fr distribution only divides the truly-
                // remaining free space (CSS Grid §11). Without this,
                // `minmax(240px, 22%)` stuck at 240 when 22% of a wider
                // container would be larger, and the 1fr middle track
                // absorbed the leftover, breaking layouts like `.hud {
                // grid-template-columns: minmax(240px, 22%) 1fr
                // minmax(240px, 22%) }`.
                //
                // E10: only apply this pre-inflation when the MIN sizing
                // function is concrete (length/percentage). For intrinsic
                // mins (auto/min-content/max-content), CSS Grid L1 §11.4
                // initializes the base at 0 and §11.5 raises it from the
                // items' intrinsic contribution in Phase 1b below; the
                // final clamp at line 340 caps the result at maxSize. The
                // previous unconditional inflation forced `minmax(auto,
                // 100px)` (and `minmax(min-content, 100px)`) to 100 even
                // when the item's intrinsic was smaller, collapsing the
                // spec's "intrinsic floor, under max" semantics.
                bool minIsConcrete = t.Kind == GridTrackKind.Minmax
                    && (t.MinKind == GridTrackKind.Length
                        || t.MinKind == GridTrackKind.Percentage);
                if (t.Kind == GridTrackKind.Minmax
                    && t.MaxKind != GridTrackKind.Fr
                    && minIsConcrete
                    && maxSize > sizes[i]) {
                    sizes[i] = maxSize;
                }
            }

            // Phase 1b: items contribute intrinsic sizes to spanned intrinsic tracks.
            // Single-track items first (more deterministic), then multi-track items.
            //
            // B8 / CSS Grid L1 §11.5: distribute contribution according to
            // growth limits rather than splitting evenly. Walk the intrinsic
            // tracks in priority order:
            //   1. min-content growth limit  (tightest — content overflows if ignored)
            //   2. max-content growth limit
            //   3. auto / flexible growth limit (most accommodating)
            // In each pass distribute the remaining contribution evenly among
            // the eligible tracks (up to each track's growth limit, if finite).
            // Contribution left un-absorbed in a pass carries forward to the
            // next pass so no space is lost.
            //
            // Fr tracks: per CSS Grid §11.5, intrinsic content contributes to
            // an fr track's base size ONLY when the container is indefinite
            // (the spec's "max-content" sizing pass for indefinite containers).
            // When the container is definite, fr tracks start at base 0 and
            // are sized purely by Phase 2 free-space distribution. Without
            // this distinction, e.g. `grid-template-rows: auto 1fr auto` in
            // a `height: 100vh` container would inflate the 1fr row to the
            // messages-area max-content (700px), eat all the negative
            // "remaining" in Phase 2, and overflow the container — even when
            // there's no auto track competing. Treat fr as intrinsic only
            // for indefinite containers (preserves the existing auto-height
            // grid behavior where fr collapses to max-content).
            bool containerIsDefinite = containerSize > 0;
            for (int spanLen = 1; spanLen <= n; spanLen++) {
                foreach (var it in items) {
                    int s = isColumnAxis ? it.Area.ColumnStart - 1 : it.Area.RowStart - 1;
                    int e = isColumnAxis ? it.Area.ColumnEnd - 1 : it.Area.RowEnd - 1;
                    if (s < 0) s = 0; if (e > n) e = n;
                    int sp = e - s;
                    if (sp != spanLen) continue;
                    if (sp <= 0) continue;
                    double size = isColumnAxis ? it.IntrinsicMain : it.IntrinsicCross;
                    if (size <= 0) continue;
                    // Subtract sizes already absorbed by non-intrinsic (fixed/percentage)
                    // tracks and by gaps.
                    double accountedFixed = 0;
                    for (int k = s; k < e; k++) {
                        bool receivesIntrinsic = trackList[k].IsIntrinsic
                            || (trackList[k].IsFlexible && !containerIsDefinite);
                        if (!receivesIntrinsic) {
                            accountedFixed += sizes[k];
                        }
                    }
                    double rem = size - accountedFixed - gap * (sp - 1);
                    if (rem <= 0) continue;

                    // §11.5 growth-limit-aware distribution:
                    //   Pass 0 — min-content growth-limit tracks (tightest).
                    //   Pass 1 — max-content growth-limit tracks.
                    //   Pass 2 — auto / flexible growth-limit tracks (most flexible).
                    // Contribution left un-absorbed (because all eligible tracks in
                    // the pass were capped at their growth limits) carries forward
                    // to the next pass so no space is lost.
                    for (int pass = 0; pass <= 2; pass++) {
                        if (rem <= 0) break;
                        // Collect eligible (non-capped) tracks for this pass.
                        // We reuse the span-relative capped[] array across passes;
                        // tracks capped in pass N stay capped in pass N+1 (they're
                        // already at their growth limit and can't grow further here).
                        int passCount = 0;
                        for (int k = s; k < e; k++) {
                            if (PassPriority(trackList[k], containerIsDefinite) == pass)
                                passCount++;
                        }
                        if (passCount == 0) continue;

                        // Distribute `rem` evenly among pass-eligible tracks.
                        // If a track hits its finite growth limit (maxes[k] > 0),
                        // freeze it, deduct the amount it absorbed from `rem`,
                        // and redistribute the remainder to the still-uncapped
                        // tracks. Loop until no new caps fire (or all capped).
                        // Because spans are small (typically 2-6 tracks) the loop
                        // terminates quickly.
                        var passCapped = new bool[sp];    // indexed relative to s
                        int uncapped = passCount;
                        double passConsumed = 0;           // total absorbed THIS pass
                        bool changed = true;
                        while (changed && uncapped > 0 && rem > 0) {
                            changed = false;
                            double share = rem / uncapped;
                            for (int k = s; k < e; k++) {
                                if (PassPriority(trackList[k], containerIsDefinite) != pass) continue;
                                int ki = k - s;
                                if (passCapped[ki]) continue;
                                double proposed = sizes[k] > share ? sizes[k] : share;
                                // Cap at finite growth limit.
                                if (maxes[k] > 0 && proposed >= maxes[k]) {
                                    double gain = maxes[k] > sizes[k] ? maxes[k] - sizes[k] : 0;
                                    passConsumed += gain;
                                    rem -= gain;
                                    sizes[k] = maxes[k];
                                    passCapped[ki] = true;
                                    uncapped--;
                                    changed = true;
                                }
                            }
                        }
                        // Apply final share to remaining uncapped tracks.
                        if (uncapped > 0 && rem > 0) {
                            double share = rem / uncapped;
                            for (int k = s; k < e; k++) {
                                if (PassPriority(trackList[k], containerIsDefinite) != pass) continue;
                                int ki = k - s;
                                if (passCapped[ki]) continue;
                                if (share > sizes[k]) sizes[k] = share;
                            }
                            rem = 0;
                        }
                        // If uncapped == 0, all tracks in this pass hit their growth
                        // limits; `rem` already reflects only the unabsorbed balance
                        // (we subtracted gains above) and carries to the next pass.
                    }
                }
            }

            // auto-fit collapse: any track containing no items collapses to 0.
            if (autoFitCollapseEmpty) {
                var hasItem = new bool[n];
                foreach (var it in items) {
                    int s = isColumnAxis ? it.Area.ColumnStart - 1 : it.Area.RowStart - 1;
                    int e = isColumnAxis ? it.Area.ColumnEnd - 1 : it.Area.RowEnd - 1;
                    if (s < 0) s = 0; if (e > n) e = n;
                    for (int k = s; k < e; k++) hasItem[k] = true;
                }
                for (int i = 0; i < n; i++) {
                    if (!hasItem[i]) {
                        sizes[i] = 0;
                        collapsed[i] = true;
                    }
                }
            }

            // Phase 1c — CSS Grid L1 s6.6 + s7.2.4: bare <flex> tracks are
            // minmax(auto, <flex>); in a DEFINITE container their base stayed
            // 0 through Phase 1b (see the rationale above — max-content must
            // NOT inflate them), but the AUTO minimum still floors them at
            // the items' MIN-content contribution. Phase 2's freeze loop
            // already respects bases that exceed the fair share, so the floor
            // composes with proportional distribution + redistribution for
            // free. Multi-track spans split the remainder evenly across the
            // span's flexible tracks (documented simplification).
            if (containerIsDefinite) {
                foreach (var it in items) {
                    double minSize = isColumnAxis ? it.IntrinsicMainMin : 0;
                    if (minSize <= 0) continue;
                    int s2 = isColumnAxis ? it.Area.ColumnStart - 1 : it.Area.RowStart - 1;
                    int e2 = isColumnAxis ? it.Area.ColumnEnd - 1 : it.Area.RowEnd - 1;
                    if (s2 < 0) s2 = 0; if (e2 > n) e2 = n;
                    if (e2 <= s2) continue;
                    // ONLY bare <flex> tracks carry the AUTO minimum —
                    // minmax(<min>, <flex>) declares its own minimum (0,
                    // 60px, ...) which Phase 1a already resolved; flooring
                    // those too inflated every minmax(len, 1fr) stress grid
                    // (grid-playground GPU diff jumped 5.2% -> 22.7%).
                    double accounted = 0;
                    int bareFrCount = 0;
                    for (int k = s2; k < e2; k++) {
                        if (trackList[k].Kind == GridTrackKind.Fr) bareFrCount++;
                        else accounted += sizes[k];
                    }
                    if (bareFrCount == 0) continue;
                    double rem2 = minSize - accounted - gap * (e2 - s2 - 1);
                    if (rem2 <= 0) continue;
                    double perFlex = rem2 / bareFrCount;
                    for (int k = s2; k < e2; k++) {
                        if (trackList[k].Kind == GridTrackKind.Fr && sizes[k] < perFlex) sizes[k] = perFlex;
                    }
                }
            }

            // Compute used non-fr space.
            double usedFixed = 0;
            int gapCount = 0;
            for (int i = 0; i < n; i++) {
                if (collapsed[i]) continue;
                if (!trackList[i].IsFlexible) {
                    usedFixed += sizes[i];
                }
            }
            // gaps between non-collapsed adjacent tracks.
            int activeCount = 0;
            for (int i = 0; i < n; i++) if (!collapsed[i]) activeCount++;
            gapCount = activeCount > 1 ? activeCount - 1 : 0;
            double gapsTotal = gap * gapCount;

            // Phase 2: distribute remaining space to fr tracks.
            //
            // K7 — CSS Grid L1 §11.7 "Expand Flexible Tracks":
            //   (1) Find the hypothetical fr size (§11.7.1). The leftover
            //       space subtracts ONLY the non-flexible tracks' bases
            //       (and gaps) — NOT the flex tracks' bases (those are
            //       the things we're sizing). hypothetical_fr =
            //       leftover / sum(flex factors). Iterate: any flex
            //       track whose `hypothetical_fr × factor < base` is
            //       frozen at its base; restart with leftover decremented
            //       by its base and the divisor reduced by its factor.
            //   (2) Each remaining (unfrozen) flex track's final size is
            //       `max(base, hypothetical_fr × factor)`.
            //
            // Pre-K7 the loop did `sizes[i] += per * frVal` AND subtracted
            // the flex bases from `remaining`. Those two bugs partially
            // cancelled for the no-intrinsic-fr case but mis-sized fr
            // tracks that had absorbed intrinsic in Phase 1b. The spec-
            // correct flow: leftover EXCLUDES flex bases, distribution
            // is `max` (not `+=`), and intrinsic-loaded fr tracks freeze
            // and surrender their slice of the divisor to the rest.
            var frFactor = new double[n];
            for (int i = 0; i < n; i++) {
                if (collapsed[i]) continue;
                var t = trackList[i];
                if (t.Kind == GridTrackKind.Fr) frFactor[i] = t.Value;
                else if (t.Kind == GridTrackKind.Minmax && t.MaxKind == GridTrackKind.Fr) frFactor[i] = t.MaxValue;
            }
            double totalFr = 0;
            for (int i = 0; i < n; i++) totalFr += frFactor[i];
            if (totalFr > 0) {
                var frozen = new bool[n];
                double per = 0;
                // Iterative refinement, bounded by n passes (worst case
                // each flex track freezes one at a time).
                for (int pass = 0; pass <= n; pass++) {
                    double leftover = containerSize - usedFixed - gapsTotal;
                    double activeFr = 0;
                    for (int i = 0; i < n; i++) {
                        if (collapsed[i] || frFactor[i] <= 0) continue;
                        if (frozen[i]) leftover -= sizes[i];
                        else activeFr += frFactor[i];
                    }
                    if (activeFr <= 0 || leftover <= 0) { per = 0; break; }
                    per = leftover / activeFr;
                    bool anyFrozen = false;
                    for (int i = 0; i < n; i++) {
                        if (collapsed[i] || frFactor[i] <= 0 || frozen[i]) continue;
                        if (sizes[i] > per * frFactor[i]) {
                            frozen[i] = true;
                            anyFrozen = true;
                        }
                    }
                    if (!anyFrozen) break;
                }
                if (per > 0) {
                    for (int i = 0; i < n; i++) {
                        if (collapsed[i] || frFactor[i] <= 0 || frozen[i]) continue;
                        double share = per * frFactor[i];
                        if (share > sizes[i]) sizes[i] = share;
                    }
                }
            }
            // Recompute `remaining` for the auto-track stretch branch
            // below: container minus current track bases minus gaps. The
            // legacy "container − usedFixed − frFlexBases − gaps" form
            // mixed two concerns; the auto branch only needs free space
            // against current bases.
            double remaining;
            {
                double sum = 0;
                for (int i = 0; i < n; i++) if (!collapsed[i]) sum += sizes[i];
                remaining = containerSize - sum - gapsTotal;
            }
            // v1 simplification: when no fr tracks exist and there is at least one
            // auto track AND the container has a definite size, size the auto
            // tracks to fill the container. Two sub-cases:
            //   (a) remaining > 0: distribute the leftover space equally to the
            //       auto tracks so common patterns like `display: grid;
            //       place-items: center;` (no template) center children in the
            //       container, AND so an implicit row in a `height: 100vh` grid
            //       with no `grid-template-rows` fills the viewport.
            //   (b) remaining < 0 AND containerSize > 0: the items' max-content
            //       contributions in Phase 1b inflated auto tracks above the
            //       definite container size (e.g. a single implicit `auto` row
            //       holding a child whose pre-grid BlockLayout stack height
            //       exceeded 100vh). Redistribute by *shrinking* the auto
            //       tracks to fill the available content area — the children
            //       get clamped to cell size by ApplyItemAlignment downstream.
            //       Without this clamp, the row track stays at max-content of
            //       items and the container overflows; with it, auto tracks
            //       behave like fr tracks against a definite container, which
            //       matches the expected `display: grid; height: 100vh` shell
            //       pattern. (CSS spec only stretches on positive free space —
            //       this engine's v1 simplification is more aggressive.)
            if (totalFr == 0 && containerSize > 0) {
                int autoCount = 0;
                double autoSum = 0;
                for (int i = 0; i < n; i++) {
                    if (collapsed[i]) continue;
                    if (trackList[i].Kind == GridTrackKind.Auto) {
                        autoCount++;
                        autoSum += sizes[i];
                    }
                }
                if (autoCount > 0) {
                    if (remaining > 0 && stretchAutoTracksOnRemainder) {
                        // Only stretch auto tracks to fill leftover space when
                        // the caller asked for it (i.e. align-content / justify-
                        // content is `stretch` or `normal`). Without this guard,
                        // an inventory grid with `align-content: start` and
                        // intrinsic 40-px slots in a 2306-px-tall container
                        // would inflate every row track to 451 px (1/5 of the
                        // remainder), pushing siblings off-screen. Chrome only
                        // stretches when the cross-content distribution opts in.
                        double per = remaining / autoCount;
                        for (int i = 0; i < n; i++) {
                            if (collapsed[i]) continue;
                            if (trackList[i].Kind == GridTrackKind.Auto) sizes[i] += per;
                        }
                    } else if (remaining < 0 && autoSum > 0) {
                        // E11 — CSS Grid L1 §11.5 "Resolve Intrinsic Track
                        // Sizes": when ALL tracks are sized by intrinsic
                        // functions (auto / min-content / max-content / fit-
                        // content) and the sum of their Phase 1 base sizes
                        // exceeds the container, the engine MUST let them
                        // overflow — NOT shrink below their intrinsic minimums.
                        // Shrinking purely intrinsic tracks collapses content
                        // below its min-content contribution and produces the
                        // "every track 33px" pathology when three min-content-
                        // 50 items live in a 100px container.
                        //
                        // The shrink is only spec-correct when SOME track in
                        // the row has a definite upper bound it can shrink
                        // toward (a concrete length/percentage max). With fr
                        // present we never enter this branch (totalFr == 0);
                        // with `minmax(intrinsic, <length|%>)` the track has
                        // Kind == Minmax and isn't selected by the loop below
                        // anyway, so we look at whether any non-collapsed
                        // track has a concrete max (`maxes[i] > 0`) to decide
                        // whether the v1 auto-shrink helper still fires.
                        bool hasConcreteMaxTrack = false;
                        for (int i = 0; i < n; i++) {
                            if (collapsed[i]) continue;
                            if (maxes[i] > 0) { hasConcreteMaxTrack = true; break; }
                        }
                        if (hasConcreteMaxTrack) {
                            // Available space for auto tracks = autoSum + remaining
                            // (remaining is negative, so we shrink). Floor at 0.
                            double availForAuto = autoSum + remaining;
                            if (availForAuto < 0) availForAuto = 0;
                            double scale = availForAuto / autoSum;
                            for (int i = 0; i < n; i++) {
                                if (collapsed[i]) continue;
                                if (trackList[i].Kind == GridTrackKind.Auto) sizes[i] *= scale;
                            }
                        }
                        // else: all tracks are purely intrinsic — let them
                        // overflow at their Phase 1 base sizes per spec.
                    }
                }
            }

            // Apply max clamps.
            for (int i = 0; i < n; i++) {
                if (collapsed[i]) continue;
                if (maxes[i] > 0 && sizes[i] > maxes[i]) sizes[i] = maxes[i];
            }

            var positions = new double[n];
            double cursor = 0;
            bool any = false;
            for (int i = 0; i < n; i++) {
                if (collapsed[i]) {
                    positions[i] = cursor;
                    continue;
                }
                if (any) cursor += gap;
                positions[i] = cursor;
                cursor += sizes[i];
                any = true;
            }
            return new SizedTracks { Sizes = sizes, Positions = positions, Collapsed = collapsed };
        }

        static void ResolveBaseAndMax(GridTrackSize t, double containerSize, out double baseSize, out double maxSize) {
            baseSize = 0;
            maxSize = 0;
            switch (t.Kind) {
                case GridTrackKind.Length:
                    baseSize = t.Value;
                    maxSize = t.Value;
                    return;
                case GridTrackKind.Percentage:
                    baseSize = containerSize > 0 ? containerSize * t.Value * 0.01 : 0;
                    maxSize = baseSize;
                    return;
                case GridTrackKind.Auto:
                case GridTrackKind.MinContent:
                case GridTrackKind.MaxContent:
                case GridTrackKind.Fr:
                    baseSize = 0;
                    maxSize = 0;
                    return;
                case GridTrackKind.FitContent: {
                    // CSS Grid L1 §7.2.3: behaves as minmax(auto, max-content)
                    // but the upper bound is clamped to the argument. We seed
                    // base=0 (auto-as-zero per the v1 simplification used by
                    // the other intrinsic tracks) and maxSize = the resolved
                    // limit; Phase 1b's existing `sizes[k] > maxes[k]` clamp
                    // delivers the cap. The limit is stashed in MaxKind/
                    // MaxValue by GridTrackSize.FitContent — read those slots
                    // directly because MaxChild() only unpacks for Minmax.
                    if (t.MaxKind == GridTrackKind.Length) maxSize = t.MaxValue;
                    else if (t.MaxKind == GridTrackKind.Percentage) maxSize = containerSize > 0 ? containerSize * t.MaxValue * 0.01 : 0;
                    else maxSize = 0;
                    baseSize = 0;
                    return;
                }
                case GridTrackKind.Minmax: {
                    var min = t.MinChild();
                    var max = t.MaxChild();
                    if (min.Kind == GridTrackKind.Length) baseSize = min.Value;
                    else if (min.Kind == GridTrackKind.Percentage) baseSize = containerSize > 0 ? containerSize * min.Value * 0.01 : 0;
                    else baseSize = 0;
                    bool maxIsConcrete = max.Kind == GridTrackKind.Length || max.Kind == GridTrackKind.Percentage;
                    if (max.Kind == GridTrackKind.Length) maxSize = max.Value;
                    else if (max.Kind == GridTrackKind.Percentage) maxSize = containerSize > 0 ? containerSize * max.Value * 0.01 : 0;
                    else maxSize = 0;
                    // CSS Grid L1 §7.2.1: "If the max is less than the min,
                    // then the max will be floored by the min (essentially
                    // yielding minmax(min, min))." Only floor when max is a
                    // concrete length/percentage — when max is intrinsic
                    // (auto/min-content/max-content/fr) maxSize=0 is a
                    // sentinel meaning "flexible", which we must not clobber.
                    if (maxIsConcrete && maxSize < baseSize) maxSize = baseSize;
                    return;
                }
            }
        }

        // CSS Grid L1 §11.5 "Increase sizes to accommodate spanning items
        // crossing content-sized tracks": distribution priority for base-size
        // increase is based on the track's MIN (base) sizing function, NOT the
        // max/growth-limit function.  Three buckets, processed in order:
        //   0 — min-content min sizing function (fill first; tightest)
        //   1 — max-content min sizing function
        //   2 — auto / fit-content min sizing function (most accommodating; last)
        //  -1 — not eligible
        //
        // Eligibility mirrors the old `receivesIntrinsic` rule to preserve
        // existing behaviour for pure fr / fixed tracks:
        //   A track is eligible when IsIntrinsic == true (i.e. at least one of
        //   min/max sizing functions is auto/min-content/max-content/fit-content)
        //   OR when the track is flexible AND the container is indefinite.
        //   Tracks that are purely fixed (length/%) or flexible-in-definite-
        //   container are excluded; their sizes were resolved in Phase 1a.
        //
        // Priority within eligible tracks uses the MIN (base-size) function:
        //   MinContent → 0, MaxContent → 1, Auto/FitContent/Fr → 2.
        // For minmax() that means t.MinKind; for plain types the track Kind.
        static int PassPriority(GridTrackSize t, bool containerIsDefinite) {
            // Check eligibility (same gate as the old `receivesIntrinsic` flag).
            bool eligible = t.IsIntrinsic || (t.IsFlexible && !containerIsDefinite);
            if (!eligible) return -1;

            // Determine the base-size function kind to pick the priority bucket.
            GridTrackKind baseKind;
            switch (t.Kind) {
                case GridTrackKind.Minmax:
                    baseKind = t.MinKind;
                    break;
                case GridTrackKind.Fr:
                    // Bare fr with indefinite container — treat as auto priority.
                    baseKind = GridTrackKind.Auto;
                    break;
                case GridTrackKind.FitContent:
                    // fit-content = minmax(auto, ...) → auto base → priority 2.
                    baseKind = GridTrackKind.Auto;
                    break;
                default:
                    // Plain MinContent / MaxContent / Auto — use their own kind.
                    baseKind = t.Kind;
                    break;
            }
            switch (baseKind) {
                case GridTrackKind.MinContent: return 0;
                case GridTrackKind.MaxContent: return 1;
                case GridTrackKind.Auto:
                case GridTrackKind.Fr:   // fr in indefinite container landed here
                    return 2;
                // minmax(length|%, intrinsic) has IsIntrinsic == true (via MaxKind)
                // but a concrete min — treat as most-flexible since the concrete
                // floor was handled in Phase 1a.
                case GridTrackKind.Length:
                case GridTrackKind.Percentage:
                    return 2;
                default:
                    return -1;
            }
        }

        static GridTrackSize[] ToArray(IReadOnlyList<GridTrackSize> tracks) {
            if (tracks == null) return new GridTrackSize[0];
            var copy = new GridTrackSize[tracks.Count];
            for (int i = 0; i < tracks.Count; i++) copy[i] = tracks[i];
            return copy;
        }
    }
}
