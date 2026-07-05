using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class UpgradeMeterHeightTests {
        static BlockBox FindByClass(Box root, string className) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    var cls = bb.Element.GetAttribute("class");
                    if (cls != null && cls.Contains(className)) return bb;
                }
            }
            return null;
        }

        [Test]
        public void Meter_with_inline_gradient_respects_class_height_3px() {
            var css = @"
                .card { display: flex; flex-direction: column; align-items: center; width: 140px; padding: 16px 10px 12px; }
                .foot { width: 100%; display: flex; flex-direction: column; gap: 4px; }
                .meter { width: 100%; height: 3px; border-radius: 999px; }
            ";
            var html = @"
                <div class=""card"">
                    <div class=""foot"">
                        <div class=""meter"" style=""background: linear-gradient(to right, #ffd22f 30%, rgba(255,255,255,0.1) 30%)""></div>
                    </div>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var meter = FindByClass(root, "meter");
            Assert.That(meter, Is.Not.Null, "meter box must exist");
            Assert.That(meter.Height, Is.EqualTo(3).Within(0.5), $"meter height should be 3px, got {meter.Height}");
        }

        [Test]
        public void Meter_in_grid_with_inline_gradient_respects_height_3px() {
            var css = @"
                .grid { display: grid; grid-template-columns: repeat(3, 140px); gap: 10px; }
                .card { display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 16px 10px 12px; }
                .icon { width: 52px; height: 52px; }
                .body { display: flex; flex-direction: column; gap: 2px; align-items: center; }
                .name { font-size: 12px; }
                .foot { width: 100%; display: flex; flex-direction: column; gap: 4px; margin-top: auto; }
                .meter { width: 100%; height: 3px; border-radius: 999px; }
                .cost { font-size: 12px; text-align: center; }
            ";
            var html = @"
                <div class=""grid"">
                    <div class=""card"">
                        <div class=""icon""></div>
                        <div class=""body"">
                            <span class=""name"">Increased Damage</span>
                            <span class=""name"">3/10</span>
                        </div>
                        <div class=""foot"">
                            <div class=""meter"" style=""background: linear-gradient(to right, #ffd22f 30%, rgba(255,255,255,0.1) 30%)""></div>
                            <span class=""cost"">200</span>
                        </div>
                    </div>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var meter = FindByClass(root, "meter");
            Assert.That(meter, Is.Not.Null, "meter box must exist");
            Assert.That(meter.Height, Is.EqualTo(3).Within(0.5), $"meter height should be 3px, got {meter.Height}");
        }

        [Test]
        public void Padding_only_button_in_row_flex_not_inflated() {
            var css = @"
                .row { display: flex; align-items: center; gap: 12px; padding: 12px; width: 400px; }
                .btn { padding: 8px 16px; font-size: 13px; font-weight: 700; border: 1px solid #ccc; border-radius: 6px; }
            ";
            var html = @"<div class=""row""><button class=""btn"">Save</button><button class=""btn"">Cancel</button></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var btn = FindByClass(root, "btn");
            Assert.That(btn, Is.Not.Null, "button box must exist");
            Assert.That(btn.Height, Is.LessThan(50), $"button height {btn.Height} should be ~35px (padding+font+border), not inflated");
        }

        [Test]
        public void Column_flex_with_row_flex_child_not_inflated() {
            var css = @"
                .col { display: flex; flex-direction: column; gap: 16px; width: 400px; height: 400px; }
                .header { display: flex; align-items: center; gap: 12px; padding: 12px; }
                .body { flex: 1; }
                .btn { padding: 8px 16px; font-size: 13px; border: none; border-radius: 6px; }
            ";
            var html = @"
                <div class=""col"">
                    <div class=""header""><button class=""btn"">Tab1</button><button class=""btn"">Tab2</button></div>
                    <div class=""body""></div>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var header = FindByClass(root, "header");
            Assert.That(header, Is.Not.Null, "header box must exist");
            Assert.That(header.Height, Is.LessThan(100), $"header height {header.Height} should be content-derived (~60-90px), not inflated to fill 400px column");
        }

        [Test]
        public void Grid_1fr_slots_with_aspect_ratio_correct_size() {
            var css = @"
                .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; width: 600px; }
                .slot { aspect-ratio: 1; background: #1c2435; }
            ";
            var html = @"
                <div class=""grid"">
                    <div class=""slot""></div>
                    <div class=""slot""></div>
                    <div class=""slot""></div>
                    <div class=""slot""></div>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var slot = FindByClass(root, "slot");
            Assert.That(slot, Is.Not.Null, "slot box must exist");
            double expectedTrackWidth = (600.0 - 3 * 8) / 4.0;
            Assert.That(slot.Width, Is.EqualTo(expectedTrackWidth).Within(1), $"slot width should be {expectedTrackWidth}, got {slot.Width}");
            Assert.That(slot.Height, Is.EqualTo(slot.Width).Within(1), $"slot height should equal width (aspect-ratio:1), got w={slot.Width} h={slot.Height}");
        }

        [Test]
        public void Flex_row_button_width_not_inflated() {
            var css = @"
                .row { display: flex; align-items: center; gap: 12px; width: 600px; padding: 8px; }
                .text { flex: 1; font-size: 13px; }
                .buy { padding: 8px 20px; font-size: 13px; font-weight: 700; border: none; border-radius: 6px; }
            ";
            var html = @"<div class=""row""><span class=""text"">Item name</span><button class=""buy"">120 Coins</button></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var buy = FindByClass(root, "buy");
            Assert.That(buy, Is.Not.Null, "buy box must exist");
            Assert.That(buy.Width, Is.LessThan(200), $"buy button width {buy.Width} should be content-sized (~120px), not inflated");
        }

        [Test]
        public void Percent_width_fill_inside_flex_card_stays_within_track() {
            var css = @"
                .card { width: 140px; display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 16px 10px 12px; }
                .track { width: 100%; height: 3px; background: #333; align-self: stretch; }
                .fill { height: 3px; background: gold; }
            ";
            var html = @"
                <div class=""card"">
                    <span>Title</span>
                    <div class=""track""><div class=""fill"" style=""width: 30%""></div></div>
                    <span>200</span>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var track = FindByClass(root, "track");
            var fill = FindByClass(root, "fill");
            Assert.That(track, Is.Not.Null, "track must exist");
            Assert.That(fill, Is.Not.Null, "fill must exist");
            double cardContentWidth = 140;
            Assert.That(track.Width, Is.EqualTo(cardContentWidth).Within(2), $"track should be {cardContentWidth}px (content-box), got {track.Width}");
            double expectedFill = cardContentWidth * 0.3;
            Assert.That(fill.Width, Is.LessThan(track.Width), $"fill ({fill.Width}) must be narrower than track ({track.Width})");
            Assert.That(fill.Width, Is.EqualTo(expectedFill).Within(2), $"fill should be 30% of track ({expectedFill}), got {fill.Width}");
        }

        [Test]
        public void Percent_width_fill_inside_button_flex_card() {
            var css = @"
                .card { width: 140px; display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 16px 10px 12px; }
                .foot { width: 100%; display: flex; flex-direction: column; gap: 4px; margin-top: auto; }
                .track { width: 100%; height: 4px; background: #333; overflow: hidden; }
                .fill { height: 100%; background: gold; }
            ";
            var html = @"
                <button class=""card"">
                    <span>Title</span>
                    <div class=""foot"">
                        <div class=""track""><div class=""fill"" style=""width: 30%""></div></div>
                        <span>200</span>
                    </div>
                </button>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var track = FindByClass(root, "track");
            var fill = FindByClass(root, "fill");
            Assert.That(track, Is.Not.Null, "track must exist");
            Assert.That(fill, Is.Not.Null, "fill must exist");
            Assert.That(fill.Width, Is.LessThan(track.Width),
                $"fill ({fill.Width}) must be narrower than track ({track.Width}) — width:30% should constrain it");
        }

        [Test]
        public void Percent_width_fill_in_grid_button_card_exact_production_pattern() {
            var css = @"
                * { box-sizing: border-box; margin: 0; padding: 0; }
                .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 10px; width: 600px; }
                .card { display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 16px 10px 12px; border: 2px solid transparent; border-radius: 8px; }
                .icon { width: 52px; height: 52px; }
                .body { display: flex; flex-direction: column; gap: 2px; align-items: center; }
                .name { font-size: 12px; }
                .track { width: 100%; height: 4px; background: #333; overflow: hidden; margin-top: auto; }
                .fill { display: block; height: 100%; background: gold; }
                .cost { font-size: 12px; }
            ";
            var html = @"
                <div class=""grid"">
                    <button class=""card"">
                        <div class=""icon""></div>
                        <div class=""body""><span class=""name"">Damage</span><span class=""name"">3/10</span></div>
                        <div class=""track""><span class=""fill"" style=""width: 30%""></span></div>
                        <span class=""cost"">200</span>
                    </button>
                    <button class=""card"">
                        <div class=""icon""></div>
                        <div class=""body""><span class=""name"">Defense</span><span class=""name"">0/10</span></div>
                        <div class=""track""><span class=""fill"" style=""width: 0%""></span></div>
                        <span class=""cost"">75</span>
                    </button>
                    <button class=""card"">
                        <div class=""icon""></div>
                        <div class=""body""><span class=""name"">Speed</span><span class=""name"">2/5</span></div>
                        <div class=""track""><span class=""fill"" style=""width: 40%""></span></div>
                        <span class=""cost"">120</span>
                    </button>
                    <button class=""card"">
                        <div class=""icon""></div>
                        <div class=""body""><span class=""name"">Health</span><span class=""name"">0/20</span></div>
                        <div class=""track""><span class=""fill"" style=""width: 0%""></span></div>
                        <span class=""cost"">30</span>
                    </button>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var track = FindByClass(root, "track");
            var fill = FindByClass(root, "fill");
            Assert.That(track, Is.Not.Null, "track must exist");
            Assert.That(fill, Is.Not.Null, "fill must exist");
            var card = FindByClass(root, "card");
            Assert.That(card, Is.Not.Null, "card must exist");
            System.Console.WriteLine($"  DIAG: card.W={card.Width:F1} track.W={track.Width:F1} fill.W={fill.Width:F1} card.ContentW={card.ContentWidth:F1}");
            Assert.That(track.Width, Is.LessThan(200), $"track width {track.Width} should fit within a grid column (~140px), not the full grid width");
            Assert.That(fill.Width, Is.LessThan(track.Width), $"fill ({fill.Width}) must be narrower than track ({track.Width})");
        }

        [Test]
        public void Meter_without_inline_style_respects_class_height_3px() {
            var css = @"
                .card { display: flex; flex-direction: column; align-items: center; width: 140px; padding: 16px 10px 12px; }
                .foot { width: 100%; display: flex; flex-direction: column; gap: 4px; }
                .meter { width: 100%; height: 3px; border-radius: 999px; background: rgba(255,255,255,0.1); }
            ";
            var html = @"
                <div class=""card"">
                    <div class=""foot"">
                        <div class=""meter""></div>
                    </div>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var meter = FindByClass(root, "meter");
            Assert.That(meter, Is.Not.Null, "meter box must exist");
            Assert.That(meter.Height, Is.EqualTo(3).Within(0.5), $"meter height should be 3px, got {meter.Height}");
        }
    }
}
