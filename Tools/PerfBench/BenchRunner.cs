using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Reactive;

namespace Weva.PerfBench {
    static class BenchRunner {
        public static void RunCascade(List<BenchResult> results, bool slow) {
            Console.WriteLine("[Cascade]");
            RunComputeAll(results, BenchScenes.Build100Cards());
            RunComputeAll(results, BenchScenes.Build500Mixed());
            RunComputeAll(results, BenchScenes.Build1000Forms());
            RunComputeAll(results, BenchScenes.Build1000Deep());
            RunComputeAll(results, BenchScenes.BuildHudHeavyPseudos());
            if (slow) RunComputeAll(results, BenchScenes.Build5000Massive(), iterations: 10, warmup: 3);
            RunIncrementalAttr(results);
            RunIncrementalHover(results);
            RunCascadeAllocCheck(results);
            RunSnapshotVsManaged(results);
        }

        public static void RunLayout(List<BenchResult> results, bool slow) {
            Console.WriteLine("[Layout]");
            RunLayoutAll(results, BenchScenes.Build100Cards());
            RunLayoutAll(results, BenchScenes.Build500Mixed());
            RunLayoutAll(results, BenchScenes.Build1000Forms());
            RunLayoutAll(results, BenchScenes.Build1000Deep());
            if (slow) RunLayoutAll(results, BenchScenes.Build5000Massive(), iterations: 8, warmup: 2);
            RunLayoutAllocCheck(results);
        }

        public static void RunLayoutKernels(List<BenchResult> results, bool slow) {
            Console.WriteLine("[LayoutKernels]");
            RunFlexKernelPair(results, 256);
            RunFlexKernelPair(results, 1024);
            RunFlexKernelPair(results, 4096);
            RunExtractFlatApplyKernel(results, 1500);
            RunGridAutoPlacementKernelPair(results, 512);
            RunGridAutoPlacementKernelPair(results, 2048);
            if (slow) {
                RunFlexKernelPair(results, 16384, iterations: 1200, warmup: 200);
                RunExtractFlatApplyKernel(results, 5000, iterations: 1200, warmup: 200);
                RunGridAutoPlacementKernelPair(results, 8192, iterations: 300, warmup: 60);
            }
        }

        public static void RunPaint(List<BenchResult> results, bool slow) {
            Console.WriteLine("[Paint]");
            RunPaintConvert(results, "Paint.Convert", "500", BenchScenes.BuildPaintFlatTree(500), 500);
            RunPaintConvert(results, "Paint.Convert", "1000", BenchScenes.BuildPaintFlatTree(1000), 1000);
            if (slow) RunPaintConvert(results, "Paint.Convert", "5000", BenchScenes.BuildPaintFlatTree(5000), 5000, iterations: 50);
            RunPaintConvert(results, "Paint.Convert_GradientHeavy", "500", BenchScenes.BuildPaintGradientTree(500), 500);
            RunPaintConvert(results, "Paint.Convert_ShadowHeavy", "500", BenchScenes.BuildPaintShadowTree(500), 500);
            RunPaintAllocCheck(results, BenchScenes.BuildPaintFlatTree(500), 500);
            RunPaintAllocCheck(results, BenchScenes.BuildPaintFlatTree(1000), 1000);
        }

        public static void RunEndToEnd(List<BenchResult> results) {
            Console.WriteLine("[EndToEnd]");
            RunFullPipeline(results, BenchScenes.Build500Mixed(), 100);
            RunHoverToggle(results, BenchScenes.Build1000Forms(), 1000);
            RunStableFlexTextUpdate(results, 1000);
            RunViewportResizeStableMedia(results, 500);
            RunViewportResizeQuests(results, 500);
            RunHoverWithLayoutProp(results, 1000);
        }

        // ===== individual benches =====

        static void RunComputeAll(List<BenchResult> results, BenchScenes.Scene s, int iterations = 30, int warmup = 5) {
            var engine = new CascadeEngine(s.Sheets, true);
            var (med, p95, p99) = BenchScenes.Time(iterations, warmup, () => {
                engine.InvalidateAll();
                engine.ComputeAll(s.Document);
            });
            results.Add(new BenchResult {
                Name = "Cascade.ComputeAll",
                Scale = $"{s.ElementCount} elem ({s.Name})",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = "cold cascade (InvalidateAll then ComputeAll)"
            });
            Console.WriteLine($"  ComputeAll {s.Name} ({s.ElementCount} elem): {med:F3}ms (p95 {p95:F3}, p99 {p99:F3})");
        }

        static void RunIncrementalAttr(List<BenchResult> results) {
            var s = BenchScenes.Build1000Forms();
            var engine = s.Cascade;
            Element target = null;
            foreach (var e in BenchScenes.AllElements(s.Document)) {
                if (e.TagName == "label") { target = e; break; }
            }
            int toggle = 0;
            var (med, p95, p99) = BenchScenes.Time(30, 5, () => {
                target?.SetAttribute("class", (toggle++ & 1) == 0 ? "highlight" : "");
                engine.Invalidate(target);
                engine.ComputeAll(s.Document);
            });
            results.Add(new BenchResult {
                Name = "Cascade.Incremental_AttributeChange",
                Scale = $"{s.ElementCount} elem", MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = "toggle one element's class, full re-cascade"
            });
            Console.WriteLine($"  Incremental_AttributeChange ({s.ElementCount} elem): {med:F3}ms");
        }

        static void RunIncrementalHover(List<BenchResult> results) {
            var s = BenchScenes.Build1000Forms();
            var engine = new CascadeEngine(s.Sheets, true);
            engine.ComputeAll(s.Document);
            Element target = null;
            foreach (var e in BenchScenes.AllElements(s.Document)) {
                if (e.TagName == "button") { target = e; break; }
            }
            var state = new HoverState();
            int toggle = 0;
            var (med, p95, p99) = BenchScenes.Time(30, 5, () => {
                state.SetHover((toggle++ & 1) == 0 ? target : null);
                engine.ComputeAll(s.Document, state);
            });
            results.Add(new BenchResult {
                Name = "Cascade.Incremental_PseudoClassChange",
                Scale = $"{s.ElementCount} elem", MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = ":hover toggle on/off; per-element state-digest cache key keeps non-target elements as cache hits"
            });
            Console.WriteLine($"  Incremental_PseudoClassChange ({s.ElementCount} elem): {med:F3}ms");
        }

        static void RunCascadeAllocCheck(List<BenchResult> results) {
            var s = BenchScenes.Build1000Forms();
            var engine = new CascadeEngine(s.Sheets, true);
            engine.ComputeAll(s.Document);
            for (int w = 0; w < 5; w++) engine.ComputeAll(s.Document);
            BenchScenes.StabilizeGC();
            long before = BenchScenes.AllocatedBytes();
            for (int i = 0; i < 100; i++) engine.ComputeAll(s.Document);
            long after = BenchScenes.AllocatedBytes();
            long perCall = (after - before) / 100;
            results.Add(new BenchResult {
                Name = "Cascade.IncrementalApply_AllocCheck",
                Scale = $"{s.ElementCount} elem",
                BytesPerCall = perCall, MedianMs = 0,
                Notes = "warm full-cache ComputeAll allocations"
            });
            Console.WriteLine($"  IncrementalApply_AllocCheck ({s.ElementCount} elem): {perCall} B/call");
        }

