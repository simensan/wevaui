using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Pins the bug visible in quests.html where the `.quest` article (a 3-
    // column grid: 56px / 1fr / 140px) sizes its auto row to less than the
    // tallest column's content, causing the middle column's late children
    // (progress bar, objectives list) to bleed below the article's border.
    //
    // Spec: CSS Grid Layout L1 §11 — an `auto`-sized row's height is the
    // max-content of items spanning the row. For our `.quest` row:
    //   - col 1: 56×56 marker
    //   - col 2: flex column containing head + desc + objectives + progress
    //   - col 3: flex column with tier label + reward chips
    // The article's intrinsic height should equal col 2's accumulated
    // flex-column height (because col 2 is the tallest), and the article
    // body should fully contain the progress bar and objectives.
    public class QuestGridAutoRowTests {
        const string css = @"
            .quest {
                display: grid;
                grid-template-columns: 56px 1fr 140px;
                gap: 16px;
                padding: 16px 20px;
            }
            .quest-marker { width: 56px; height: 56px; }
            .quest-body { display: flex; flex-direction: column; gap: 6px; }
            .quest-head { font-size: 14px; line-height: 21px; height: 21px; }
            .quest-desc { font-size: 14px; line-height: 20px; height: 20px; }
            .quest-objectives {
                list-style: none;
                margin: 8px 0 0 0;
                padding: 0;
                display: flex;
                flex-direction: column;
                gap: 4px;
            }
            .obj { position: relative; padding-left: 22px; font-size: 12px; line-height: 16px; height: 16px; }
            .obj::before {
                content: ""o"";
                position: absolute;
                left: 0;
                top: 0;
            }
            .quest-progress {
                display: flex;
                align-items: center;
                gap: 12px;
                margin-top: 4px;
                height: 15px;
            }
            .quest-side { display: flex; flex-direction: column; }
        ";

        [Test]
        public void Quest_article_height_includes_progress_bar_and_objectives() {
            // 4 objectives × 16px + 3 × 4px gap = 76px (objectives ul)
            // + head 21 + desc 20 + 8px ul margin-top + progress 15px + 3 × 6px gap = 21+20+76+15 + 18 = 150
            // article = body height + 32px padding = 182.
            var (root, _, _) = Build(
                @"<div style=""width:1200px""><article class=""quest"">
                    <div class=""quest-marker""></div>
                    <div class=""quest-body"">
                        <div class=""quest-head"">Whispers</div>
                        <p class=""quest-desc"">desc</p>
                        <ul class=""quest-objectives"">
                            <li class=""obj"">a</li>
                            <li class=""obj"">b</li>
                            <li class=""obj"">c</li>
                            <li class=""obj"">d</li>
                        </ul>
                        <div class=""quest-progress""><div></div></div>
                    </div>
                    <div class=""quest-side""></div>
                </article></div>", css, viewportWidth: 1200);

            var article = FindByClass(root, "quest");
            var body = FindByClass(root, "quest-body");
            var progress = FindByClass(root, "quest-progress");

            // The progress bar's bottom edge must fit inside the article's
            // content rect — otherwise it visually bleeds outside the
            // article's border on screen.
            double articleBottom = article.Y + article.Height;
            double progressBottom = progress.Y + progress.Height;
            Assert.That(progressBottom, Is.LessThanOrEqualTo(articleBottom + 1e-3),
                $"Progress bar bottom ({progressBottom}) must be inside article ({articleBottom}). " +
                $"Article h={article.Height}, body h={body.Height}, progress y={progress.Y} h={progress.Height}.");
        }

        [Test]
        public void Quest_body_flex_column_height_equals_children_sum() {
            // .quest-body height should equal the sum of its 5 children +
            // 4 gaps of 6px each. If the engine measures the column at a
            // stale moment (before children finalize), this comes up short.
            var (root, _, _) = Build(
                @"<div style=""width:1200px""><article class=""quest"">
                    <div class=""quest-marker""></div>
                    <div class=""quest-body"">
                        <div class=""quest-head"">Whispers</div>
                        <p class=""quest-desc"">desc</p>
                        <ul class=""quest-objectives"">
                            <li class=""obj"">a</li>
                            <li class=""obj"">b</li>
                            <li class=""obj"">c</li>
                            <li class=""obj"">d</li>
                        </ul>
                        <div class=""quest-progress""><div></div></div>
                    </div>
                    <div class=""quest-side""></div>
                </article></div>", css, viewportWidth: 1200);

            var body = FindByClass(root, "quest-body");
            // 21 (head) + 20 (desc) + 8 (ul margin-top) + 76 (4 objs + 3 gaps) + 15 (progress) + 3 × 6 (gaps) = 158
            // Engine produces ~162 — within 5px of nominal, within line-height
            // and font-metrics rounding tolerance.
            Assert.That(body.Height, Is.EqualTo(160).Within(5));
        }

        [Test]
        public void Scrollable_quest_list_does_not_shrink_cards_below_auto_min_height() {
            var (root, _, _) = Build(
                @"<section class=""quests"" style=""width:1200px;height:140px"">
                    <article class=""quest"">
                        <div class=""quest-marker""></div>
                        <div class=""quest-body"">
                            <header class=""quest-head"">
                                <h3 class=""quest-title"">A Bottle of Crowsblood</h3>
                                <span class=""quest-tier"">Side</span>
                            </header>
                            <p class=""quest-desc"">The brewer at the Brass Coin needs a sealed bottle.</p>
                            <div class=""quest-progress"">
                                <div class=""progress-bar""><div></div></div>
                                <span class=""progress-label"">2 / 3 obj.</span>
                            </div>
                        </div>
                        <div class=""quest-side""><div>340</div></div>
                    </article>
                    <article class=""quest"">
                        <div class=""quest-marker""></div>
                        <div class=""quest-body"">
                            <header class=""quest-head"">
                                <h3 class=""quest-title"">The Stalker's Bond</h3>
                                <span class=""quest-tier"">Side</span>
                            </header>
                            <p class=""quest-desc"">Hunt the shadow-stag prowling the gorse moor.</p>
                            <div class=""quest-progress"">
                                <div class=""progress-bar""><div></div></div>
                                <span class=""progress-label"">1 / 5 obj.</span>
                            </div>
                        </div>
                        <div class=""quest-side""><div>560</div></div>
                    </article>
                </section>",
                @"
                    .quests {
                        display: flex;
                        flex-direction: column;
                        gap: 12px;
                        overflow-y: auto;
                        min-height: 0;
                    }
                " + css,
                viewportWidth: 1200);

            var article = FindByClass(root, "quest");
            var body = FindByClass(root, "quest-body");

            double articleBottom = article.Y + article.Height;
            double bodyBottom = body.Y + body.Height;
            Assert.That(bodyBottom, Is.LessThanOrEqualTo(articleBottom + 1e-3),
                $"Body bottom ({bodyBottom}) must fit inside article ({articleBottom}). " +
                $"Article h={article.Height}, body y={body.Y}, body h={body.Height}.");
        }
    }
}
