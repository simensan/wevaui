using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // B8 / CSS Grid L1 §11.5 "Distribute space across intrinsic tracks from
    // spanning items": the engine must walk tracks in base-size-function
    // priority order (min-content first, then max-content, then auto) rather
    // than splitting the contribution evenly.
    //
    // Key consequence: when a higher-priority track is CAPPED by a finite
    // growth limit, the leftover contribution must carry forward to the next
    // lower-priority pass.  The old even-split code silently LOST this
    // leftover — capped tracks under-allocated and lower-priority tracks
    // received less than their share.
    //
    // Test layout convention:
    //   Container width = sum of expected track sizes so that the auto-track
    //   stretch pass has no remainder to inflate.  Measurement items are
    //   empty divs (no explicit width) placed in row 1 of each column so
    //   that `justify-self: stretch` (the default) reports the resolved
    //   track width.  A spanning item whose `width:` is explicit lives in
    //   row 2 and drives the IntrinsicMain contribution; measurement items
    //   contribute IntrinsicMain = 0 (empty).
    public class GridIntrinsicSpanDistributionTests {

        // B8 — Test 1 (canonical regression).
        //   2-track grid: `minmax(min-content, 50px)` + `auto`.
        //   Container = 120px.  Spanning item: width = 120px.
        //
        //   §11.5 walk (new code):
        //     Pass 0 (min-content base): col-0 capped at 50. gain=50. rem=70.
        //     Pass 2 (auto): col-1 gets 70. rem=0.
        //   Expected: col-0=50, col-1=70. Sum=120.
        //
        //   Old even-split: share=60. col-0=min(60,50)=50. col-1=60.
        //   Sum=110 — 10px lost in col-1 (got 60 not 70).
        [Test]
        public void Capped_min_content_track_overflows_remainder_to_auto_B8() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(min-content, 50px) auto; width: 120px; }
                .span { grid-column: 1 / 3; width: 120px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +    // col-0 measurement: auto-width, stretch
                    "<div></div>" +    // col-1 measurement
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            Assert.That(col0.Width, Is.EqualTo(50).Within(0.01),
                $"col-0 (minmax(min-content,50px)) capped at 50; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(70).Within(0.01),
                $"col-1 (auto) receives remainder 70px; pre-B8 was 60px; got {col1.Width}");
        }

        // B8 — Test 2.
        //   2-track grid: `minmax(min-content, 40px)` + `max-content`.
        //   Container = 100px.  Spanning item: width = 100px.
        //
        //   §11.5 walk:
        //     Pass 0 (min-content): col-0 capped at 40. gain=40. rem=60.
        //     Pass 1 (max-content): col-1 gets 60. rem=0.
        //   Expected: col-0=40, col-1=60.
        //
        //   Old even-split: share=50. col-0=min(50,40)=40. col-1=50.
        //   10px lost from col-1 (got 50 not 60).
        [Test]
        public void Capped_min_content_overflows_to_max_content_B8() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(min-content, 40px) max-content; width: 100px; }
                .span { grid-column: 1 / 3; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            Assert.That(col0.Width, Is.EqualTo(40).Within(0.01),
                $"col-0 (minmax(min-content,40px)) capped at 40; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(60).Within(0.01),
                $"col-1 (max-content) receives overflow 60px; pre-B8 was 50px; got {col1.Width}");
        }

        // B8 — Test 3 (three-track, two caps).
        //   3-track: `minmax(min-content,30px)` + `minmax(min-content,40px)` + `auto`.
        //   Container = 120px.  Spanning item: width = 120px.
        //
        //   §11.5 walk — Pass 0 (two min-content tracks, passRem=120):
        //     Iter 1: share=60. col-0 cap at 30 (gain=30, rem=90). col-1: 60<40 no cap.
        //     Iter 2: share=90. col-1 cap at 40 (gain=40, rem=50). All capped.
        //   Pass 2 (auto): col-2 gets 50.
        //   Expected: [30, 40, 50]. Sum=120.
        //
        //   Old even-split: share=40. col-0=30, col-1=40, col-2=40. Sum=110.
        //   10px lost in col-2 (got 40 not 50).
        [Test]
        public void Two_capped_min_content_tracks_overflow_remainder_to_auto_B8() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(min-content, 30px) minmax(min-content, 40px) auto; width: 120px; }
                .span { grid-column: 1 / 4; width: 120px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            var col2 = ChildAt(grid, 2);
            Assert.That(col0.Width, Is.EqualTo(30).Within(0.01),
                $"col-0 capped at 30; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(40).Within(0.01),
                $"col-1 capped at 40; got {col1.Width}");
            Assert.That(col2.Width, Is.EqualTo(50).Within(0.01),
                $"col-2 (auto) absorbs remainder 50px; pre-B8 was 40px; got {col2.Width}");
        }

        // B8 — Test 4 (min-content priority absorbs all before auto).
        //   2-track: `min-content` + `auto`.
        //   Container = 100px.  Spanning item: width = 100px.
        //
        //   §11.5 walk:
        //     Pass 0 (min-content): col-0 uncapped (maxes=0), share=100.
        //       col-0=100. rem=0.
        //     Pass 2 (auto): rem=0, skip. col-1 stays 0.
        //   Expected: col-0=100, col-1=0.
        //
        //   Old even-split: share=50. col-0=50. col-1=50.
        //   Distinction: col-0 should be 100 not 50.
        [Test]
        public void Min_content_track_absorbs_all_before_auto_when_uncapped_B8() {
            const string css = @"
                .grid { display: grid; grid-template-columns: min-content auto; width: 100px; }
                .span { grid-column: 1 / 3; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            // min-content track absorbs the full 100px contribution;
            // auto track receives nothing (rem=0 by the time pass 2 runs).
            Assert.That(col0.Width, Is.EqualTo(100).Within(0.01),
                $"col-0 (min-content) absorbs full 100px; pre-B8 was 50; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(0).Within(0.01),
                $"col-1 (auto) receives 0 — rem exhausted in pass 0; got {col1.Width}");
        }

        // B8 — Test 5 (no-op: uncapped same-priority tracks split evenly).
        //   2-track: `auto` + `auto`.
        //   Container = 100px.  Spanning item: width = 100px.
        //
        //   §11.5 walk: both in pass 2. share=50. Neither capped. Both=50.
        //   Expected: [50, 50]. Same as old even-split — regression-pin.
        [Test]
        public void Two_auto_tracks_still_split_evenly_B8_no_regression() {
            const string css = @"
                .grid { display: grid; grid-template-columns: auto auto; width: 100px; }
                .span { grid-column: 1 / 3; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            Assert.That(col0.Width, Is.EqualTo(50).Within(0.01),
                $"col-0 (auto) = 50; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(50).Within(0.01),
                $"col-1 (auto) = 50; got {col1.Width}");
        }

        // B8 — Test 6 (mixed fixed + intrinsic span).
        //   3-track: `60px` + `minmax(min-content, 40px)` + `auto`.
        //   Container = 150px.  Spanning item: width = 150px spans all 3.
        //
        //   §11.5 walk:
        //     accountedFixed = 60 (fixed track pre-consumed).
        //     rem = 150 - 60 = 90.
        //     Pass 0 (min-content): col-1 capped at 40. gain=40. rem=50.
        //     Pass 2 (auto): col-2 gets 50.
        //   Expected: col-0=60, col-1=40, col-2=50. Sum=150.
        //
        //   Old even-split: rem=90. share=45. col-1=40, col-2=45.
        //   5px lost in col-2 (got 45 not 50).
        [Test]
        public void Fixed_track_pre_consumed_remainder_distributed_per_spec_B8() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 60px minmax(min-content, 40px) auto; width: 150px; }
                .span { grid-column: 1 / 4; width: 150px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            var col2 = ChildAt(grid, 2);
            Assert.That(col0.Width, Is.EqualTo(60).Within(0.01),
                $"col-0 (fixed 60px) unchanged; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(40).Within(0.01),
                $"col-1 (minmax(min-content,40px)) capped at 40; got {col1.Width}");
            Assert.That(col2.Width, Is.EqualTo(50).Within(0.01),
                $"col-2 (auto) absorbs remainder 50px; pre-B8 was 45; got {col2.Width}");
        }

        // B8 — Test 7 (all three pass buckets exercised).
        //   4-track: `minmax(min-content,20px)` + `max-content` + `auto` + `auto`.
        //   Container = 180px.  Spanning item: width = 180px.
        //
        //   §11.5 walk:
        //     Pass 0 (min-content): col-0 capped at 20. gain=20. rem=160.
        //     Pass 1 (max-content): col-1 uncapped. share=160. col-1=160. rem=0.
        //     Pass 2 (auto): rem=0, skip.
        //   Expected: col-0=20, col-1=160, col-2=0, col-3=0.
        //
        //   Old even-split: share=45. col-0=min(45,20)=20. col-1=45. col-2=45. col-3=45.
        //   Sum=155 — 25px lost; col-1 got 45 not 160.
        [Test]
        public void All_three_pass_buckets_min_max_auto_distribute_correctly_B8() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(min-content, 20px) max-content auto auto; width: 180px; }
                .span { grid-column: 1 / 5; width: 180px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\">" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div></div>" +
                    "<div class=\"span\"></div>" +
                "</div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var col0 = ChildAt(grid, 0);
            var col1 = ChildAt(grid, 1);
            var col2 = ChildAt(grid, 2);
            var col3 = ChildAt(grid, 3);
            Assert.That(col0.Width, Is.EqualTo(20).Within(0.01),
                $"col-0 (minmax(min-content,20px)) capped at 20; got {col0.Width}");
            Assert.That(col1.Width, Is.EqualTo(160).Within(0.01),
                $"col-1 (max-content) absorbs 160px from pass-1; pre-B8 was 45; got {col1.Width}");
            Assert.That(col2.Width, Is.EqualTo(0).Within(0.01),
                $"col-2 (auto) gets nothing — rem exhausted in pass 1; got {col2.Width}");
            Assert.That(col3.Width, Is.EqualTo(0).Within(0.01),
                $"col-3 (auto) gets nothing — rem exhausted in pass 1; got {col3.Width}");
        }
    }
}