        static void RunSnapshotVsManaged(List<BenchResult> results) {
            var s = BenchScenes.Build500Mixed();
            var snapEngine = new CascadeEngine(s.Sheets, true);
            var (snapMed, snapP95, _) = BenchScenes.Time(30, 5, () => {
                snapEngine.InvalidateAll();
                snapEngine.ComputeAll(s.Document);
            });
            var manEngine = new CascadeEngine(s.Sheets, false);
            var (manMed, manP95, _) = BenchScenes.Time(30, 5, () => {
                manEngine.InvalidateAll();
                manEngine.ComputeAll(s.Document);
            });
            double speedup = manMed / snapMed;
            results.Add(new BenchResult {
                Name = "Cascade.SnapshotPath_Vs_Managed",
                Scale = $"{s.ElementCount} elem",
                MedianMs = snapMed, P95Ms = snapP95,
                Notes = $"snapshot {speedup:F2}x faster than managed ({manMed:F3}ms managed)"
            });
            Console.WriteLine($"  SnapshotPath_Vs_Managed: {speedup:F2}x");
        }

        static void RunLayoutAll(List<BenchResult> results, BenchScenes.Scene s, int iterations = 20, int warmup = 5) {
            var resolver = BenchScenes.StyleResolver(s);
            var (med, p95, p99) = BenchScenes.Time(iterations, warmup, () => s.Layout.Layout(s.Document, resolver, s.Context));
            results.Add(new BenchResult {
                Name = "Layout.LayoutAll",
                Scale = $"{s.ElementCount} elem ({s.Name})",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = "full pipeline layout"
            });
            Console.WriteLine($"  LayoutAll {s.Name} ({s.ElementCount} elem): {med:F3}ms (p95 {p95:F3})");
        }

        static void RunLayoutAllocCheck(List<BenchResult> results) {
            var s = BenchScenes.Build1000Forms();
            var resolver = BenchScenes.StyleResolver(s);
            for (int w = 0; w < 5; w++) s.Layout.Layout(s.Document, resolver, s.Context);
            BenchScenes.StabilizeGC();
            long before = BenchScenes.AllocatedBytes();
            const int n = 20;
            for (int i = 0; i < n; i++) s.Layout.Layout(s.Document, resolver, s.Context);
            long after = BenchScenes.AllocatedBytes();
            long perCall = (after - before) / n;
            results.Add(new BenchResult {
                Name = "Layout.LayoutAll_AllocCheck",
                Scale = $"{s.ElementCount} elem",
                BytesPerCall = perCall, MedianMs = 0,
                Notes = "warm pool, 20 back-to-back layouts"
            });
            Console.WriteLine($"  Layout.AllocCheck ({s.ElementCount} elem): {perCall} B/call");
        }

        static void RunFlexKernelPair(List<BenchResult> results, int count, int iterations = 1000, int warmup = 100) {
            var flat = FlexKernelArrays.Create(count);
            var packed = FlexKernelStructItem.Create(count);
            var objects = FlexKernelObjectItem.Create(count);
            double container = Math.Max(320, count * 7.25);

            double FlatBody() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    checksum += RunFlatFlexRow(flat, count, container + (i & 31));
                }
                return checksum;
            }

            double PackedBody() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    checksum += RunPackedFlexRow(packed, count, container + (i & 31));
                }
                return checksum;
            }

            double ObjectBody() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    checksum += RunObjectFlexRow(objects, count, container + (i & 31));
                }
                return checksum;
            }

            for (int i = 0; i < warmup; i++) {
                kernelSink += RunFlatFlexRow(flat, count, container + (i & 31));
                kernelSink += RunPackedFlexRow(packed, count, container + (i & 31));
                kernelSink += RunObjectFlexRow(objects, count, container + (i & 31));
            }

            var (flatMed, flatP95, flatP99) = BenchScenes.Time(20, 2, () => kernelSink += FlatBody());
            long flatAllocs = BenchScenes.MeasureAllocs(5, 1, () => kernelSink += FlatBody());
            AddKernelResult(results, $"LayoutKernel.FlatFlexRow_{count}", count, flatMed / iterations, flatP95 / iterations, flatP99 / iterations, flatAllocs / iterations,
                "flat SoA flex free-space distribution candidate; excludes DOM/style/text");
            Console.WriteLine($"  FlatFlexRow {count}: {(flatMed / iterations) * 1000:F2}us/pass alloc={flatAllocs / iterations}B/pass");

            var (packedMed, packedP95, packedP99) = BenchScenes.Time(20, 2, () => kernelSink += PackedBody());
            long packedAllocs = BenchScenes.MeasureAllocs(5, 1, () => kernelSink += PackedBody());
            AddKernelResult(results, $"LayoutKernel.PackedFlexRow_{count}", count, packedMed / iterations, packedP95 / iterations, packedP99 / iterations, packedAllocs / iterations,
                "packed struct flex free-space distribution; closest managed proxy for a C/Burst item buffer");
            Console.WriteLine($"  PackedFlexRow {count}: {(packedMed / iterations) * 1000:F2}us/pass alloc={packedAllocs / iterations}B/pass");

            var (objMed, objP95, objP99) = BenchScenes.Time(20, 2, () => kernelSink += ObjectBody());
            long objAllocs = BenchScenes.MeasureAllocs(5, 1, () => kernelSink += ObjectBody());
            AddKernelResult(results, $"LayoutKernel.ObjectFlexRow_{count}", count, objMed / iterations, objP95 / iterations, objP99 / iterations, objAllocs / iterations,
                "same flex math over managed objects; isolates object traversal overhead");
            Console.WriteLine($"  ObjectFlexRow {count}: {(objMed / iterations) * 1000:F2}us/pass alloc={objAllocs / iterations}B/pass");
        }

        static void RunExtractFlatApplyKernel(List<BenchResult> results, int count, int iterations = 1000, int warmup = 100) {
            var boxes = KernelBox.Create(count);
            var flat = FlexKernelArrays.Create(count);
            double container = Math.Max(320, count * 7.25);

            double Body() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    ExtractFlex(boxes, flat, count);
                    checksum += RunFlatFlexRow(flat, count, container + (i & 31));
                    ApplyFlex(boxes, flat, count);
                }
                return checksum + boxes[count - 1].X;
            }

            for (int i = 0; i < warmup; i++) {
                ExtractFlex(boxes, flat, count);
                kernelSink += RunFlatFlexRow(flat, count, container + (i & 31));
                ApplyFlex(boxes, flat, count);
            }
            var (med, p95, p99) = BenchScenes.Time(16, 2, () => kernelSink += Body());
            long allocs = BenchScenes.MeasureAllocs(5, 1, () => kernelSink += Body());
            AddKernelResult(results, $"LayoutKernel.ExtractFlatApply_{count}", count, med / iterations, p95 / iterations, p99 / iterations, allocs / iterations,
                "copy object layout inputs to flat arrays, run flat flex, copy outputs back; estimates native/Burst bridge tax");
            Console.WriteLine($"  ExtractFlatApply {count}: {(med / iterations) * 1000:F2}us/pass alloc={allocs / iterations}B/pass");
        }

        static void RunGridAutoPlacementKernelPair(List<BenchResult> results, int count, int iterations = 200, int warmup = 30) {
            const int columns = 12;
            int rows = Math.Max(64, (count / columns) + 64);
            var flat = GridKernelArrays.Create(count, columns, rows);
            var packed = GridKernelStructItem.Create(count);
            var packedOccupancy = new int[columns * rows];
            var objects = GridKernelObjectItem.Create(count);
            var objectOccupancy = new int[columns * rows];

            double FlatBody() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    checksum += RunFlatGridAutoPlacement(flat, count, columns, rows);
                }
                return checksum;
            }

            double PackedBody() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    checksum += RunPackedGridAutoPlacement(packed, packedOccupancy, count, columns, rows);
                }
                return checksum;
            }

            double ObjectBody() {
                double checksum = 0;
                for (int i = 0; i < iterations; i++) {
                    checksum += RunObjectGridAutoPlacement(objects, objectOccupancy, count, columns, rows);
                }
                return checksum;
            }

            for (int i = 0; i < warmup; i++) {
                kernelSink += RunFlatGridAutoPlacement(flat, count, columns, rows);
                kernelSink += RunPackedGridAutoPlacement(packed, packedOccupancy, count, columns, rows);
                kernelSink += RunObjectGridAutoPlacement(objects, objectOccupancy, count, columns, rows);
            }

            var (flatMed, flatP95, flatP99) = BenchScenes.Time(14, 2, () => kernelSink += FlatBody());
            long flatAllocs = BenchScenes.MeasureAllocs(4, 1, () => kernelSink += FlatBody());
            AddKernelResult(results, $"LayoutKernel.FlatGridAutoPlace_{count}", count, flatMed / iterations, flatP95 / iterations, flatP99 / iterations, flatAllocs / iterations,
                "flat occupancy scan for grid auto-placement; candidate for SIMD/Burst only after extraction cost is known");
            Console.WriteLine($"  FlatGridAutoPlace {count}: {(flatMed / iterations) * 1000:F2}us/pass alloc={flatAllocs / iterations}B/pass");

            var (packedMed, packedP95, packedP99) = BenchScenes.Time(14, 2, () => kernelSink += PackedBody());
            long packedAllocs = BenchScenes.MeasureAllocs(4, 1, () => kernelSink += PackedBody());
            AddKernelResult(results, $"LayoutKernel.PackedGridAutoPlace_{count}", count, packedMed / iterations, packedP95 / iterations, packedP99 / iterations, packedAllocs / iterations,
                "packed struct auto-placement; closer to a native item buffer than managed objects");
            Console.WriteLine($"  PackedGridAutoPlace {count}: {(packedMed / iterations) * 1000:F2}us/pass alloc={packedAllocs / iterations}B/pass");

            var (objMed, objP95, objP99) = BenchScenes.Time(14, 2, () => kernelSink += ObjectBody());
            long objAllocs = BenchScenes.MeasureAllocs(4, 1, () => kernelSink += ObjectBody());
            AddKernelResult(results, $"LayoutKernel.ObjectGridAutoPlace_{count}", count, objMed / iterations, objP95 / iterations, objP99 / iterations, objAllocs / iterations,
                "same auto-placement over managed item objects and shared occupancy array");
            Console.WriteLine($"  ObjectGridAutoPlace {count}: {(objMed / iterations) * 1000:F2}us/pass alloc={objAllocs / iterations}B/pass");
        }

        static void AddKernelResult(List<BenchResult> results, string name, int count, double med, double p95, double p99, long bytesPerCall, string notes) {
            results.Add(new BenchResult {
                Name = name,
                Scale = $"{count} items",
                MedianMs = med,
                P95Ms = p95,
                P99Ms = p99,
                BytesPerCall = bytesPerCall,
                Notes = notes
            });
        }

        static double RunFlatFlexRow(FlexKernelArrays a, int count, double containerWidth) {
            double outer = 0;
            double growSum = 0;
            double shrinkSum = 0;
            for (int i = 0; i < count; i++) {
                outer += a.Basis[i] + a.MarginStart[i] + a.MarginEnd[i];
                growSum += a.Grow[i];
                shrinkSum += a.Shrink[i] * a.Basis[i];
            }

            double free = containerWidth - outer;
            double cursor = 0;
            double checksum = 0;
            if (free >= 0 && growSum > 0) {
                for (int i = 0; i < count; i++) {
                    double width = Clamp(a.Basis[i] + free * (a.Grow[i] / growSum), a.Min[i], a.Max[i]);
                    cursor += a.MarginStart[i];
                    a.X[i] = cursor;
                    a.Width[i] = width;
                    cursor += width + a.MarginEnd[i];
                    checksum += width * 0.125 + a.X[i] * 0.0001;
                }
            } else if (free < 0 && shrinkSum > 0) {
                for (int i = 0; i < count; i++) {
                    double weight = a.Shrink[i] * a.Basis[i];
                    double width = Clamp(a.Basis[i] + free * (weight / shrinkSum), a.Min[i], a.Max[i]);
                    cursor += a.MarginStart[i];
                    a.X[i] = cursor;
                    a.Width[i] = width;
                    cursor += width + a.MarginEnd[i];
                    checksum += width * 0.125 + a.X[i] * 0.0001;
                }
            } else {
                for (int i = 0; i < count; i++) {
                    cursor += a.MarginStart[i];
                    a.X[i] = cursor;
                    a.Width[i] = a.Basis[i];
                    cursor += a.Basis[i] + a.MarginEnd[i];
                    checksum += a.Width[i] * 0.125 + a.X[i] * 0.0001;
                }
            }
            return checksum;
        }

        static double RunPackedFlexRow(FlexKernelStructItem[] items, int count, double containerWidth) {
            double outer = 0;
            double growSum = 0;
            double shrinkSum = 0;
            for (int i = 0; i < count; i++) {
                ref var item = ref items[i];
                outer += item.Basis + item.MarginStart + item.MarginEnd;
                growSum += item.Grow;
                shrinkSum += item.Shrink * item.Basis;
            }

            double free = containerWidth - outer;
            double cursor = 0;
            double checksum = 0;
            if (free >= 0 && growSum > 0) {
                for (int i = 0; i < count; i++) {
                    ref var item = ref items[i];
                    double width = Clamp(item.Basis + free * (item.Grow / growSum), item.Min, item.Max);
                    cursor += item.MarginStart;
                    item.X = cursor;
                    item.Width = width;
                    cursor += width + item.MarginEnd;
                    checksum += width * 0.125 + item.X * 0.0001;
                }
            } else if (free < 0 && shrinkSum > 0) {
                for (int i = 0; i < count; i++) {
                    ref var item = ref items[i];
                    double weight = item.Shrink * item.Basis;
                    double width = Clamp(item.Basis + free * (weight / shrinkSum), item.Min, item.Max);
                    cursor += item.MarginStart;
                    item.X = cursor;
                    item.Width = width;
                    cursor += width + item.MarginEnd;
                    checksum += width * 0.125 + item.X * 0.0001;
                }
            } else {
                for (int i = 0; i < count; i++) {
                    ref var item = ref items[i];
                    cursor += item.MarginStart;
                    item.X = cursor;
                    item.Width = item.Basis;
                    cursor += item.Basis + item.MarginEnd;
                    checksum += item.Width * 0.125 + item.X * 0.0001;
                }
            }
            return checksum;
        }

        static double RunObjectFlexRow(FlexKernelObjectItem[] items, int count, double containerWidth) {
            double outer = 0;
            double growSum = 0;
            double shrinkSum = 0;
            for (int i = 0; i < count; i++) {
                var item = items[i];
                outer += item.Basis + item.MarginStart + item.MarginEnd;
                growSum += item.Grow;
                shrinkSum += item.Shrink * item.Basis;
            }

            double free = containerWidth - outer;
            double cursor = 0;
            double checksum = 0;
            if (free >= 0 && growSum > 0) {
                for (int i = 0; i < count; i++) {
                    var item = items[i];
                    double width = Clamp(item.Basis + free * (item.Grow / growSum), item.Min, item.Max);
                    cursor += item.MarginStart;
                    item.X = cursor;
                    item.Width = width;
                    cursor += width + item.MarginEnd;
                    checksum += width * 0.125 + item.X * 0.0001;
                }
            } else if (free < 0 && shrinkSum > 0) {
                for (int i = 0; i < count; i++) {
                    var item = items[i];
                    double weight = item.Shrink * item.Basis;
                    double width = Clamp(item.Basis + free * (weight / shrinkSum), item.Min, item.Max);
                    cursor += item.MarginStart;
                    item.X = cursor;
                    item.Width = width;
                    cursor += width + item.MarginEnd;
                    checksum += width * 0.125 + item.X * 0.0001;
                }
            } else {
                for (int i = 0; i < count; i++) {
                    var item = items[i];
                    cursor += item.MarginStart;
                    item.X = cursor;
                    item.Width = item.Basis;
                    cursor += item.Basis + item.MarginEnd;
                    checksum += item.Width * 0.125 + item.X * 0.0001;
                }
            }
            return checksum;
        }

        static double RunPackedGridAutoPlacement(GridKernelStructItem[] items, int[] occupied, int count, int columns, int rows) {
            Array.Clear(occupied, 0, occupied.Length);
            double checksum = 0;
            int cursorRow = 0;
            int cursorCol = 0;
            for (int i = 0; i < count; i++) {
                ref var item = ref items[i];
                bool placed = false;
                for (int row = cursorRow; row < rows && !placed; row++) {
                    int startCol = row == cursorRow ? cursorCol : 0;
                    for (int col = startCol; col <= columns - item.ColSpan; col++) {
                        if (!CanPlace(occupied, columns, rows, row, col, item.RowSpan, item.ColSpan)) continue;
                        MarkGrid(occupied, columns, row, col, item.RowSpan, item.ColSpan);
                        item.Row = row;
                        item.Col = col;
                        cursorRow = row;
                        cursorCol = col + item.ColSpan;
                        if (cursorCol >= columns) {
                            cursorCol = 0;
                            cursorRow++;
                        }
                        checksum += row * 13 + col;
                        placed = true;
                        break;
                    }
                }
            }
            return checksum;
        }

        static double RunFlatGridAutoPlacement(GridKernelArrays g, int count, int columns, int rows) {
            Array.Clear(g.Occupied, 0, g.Occupied.Length);
            double checksum = 0;
            int cursorRow = 0;
            int cursorCol = 0;
            for (int i = 0; i < count; i++) {
                int colSpan = g.ColSpan[i];
                int rowSpan = g.RowSpan[i];
                bool placed = false;
                for (int row = cursorRow; row < rows && !placed; row++) {
                    int startCol = row == cursorRow ? cursorCol : 0;
                    for (int col = startCol; col <= columns - colSpan; col++) {
                        if (!CanPlace(g.Occupied, columns, rows, row, col, rowSpan, colSpan)) continue;
                        MarkGrid(g.Occupied, columns, row, col, rowSpan, colSpan);
                        g.Row[i] = row;
                        g.Col[i] = col;
                        cursorRow = row;
                        cursorCol = col + colSpan;
                        if (cursorCol >= columns) {
                            cursorCol = 0;
                            cursorRow++;
                        }
                        checksum += row * 13 + col;
                        placed = true;
                        break;
                    }
                }
            }
            return checksum;
        }

        static double RunObjectGridAutoPlacement(GridKernelObjectItem[] items, int[] occupied, int count, int columns, int rows) {
            Array.Clear(occupied, 0, occupied.Length);
            double checksum = 0;
            int cursorRow = 0;
            int cursorCol = 0;
            for (int i = 0; i < count; i++) {
                var item = items[i];
                bool placed = false;
                for (int row = cursorRow; row < rows && !placed; row++) {
                    int startCol = row == cursorRow ? cursorCol : 0;
                    for (int col = startCol; col <= columns - item.ColSpan; col++) {
                        if (!CanPlace(occupied, columns, rows, row, col, item.RowSpan, item.ColSpan)) continue;
                        MarkGrid(occupied, columns, row, col, item.RowSpan, item.ColSpan);
                        item.Row = row;
                        item.Col = col;
                        cursorRow = row;
                        cursorCol = col + item.ColSpan;
                        if (cursorCol >= columns) {
                            cursorCol = 0;
                            cursorRow++;
                        }
                        checksum += row * 13 + col;
                        placed = true;
                        break;
                    }
                }
            }
            return checksum;
        }

        static bool CanPlace(int[] occupied, int columns, int rows, int row, int col, int rowSpan, int colSpan) {
            if (row + rowSpan > rows) return false;
            for (int y = 0; y < rowSpan; y++) {
                int offset = (row + y) * columns + col;
                for (int x = 0; x < colSpan; x++) {
                    if (occupied[offset + x] != 0) return false;
                }
            }
            return true;
        }

        static void MarkGrid(int[] occupied, int columns, int row, int col, int rowSpan, int colSpan) {
            for (int y = 0; y < rowSpan; y++) {
                int offset = (row + y) * columns + col;
                for (int x = 0; x < colSpan; x++) occupied[offset + x] = 1;
            }
        }

        static void ExtractFlex(KernelBox[] boxes, FlexKernelArrays a, int count) {
            for (int i = 0; i < count; i++) {
                var box = boxes[i];
                a.Basis[i] = box.Basis;
                a.Min[i] = box.Min;
                a.Max[i] = box.Max;
                a.Grow[i] = box.Grow;
                a.Shrink[i] = box.Shrink;
                a.MarginStart[i] = box.MarginStart;
                a.MarginEnd[i] = box.MarginEnd;
            }
        }

        static void ApplyFlex(KernelBox[] boxes, FlexKernelArrays a, int count) {
            for (int i = 0; i < count; i++) {
                boxes[i].X = a.X[i];
                boxes[i].Width = a.Width[i];
            }
        }

        static double Clamp(double value, double min, double max) {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        static void RunPaintConvert(List<BenchResult> results, string name, string scale, BlockBox root, int boxCount, int iterations = 200) {
            var converter = new BoxToPaintConverter();
            var (med, p95, p99) = BenchScenes.Time(iterations, 50, () => converter.Convert(root));
            results.Add(new BenchResult {
                Name = name,
                Scale = $"{scale} boxes",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = $"{boxCount}-box flat tree"
            });
            Console.WriteLine($"  {name} {scale}: {med:F3}ms (p95 {p95:F3})");
        }

        static void RunPaintAllocCheck(List<BenchResult> results, BlockBox root, int boxCount) {
            var converter = new BoxToPaintConverter();
            // Warm: prime brush cache, JIT, command pool, list pool. Each warm
            // call returns its list so the steady-state has both stacks full.
            for (int w = 0; w < 100; w++) {
                var l = converter.Convert(root);
                converter.Return(l);
            }
            BenchScenes.StabilizeGC();
            const int n = 500;
            long before = BenchScenes.AllocatedBytes();
            for (int i = 0; i < n; i++) {
                var l = converter.Convert(root);
                converter.Return(l);
            }
            long after = BenchScenes.AllocatedBytes();
            long perCall = (after - before) / n;
            results.Add(new BenchResult {
                Name = "Paint.Convert_AllocCheck",
                Scale = $"{boxCount} boxes",
                BytesPerCall = perCall, MedianMs = 0,
                Notes = "warm converter, steady-state allocs (with PaintList/Cmd pool return)"
            });
            Console.WriteLine($"  Paint.Convert_AllocCheck {boxCount}: {perCall} B/call");
        }

        static void RunFullPipeline(List<BenchResult> results, BenchScenes.Scene s, int frames) {
            var cascade = new CascadeEngine(s.Sheets, true);
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            var paint = new BoxToPaintConverter();
            for (int w = 0; w < 5; w++) {
                var styles = cascade.ComputeAll(s.Document);
                var box = layout.Layout(s.Document, e => styles.TryGetValue(e, out var cs) ? cs : null, s.Context);
                paint.Convert(box);
            }
            BenchScenes.StabilizeGC();
            var times = new double[frames];
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < frames; i++) {
                sw.Restart();
                var styles = cascade.ComputeAll(s.Document);
                var box = layout.Layout(s.Document, e => styles.TryGetValue(e, out var cs) ? cs : null, s.Context);
                paint.Convert(box);
                sw.Stop();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            double med = BenchScenes.Median(times);
            double p95 = BenchScenes.Percentile(times, 0.95);
            double p99 = BenchScenes.Percentile(times, 0.99);
            results.Add(new BenchResult {
                Name = "EndToEnd.FullPipeline",
                Scale = $"{frames}f / {s.ElementCount} elem",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = "Cascade -> Layout -> Paint, full re-run per frame"
            });
            Console.WriteLine($"  FullPipeline {frames}f/{s.ElementCount}elem: {med:F3}ms (p95 {p95:F3}, p99 {p99:F3})");
        }

        static void RunHoverToggle(List<BenchResult> results, BenchScenes.Scene s, int frames) {
            var cascade = new CascadeEngine(s.Sheets, true);
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            var paint = new BoxToPaintConverter();
            var tracker = new InvalidationTracker();
            Element button = null;
            foreach (var e in BenchScenes.AllElements(s.Document)) {
                if (e.TagName == "button") { button = e; break; }
            }
            var state = new HoverState();
            for (int w = 0; w < 5; w++) {
                Element prev = state.Hovered;
                state.SetHover((w & 1) == 0 ? button : null);
                if (prev != null) tracker.MarkDirty(prev, InvalidationKind.PseudoClassState);
                if (state.Hovered != null) tracker.MarkDirty(state.Hovered, InvalidationKind.PseudoClassState);
                var styles = cascade.ComputeAll(s.Document, state);
                cascade.ApplyLayoutInvalidation(tracker);
                var box = layout.Layout(s.Document, e => styles.TryGetValue(e, out var cs) ? cs : null, s.Context, tracker);
                paint.Convert(box);
                tracker.Clear();
            }
            BenchScenes.StabilizeGC();
            layout.ResetCacheStats();
            var times = new double[frames];
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < frames; i++) {
                Element next = (i & 1) == 0 ? button : null;
                Element prev = state.Hovered;
                state.SetHover(next);
                if (prev != null) tracker.MarkDirty(prev, InvalidationKind.PseudoClassState);
                if (next != null) tracker.MarkDirty(next, InvalidationKind.PseudoClassState);
                sw.Restart();
                var styles = cascade.ComputeAll(s.Document, state);
                cascade.ApplyLayoutInvalidation(tracker);
                var box = layout.Layout(s.Document, e => styles.TryGetValue(e, out var cs) ? cs : null, s.Context, tracker);
                paint.Convert(box);
                sw.Stop();
                tracker.Clear();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            double med = BenchScenes.Median(times);
            double p95 = BenchScenes.Percentile(times, 0.95);
            double p99 = BenchScenes.Percentile(times, 0.99);
            results.Add(new BenchResult {
                Name = "EndToEnd.HoverToggle",
                Scale = $"{frames}f / {s.ElementCount} elem",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = $"toggle :hover on one button per frame; layout skipped {layout.SkipCount}/{frames} frames via IncrementalLayoutGate"
            });
            Console.WriteLine($"  HoverToggle {frames}f/{s.ElementCount}elem: {med:F3}ms (p95 {p95:F3}, p99 {p99:F3}) skips={layout.SkipCount}");
        }

        static void RunStableFlexTextUpdate(List<BenchResult> results, int frames) {
            const string ua =
                "html, body, div, section, header, span { display: block; } " +
                "span { display: inline; } body { margin: 0; padding: 0; }";
            const string author =
                ".shell { padding: 16px; }" +
                ".top { display: flex; align-items: center; }" +
                ".perf { display: flex; margin-left: auto; gap: 8px; }" +
                ".chip { display: flex; min-width: 72px; height: 24px; padding: 6px 9px; }" +
                ".row { display: flex; padding: 8px; }" +
                ".icon { width: 24px; height: 24px; }" +
                ".copy { display: block; padding-left: 12px; }" +
                ".title { display: block; width: 260px; height: 18px; }";
            var sb = new System.Text.StringBuilder();
            sb.Append("<section class=\"shell\"><header class=\"top\"><div>Quest Log</div><div class=\"perf\"><div class=\"chip\"><span id=\"frame\">10</span><span>ms</span></div></div></header>");
            for (int i = 0; i < 300; i++) {
                sb.Append("<div class=\"row\"><div class=\"icon\"></div><div class=\"copy\"><span class=\"title\">Quest ")
                    .Append(i)
                    .Append("</span><span>Track the missing watchmen before the seneschal can bury the story.</span></div></div>");
            }
            sb.Append("</section>");

            var doc = Weva.Parsing.HtmlParser.Parse(sb.ToString());
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(Weva.Css.CssParser.Parse(ua)),
                OriginatedStylesheet.Author(Weva.Css.CssParser.Parse(author))
            };
            var cascade = new CascadeEngine(sheets, true);
            var styles = cascade.ComputeAll(doc);
            int elementCount = 0;
            foreach (var _ in BenchScenes.AllElements(doc)) elementCount++;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1920,
                ViewportHeightPx = 1080,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            var paint = new BoxToPaintConverter();
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            var text = (TextNode)doc.GetElementById("frame").Children[0];

            var initial = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            paint.Convert(initial);
            tracker.Clear();

            BenchScenes.StabilizeGC();
            layout.ResetCacheStats();
            var times = new double[frames];
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < frames; i++) {
                text.Data = ((i % 90) + 10).ToString();
                sw.Restart();
                var box = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                paint.Convert(box);
                sw.Stop();
                tracker.Clear();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            double med = BenchScenes.Median(times);
            double p95 = BenchScenes.Percentile(times, 0.95);
            double p99 = BenchScenes.Percentile(times, 0.99);
            results.Add(new BenchResult {
                Name = "EndToEnd.StableFlexTextUpdate",
                Scale = $"{frames}f / {elementCount} elem",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = $"two-digit text update inside fixed-size flex chip; subtree-skipped {layout.SubtreeSkipHits}/{frames}"
            });
            Console.WriteLine($"  StableFlexTextUpdate {frames}f/{elementCount}elem: {med:F3}ms (p95 {p95:F3}, p99 {p99:F3}) subtreeSkips={layout.SubtreeSkipHits}");
        }

        static void RunViewportResizeStableMedia(List<BenchResult> results, int frames) {
            const string ua =
                "html, body, div, section, header, span { display: block; } " +
                "span { display: inline; } body { margin: 0; padding: 0; }";
            const string author =
                "@media (min-width: 600px) { .row { padding: 8px; } }" +
                ".shell { padding: 16px; }" +
                ".top { display: flex; align-items: center; }" +
                ".row { display: flex; }" +
                ".icon { width: 24px; height: 24px; }" +
                ".copy { display: block; padding-left: 12px; }" +
                ".title { display: block; width: 260px; height: 18px; }";
            var sb = new System.Text.StringBuilder();
            sb.Append("<section class=\"shell\"><header class=\"top\"><div>Quest Log</div></header>");
            for (int i = 0; i < 300; i++) {
                sb.Append("<div class=\"row\"><div class=\"icon\"></div><div class=\"copy\"><span class=\"title\">Quest ")
                    .Append(i)
                    .Append("</span><span>Track the missing watchmen before the seneschal can bury the story.</span></div></div>");
            }
            sb.Append("</section>");

            var doc = Weva.Parsing.HtmlParser.Parse(sb.ToString());
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(Weva.Css.CssParser.Parse(ua)),
                OriginatedStylesheet.Author(Weva.Css.CssParser.Parse(author))
            };
            var cascade = new CascadeEngine(sheets, Weva.Css.Media.MediaContext.Default(900, 700), true);
            IReadOnlyDictionary<Element, ComputedStyle> styles = cascade.ComputeAll(doc);
            int elementCount = 0;
            foreach (var _ in BenchScenes.AllElements(doc)) elementCount++;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 900,
                ViewportHeightPx = 700,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = cascade.LastSnapshot,
                SnapshotStyles = cascade.Styles
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            layout.CollectStageTimings = true;
            var paint = new BoxToPaintConverter();
            var tracker = new InvalidationTracker();
            var initial = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            paint.Convert(initial);

            BenchScenes.StabilizeGC();
            layout.ResetCacheStats();
            var times = new double[frames];
            var layoutTimes = new double[frames];
            var paintTimes = new double[frames];
            var sw = new System.Diagnostics.Stopwatch();
            var phase = new System.Diagnostics.Stopwatch();
            int cascadeRuns = 0;
            double buildSum = 0;
            double blockSum = 0;
            double analyzeSum = 0;
            double flexSum = 0;
            double positioningSum = 0;
            double repairSum = 0;
            double reconcileSum = 0;
            for (int i = 0; i < frames; i++) {
                double width = 900 + (i % 200);
                var media = Weva.Css.Media.MediaContext.Default(width, 700);
                bool mediaChanged = cascade.SetMediaContextForViewportResize(media);
                ctx.ViewportWidthPx = media.ViewportWidthPx;
                ctx.ViewportHeightPx = media.ViewportHeightPx;
                if (mediaChanged) {
                    styles = cascade.ComputeAll(doc);
                    ctx.Snapshot = cascade.LastSnapshot;
                    ctx.SnapshotStyles = cascade.Styles;
                    cascadeRuns++;
                }
                var kind = InvalidationKind.Layout | InvalidationKind.Paint;
                if (mediaChanged) kind |= InvalidationKind.Style;
                tracker.MarkDirty(doc, kind);

                sw.Restart();
                phase.Restart();
                var box = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                phase.Stop();
                layoutTimes[i] = phase.Elapsed.TotalMilliseconds;
                buildSum += layout.LastBuildMs;
                blockSum += layout.LastBlockMs;
                analyzeSum += layout.LastAnalyzeMs;
                flexSum += layout.LastFlexMs;
                positioningSum += layout.LastPositioningMs;
                repairSum += layout.LastRepairMs;
                reconcileSum += layout.LastReconcileMs;
                phase.Restart();
                paint.Convert(box);
                phase.Stop();
                paintTimes[i] = phase.Elapsed.TotalMilliseconds;
                sw.Stop();
                tracker.Clear();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            double med = BenchScenes.Median(times);
            double p95 = BenchScenes.Percentile(times, 0.95);
            double p99 = BenchScenes.Percentile(times, 0.99);
            double layoutMed = BenchScenes.Median(layoutTimes);
            double paintMed = BenchScenes.Median(paintTimes);
            results.Add(new BenchResult {
                Name = "EndToEnd.ViewportResizeStableMedia",
                Scale = $"{frames}f / {elementCount} elem",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = $"resize above a stable @media breakpoint; cascade recomputed {cascadeRuns}/{frames}; layout {layoutMed:F3}ms, paint {paintMed:F3}ms; avg stages build/block/analyze/flex/pos/repair/reconcile {buildSum / frames:F2}/{blockSum / frames:F2}/{analyzeSum / frames:F2}/{flexSum / frames:F2}/{positioningSum / frames:F2}/{repairSum / frames:F2}/{reconcileSum / frames:F2}ms"
            });
            Console.WriteLine($"  ViewportResizeStableMedia {frames}f/{elementCount}elem: {med:F3}ms (p95 {p95:F3}, p99 {p99:F3}) cascadeRuns={cascadeRuns} layout={layoutMed:F3}ms paint={paintMed:F3}ms stages={buildSum / frames:F2}/{blockSum / frames:F2}/{analyzeSum / frames:F2}/{flexSum / frames:F2}/{positioningSum / frames:F2}/{repairSum / frames:F2}/{reconcileSum / frames:F2}");
        }

        static void RunViewportResizeQuests(List<BenchResult> results, int frames) {
            string html = System.IO.File.ReadAllText(System.IO.Path.Combine("Assets", "UI", "quests.html"));
            string css = System.IO.File.ReadAllText(System.IO.Path.Combine("Assets", "UI", "quests.css"));
            var doc = Weva.Parsing.HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(Weva.Css.CssParser.Parse(BenchScenes.UA)),
                OriginatedStylesheet.Author(Weva.Css.CssParser.Parse(css))
            };
            var cascade = new CascadeEngine(sheets, Weva.Css.Media.MediaContext.Default(1600, 900), true);
            IReadOnlyDictionary<Element, ComputedStyle> styles = cascade.ComputeAll(doc);
            int elementCount = 0;
            foreach (var _ in BenchScenes.AllElements(doc)) elementCount++;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1600,
                ViewportHeightPx = 900,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = cascade.LastSnapshot,
                SnapshotStyles = cascade.Styles
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            var paint = new BoxToPaintConverter();
            var tracker = new InvalidationTracker();
            var initial = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            paint.Convert(initial);

            BenchScenes.StabilizeGC();
            layout.ResetCacheStats();
            var times = new double[frames];
            var layoutTimes = new double[frames];
            var paintTimes = new double[frames];
            var sw = new System.Diagnostics.Stopwatch();
            var phase = new System.Diagnostics.Stopwatch();
            int cascadeRuns = 0;
            for (int i = 0; i < frames; i++) {
                double width = 1500 + (i % 220);
                double height = 760 + (i % 140);
                var media = Weva.Css.Media.MediaContext.Default(width, height);
                bool mediaChanged = cascade.SetMediaContextForViewportResize(media);
                ctx.ViewportWidthPx = media.ViewportWidthPx;
                ctx.ViewportHeightPx = media.ViewportHeightPx;
                if (mediaChanged) {
                    styles = cascade.ComputeAll(doc);
                    ctx.Snapshot = cascade.LastSnapshot;
                    ctx.SnapshotStyles = cascade.Styles;
                    cascadeRuns++;
                }
                var kind = InvalidationKind.Layout | InvalidationKind.Paint;
                if (mediaChanged) kind |= InvalidationKind.Style;
                tracker.MarkDirty(doc, kind);

                sw.Restart();
                phase.Restart();
                var box = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                phase.Stop();
                layoutTimes[i] = phase.Elapsed.TotalMilliseconds;
                phase.Restart();
                paint.Convert(box);
                phase.Stop();
                paintTimes[i] = phase.Elapsed.TotalMilliseconds;
                sw.Stop();
                tracker.Clear();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            double med = BenchScenes.Median(times);
            double p95 = BenchScenes.Percentile(times, 0.95);
            double p99 = BenchScenes.Percentile(times, 0.99);
            double layoutMed = BenchScenes.Median(layoutTimes);
            double paintMed = BenchScenes.Median(paintTimes);
            results.Add(new BenchResult {
                Name = "EndToEnd.ViewportResizeQuests",
                Scale = $"{frames}f / {elementCount} elem",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = $"Assets/UI/quests resize; cascade recomputed {cascadeRuns}/{frames}; layout {layoutMed:F3}ms, paint {paintMed:F3}ms"
            });
            Console.WriteLine($"  ViewportResizeQuests {frames}f/{elementCount}elem: {med:F3}ms (p95 {p95:F3}, p99 {p99:F3}) cascadeRuns={cascadeRuns} layout={layoutMed:F3}ms paint={paintMed:F3}ms");
        }

        // EndToEnd.HoverWithLayoutProp_1000Frames bench. Custom scene of 250
        // cards × 4 block-level `.btn` children = 1000+ elements, with a
        // hover rule that flips border-width on the dirty btn. The hovered
        // .btn lives inside a card whose other children are also blocks, so
        // the parent card has ContainsInlines=false — exactly the pattern
        // the v1 subtree-skip predicate handles.
        //
        // Why a custom scene rather than reuse Build1000Forms? Form rows
        // mix inline content (labels) and an inline-default button — the
        // form-row's `ContainsInlines = true` disqualifies the subtree-skip
        // predicate (per CSS Inline L3 §3, line-box arrangement depends on
        // every inline's intrinsic width). The block-only card pattern is
        // both realistic and exercises the v1 subtree-skip path.
        //
        // v0.7 baseline (full re-layout): expected 4-8 ms; target after
        // subtree-skip: ≤ 1 ms.
        static void RunHoverWithLayoutProp(List<BenchResult> results, int frames) {
            const string ua =
                "html, body, div, section, p, h1, h2, h3 { display: block; } " +
                "span, a, b, i, em, strong { display: inline; } " +
                "body { margin: 0; padding: 0; }";
            const string author =
                ".container { padding: 8px; }" +
                ".card { padding: 8px; margin: 4px; }" +
                ".btn { display: block; padding: 4px 8px; }" +
                ".btn:hover { border-width: 4px; border-style: solid; }";
            var sb = new System.Text.StringBuilder();
            sb.Append("<section class=\"container\">");
            for (int i = 0; i < 250; i++) {
                sb.Append("<div class=\"card\">");
                sb.Append("<div class=\"btn\" id=\"b").Append(i).Append("\"></div>");
                sb.Append("<div class=\"btn\"></div><div class=\"btn\"></div><div class=\"btn\"></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            var doc = Weva.Parsing.HtmlParser.Parse(sb.ToString());
            var sheets = new List<Weva.Css.Cascade.OriginatedStylesheet> {
                Weva.Css.Cascade.OriginatedStylesheet.UserAgent(Weva.Css.CssParser.Parse(ua)),
                Weva.Css.Cascade.OriginatedStylesheet.Author(Weva.Css.CssParser.Parse(author))
            };
            var cascade = new CascadeEngine(sheets, true);
            int elementCount = 0;
            foreach (var _ in BenchScenes.AllElements(doc)) elementCount++;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024,
                ViewportHeightPx = 768,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            var paint = new BoxToPaintConverter();
            var tracker = new InvalidationTracker();
            Element button = doc.GetElementById("b0");
            var state = new HoverState();
            // Warmup: prime cascade + layout caches AND populate the cascade's
            // per-element previousStyle so LayoutAffectingPropertyChanged has
            // a non-null reference to compare against on the first hover.
            for (int w = 0; w < 5; w++) {
                Element prev = state.Hovered;
                state.SetHover((w & 1) == 0 ? button : null);
                if (prev != null) tracker.MarkDirty(prev, InvalidationKind.PseudoClassState);
                if (state.Hovered != null) tracker.MarkDirty(state.Hovered, InvalidationKind.PseudoClassState);
                var styles = cascade.ComputeAll(doc, state);
                cascade.ApplyLayoutInvalidation(tracker);
                var box = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                paint.Convert(box);
                tracker.Clear();
            }
            BenchScenes.StabilizeGC();
            layout.ResetCacheStats();
            var times = new double[frames];
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < frames; i++) {
                Element next = (i & 1) == 0 ? button : null;
                Element prev = state.Hovered;
                state.SetHover(next);
                if (prev != null) tracker.MarkDirty(prev, InvalidationKind.PseudoClassState);
                if (next != null) tracker.MarkDirty(next, InvalidationKind.PseudoClassState);
                sw.Restart();
                var styles = cascade.ComputeAll(doc, state);
                cascade.ApplyLayoutInvalidation(tracker);
                var box = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                paint.Convert(box);
                sw.Stop();
                tracker.Clear();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            double med = BenchScenes.Median(times);
            double p95 = BenchScenes.Percentile(times, 0.95);
            double p99 = BenchScenes.Percentile(times, 0.99);
            results.Add(new BenchResult {
                Name = "EndToEnd.HoverWithLayoutProp",
                Scale = $"{frames}f / {elementCount} elem",
                MedianMs = med, P95Ms = p95, P99Ms = p99,
                Notes = $"toggle :hover on one .btn per frame with border-width change; subtree-skipped {layout.SubtreeSkipHits}/{frames}, gate-skipped {layout.SkipCount}/{frames}"
            });
            Console.WriteLine($"  HoverWithLayoutProp {frames}f/{elementCount}elem: {med:F3}ms (p95 {p95:F3}, p99 {p99:F3}) subtreeSkips={layout.SubtreeSkipHits} gateSkips={layout.SkipCount}");
        }

        sealed class HoverState : IElementStateProvider {
            Element hovered;
            long version;
            public Element Hovered => hovered;
            public void SetHover(Element e) { if (!ReferenceEquals(hovered, e)) { hovered = e; version++; } }
            public ElementState GetState(Element e) => ReferenceEquals(e, hovered) ? ElementState.Hover : ElementState.None;
            public long Version => version;
        }

        static double kernelSink;

        sealed class FlexKernelArrays {
            public double[] Basis;
            public double[] Min;
            public double[] Max;
            public double[] Grow;
            public double[] Shrink;
            public double[] MarginStart;
            public double[] MarginEnd;
            public double[] X;
            public double[] Width;

            public static FlexKernelArrays Create(int count) {
                var a = new FlexKernelArrays {
                    Basis = new double[count],
                    Min = new double[count],
                    Max = new double[count],
                    Grow = new double[count],
                    Shrink = new double[count],
                    MarginStart = new double[count],
                    MarginEnd = new double[count],
                    X = new double[count],
                    Width = new double[count]
                };
                for (int i = 0; i < count; i++) {
                    a.Basis[i] = 12 + (i % 17);
                    a.Min[i] = 4 + (i % 3);
                    a.Max[i] = 48 + (i % 11);
                    a.Grow[i] = 1 + (i % 4);
                    a.Shrink[i] = 1 + (i % 2);
                    a.MarginStart[i] = i % 5;
                    a.MarginEnd[i] = (i + 2) % 5;
                }
                return a;
            }
        }

        sealed class FlexKernelObjectItem {
            public double Basis;
            public double Min;
            public double Max;
            public double Grow;
            public double Shrink;
            public double MarginStart;
            public double MarginEnd;
            public double X;
            public double Width;

            public static FlexKernelObjectItem[] Create(int count) {
                var items = new FlexKernelObjectItem[count];
                for (int i = 0; i < count; i++) {
                    items[i] = new FlexKernelObjectItem {
                        Basis = 12 + (i % 17),
                        Min = 4 + (i % 3),
                        Max = 48 + (i % 11),
                        Grow = 1 + (i % 4),
                        Shrink = 1 + (i % 2),
                        MarginStart = i % 5,
                        MarginEnd = (i + 2) % 5
                    };
                }
                return items;
            }
        }

        struct FlexKernelStructItem {
            public double Basis;
            public double Min;
            public double Max;
            public double Grow;
            public double Shrink;
            public double MarginStart;
            public double MarginEnd;
            public double X;
            public double Width;

            public static FlexKernelStructItem[] Create(int count) {
                var items = new FlexKernelStructItem[count];
                for (int i = 0; i < count; i++) {
                    items[i] = new FlexKernelStructItem {
                        Basis = 12 + (i % 17),
                        Min = 4 + (i % 3),
                        Max = 48 + (i % 11),
                        Grow = 1 + (i % 4),
                        Shrink = 1 + (i % 2),
                        MarginStart = i % 5,
                        MarginEnd = (i + 2) % 5
                    };
                }
                return items;
            }
        }

        sealed class KernelBox {
            public double Basis;
            public double Min;
            public double Max;
            public double Grow;
            public double Shrink;
            public double MarginStart;
            public double MarginEnd;
            public double X;
            public double Width;

            public static KernelBox[] Create(int count) {
                var boxes = new KernelBox[count];
                for (int i = 0; i < count; i++) {
                    boxes[i] = new KernelBox {
                        Basis = 12 + (i % 17),
                        Min = 4 + (i % 3),
                        Max = 48 + (i % 11),
                        Grow = 1 + (i % 4),
                        Shrink = 1 + (i % 2),
                        MarginStart = i % 5,
                        MarginEnd = (i + 2) % 5
                    };
                }
                return boxes;
            }
        }

        sealed class GridKernelArrays {
            public int[] RowSpan;
            public int[] ColSpan;
            public int[] Row;
            public int[] Col;
            public int[] Occupied;

            public static GridKernelArrays Create(int count, int columns, int rows) {
                var g = new GridKernelArrays {
                    RowSpan = new int[count],
                    ColSpan = new int[count],
                    Row = new int[count],
                    Col = new int[count],
                    Occupied = new int[columns * rows]
                };
                for (int i = 0; i < count; i++) {
                    g.ColSpan[i] = (i % 11) == 0 ? 2 : 1;
                    g.RowSpan[i] = (i % 7) == 0 ? 2 : 1;
                }
                return g;
            }
        }

        sealed class GridKernelObjectItem {
            public int RowSpan;
            public int ColSpan;
            public int Row;
            public int Col;

            public static GridKernelObjectItem[] Create(int count) {
                var items = new GridKernelObjectItem[count];
                for (int i = 0; i < count; i++) {
                    items[i] = new GridKernelObjectItem {
                        ColSpan = (i % 11) == 0 ? 2 : 1,
                        RowSpan = (i % 7) == 0 ? 2 : 1
                    };
                }
                return items;
            }
        }

        struct GridKernelStructItem {
            public int RowSpan;
            public int ColSpan;
            public int Row;
            public int Col;

            public static GridKernelStructItem[] Create(int count) {
                var items = new GridKernelStructItem[count];
                for (int i = 0; i < count; i++) {
                    items[i] = new GridKernelStructItem {
                        ColSpan = (i % 11) == 0 ? 2 : 1,
                        RowSpan = (i % 7) == 0 ? 2 : 1
                    };
                }
                return items;
            }
        }
    }
}
