using System;
using System.Collections.Generic;
using Weva.Binding;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;

namespace Weva.BaselineGen {
    static class ReactivePaintCheck {
        sealed class Controller {
            [UIBind] public string XpPercent = "0%";
            [UIBind] public string XpFillClass = "xp-pct-0";
            [UIBind] public string XpFillStyle = "transform: scaleX(0)";
            [UIBind] public string XpDebugText = "XP 0/0";
            [UIBind] public string CooldownSweepStyle = "background: transparent";
            [UIBind] public SkillSlot ActiveSkill = new();
            [UIBind] public IList<SkillSlot> PassiveSkills = new List<SkillSlot>();
        }

        sealed class SkillSlot {
            public string Id = "";
            public string IconPath = "";
            public string LevelText = "";
            public string CooldownText = "";
            public string CooldownSweepStyle = "background: transparent";
            public bool IsOnCooldown;
            public bool IsActive;
            public bool HideLevel = true;
        }

        public static int Run() {
            int failures = 0;
            failures += CheckBoundInlineWidthAfterReset();
            failures += CheckClassBoundTransformAfterReset();
            failures += CheckBoundInlineTransformAfterReset();
            failures += CheckBoundGradientWidthInFixedHudAfterReset();
            failures += CheckBoundConicGradientStyleUpdates();
            failures += CheckNestedCooldownBindingsUpdateOnSameObject();
            failures += CheckRepeatedCooldownBindingsUpdateOnSameObject();
            failures += CheckGameHudCooldownBindingsDoNotMoveSkillIcon();
            return failures;
        }

        static int CheckBoundInlineWidthAfterReset() {
            var controller = new Controller();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='debug'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='bar'><div id='fill' class='fill' style='width: {{ XpPercent }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.bar{width:100%;height:6px;background:#111}" +
                    ".fill{display:block;height:6px;background:#60a5fa}"
                },
                MediaContext = MediaContext.Default(400, 200),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += Step(state, controller, 0.000, "50%", "XP 5/10", 100.0);
            failures += Step(state, controller, 0.016, "0%", "XP 0/25", 0.0);
            failures += Step(state, controller, 0.032, "20%", "XP 5/25", 40.0);
            failures += Step(state, controller, 0.048, "28%", "XP 7/25", 56.0);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS bound inline width updates layout and paint after reset"
                : "[reactive-paint] FAIL bound inline width regression");
            return failures;
        }

        static int CheckBoundGradientWidthInFixedHudAfterReset() {
            var controller = new Controller();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<div class='hud-root'><div class='hud-bottom'>" +
                    "<span id='debug' class='xp-debug'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div class='xp-gain'></div>" +
                    "<div id='fill' class='xp-fill' style='width: {{ XpPercent }}'></div></div>" +
                    "</div></div>",
                StylesheetSources = new List<string> {
                    "*{box-sizing:border-box}" +
                    ".hud-root{position:fixed;inset:0;display:flex;flex-direction:column;justify-content:space-between}" +
                    ".hud-bottom{display:flex;flex-direction:column;gap:0;width:100%;padding:0 24px 16px}" +
                    ".xp-debug{align-self:center;margin-top:4px;font-size:10px;font-weight:800}" +
                    ".xp-bar{position:relative;align-self:stretch;width:100%;height:6px;min-height:6px;padding:0;background:rgba(255,255,255,.14);margin-top:10px;overflow:hidden}" +
                    ".xp-gain{display:none}" +
                    ".xp-fill{position:absolute;top:0;left:0;display:block;width:0%;height:6px;min-height:6px;background:linear-gradient(90deg,#3b82f6,#60a5fa)}"
                },
                MediaContext = MediaContext.Default(1225, 677),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += GradientStep(state, controller, 0.000, "50%", "XP 5/10", 588.5);
            failures += GradientStep(state, controller, 0.016, "0%", "XP 0/25", 0.0);
            failures += GradientStep(state, controller, 0.032, "4%", "XP 1/25", 47.08);
            failures += GradientStep(state, controller, 0.048, "28%", "XP 7/25", 329.56);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS game-HUD-shaped gradient XP bar updates after reset"
                : "[reactive-paint] FAIL game-HUD-shaped gradient XP bar regression");
            return failures;
        }

        static int CheckClassBoundTransformAfterReset() {
            var controller = new Controller();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='debug'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='bar'><div id='fill' class='fill {{ XpFillClass }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.bar{width:100%;height:6px;background:#111;overflow:hidden}" +
                    ".fill{display:block;width:100%;height:6px;background:#60a5fa;transform:scaleX(0);transform-origin:left center}" +
                    ".xp-pct-0{transform:scaleX(0)}" +
                    ".xp-pct-20{transform:scaleX(0.2)}" +
                    ".xp-pct-28{transform:scaleX(0.28)}" +
                    ".xp-pct-50{transform:scaleX(0.5)}"
                },
                MediaContext = MediaContext.Default(400, 200),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += TransformStep(state, controller, 0.000, "xp-pct-50", "XP 5/10", 0.5);
            failures += TransformStep(state, controller, 0.016, "xp-pct-0", "XP 0/25", 0.0);
            failures += TransformStep(state, controller, 0.032, "xp-pct-20", "XP 5/25", 0.2);
            failures += TransformStep(state, controller, 0.048, "xp-pct-28", "XP 7/25", 0.28);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS class-bound transform updates after reset"
                : "[reactive-paint] FAIL class-bound transform regression");
            return failures;
        }

        static int CheckBoundInlineTransformAfterReset() {
            var controller = new Controller();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='debug'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='bar'><div id='fill' class='fill' style='{{ XpFillStyle }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.bar{width:100%;height:6px;background:#111;overflow:hidden}" +
                    ".fill{display:block;width:100%;height:6px;background:#60a5fa;transform:scaleX(0);transform-origin:left center}"
                },
                MediaContext = MediaContext.Default(400, 200),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += TransformStyleStep(state, controller, 0.000, "transform: scaleX(0.5)", "XP 5/10", 0.5);
            failures += TransformStyleStep(state, controller, 0.016, "transform: scaleX(0)", "XP 0/25", 0.0);
            failures += TransformStyleStep(state, controller, 0.032, "transform: scaleX(0.0123)", "XP 1/81", 0.0123);
            failures += TransformStyleStep(state, controller, 0.048, "transform: scaleX(0.0246)", "XP 2/81", 0.0246);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS bound inline transform updates after reset"
                : "[reactive-paint] FAIL bound inline transform regression");
            return failures;
        }

        static int CheckBoundConicGradientStyleUpdates() {
            var controller = new Controller();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main><div id='slot'><div id='sweep' class='sweep' style='{{ CooldownSweepStyle }}'></div>" +
                    "<span id='debug'>{{ XpDebugText }}</span></div></main>",
                StylesheetSources = new List<string> {
                    "main{width:80px}.sweep{display:block;width:60px;height:60px}"
                },
                MediaContext = MediaContext.Default(100, 100),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += CooldownStep(state, controller, 0.000,
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 270deg, rgba(255, 255, 255, 0.42) 270deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.75);
            failures += CooldownStep(state, controller, 0.016,
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 180deg, rgba(255, 255, 255, 0.42) 180deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.5);
            failures += CooldownStep(state, controller, 0.032,
                "background: transparent",
                double.NaN);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS bound conic cooldown style updates"
                : "[reactive-paint] FAIL bound conic cooldown regression");
            return failures;
        }

        static int CheckNestedCooldownBindingsUpdateOnSameObject() {
            var controller = new Controller();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main><div id='slot'><div id='sweep' class='sweep' style='{{ ActiveSkill.CooldownSweepStyle }}'></div>" +
                    "<span id='text'>{{ ActiveSkill.CooldownText }}</span></div></main>",
                StylesheetSources = new List<string> {
                    "main{width:80px}.sweep{display:block;width:60px;height:60px}#text{display:block;width:20px;height:16px}"
                },
                MediaContext = MediaContext.Default(100, 100),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += NestedCooldownStep(state, controller, controller.ActiveSkill, 0.000, "8",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.8);
            failures += NestedCooldownStep(state, controller, controller.ActiveSkill, 0.016, "7",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 252deg, rgba(255, 255, 255, 0.42) 252deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.7);
            failures += NestedCooldownStep(state, controller, controller.ActiveSkill, 0.032, "",
                "background: transparent",
                double.NaN);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS nested cooldown bindings update on same object"
                : "[reactive-paint] FAIL nested cooldown binding regression");
            return failures;
        }

        static int CheckRepeatedCooldownBindingsUpdateOnSameObject() {
            var slot = new SkillSlot { Id = "passive_0" };
            var controller = new Controller();
            controller.PassiveSkills.Add(slot);
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main><template data-each='PassiveSkills as skill' data-key='Id'>" +
                    "<div class='slot'><div class='sweep' style='{{ skill.CooldownSweepStyle }}'></div>" +
                    "<span class='text'>{{ skill.CooldownText }}</span></div></template></main>",
                StylesheetSources = new List<string> {
                    "main{width:80px}.sweep{display:block;width:60px;height:60px}.text{display:block;width:20px;height:16px}"
                },
                MediaContext = MediaContext.Default(100, 100),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += NestedCooldownStep(state, controller, slot, 0.000, "8",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.8);
            failures += NestedCooldownStep(state, controller, slot, 0.016, "7",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 252deg, rgba(255, 255, 255, 0.42) 252deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.7);
            failures += NestedCooldownStep(state, controller, slot, 0.032, "",
                "background: transparent",
                double.NaN);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS repeated cooldown bindings update on same object"
                : "[reactive-paint] FAIL repeated cooldown binding regression");
            return failures;
        }

        static int CheckGameHudCooldownBindingsDoNotMoveSkillIcon() {
            var controller = new Controller();
            controller.ActiveSkill = new SkillSlot {
                IconPath = "skill-icon",
                LevelText = "",
                HideLevel = true,
                CooldownText = "",
                CooldownSweepStyle = "background: transparent",
                IsOnCooldown = false,
                IsActive = false
            };
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<div class='hud-root'><div class='hud-bottom'><div class='skill-bar'>" +
                    "<div class='skill-group skills-active'>" +
                    "<div id='slot' class='skill-slot active' data-class-on-cooldown='ActiveSkill.IsOnCooldown' data-class-active-now='ActiveSkill.IsActive'>" +
                    "<img id='icon' class='skill-slot-icon' src='{{ ActiveSkill.IconPath }}' />" +
                    "<div id='sweep' class='skill-slot-cd-sweep' style='{{ ActiveSkill.CooldownSweepStyle }}'></div>" +
                    "<span class='skill-slot-key'>E</span>" +
                    "<span class='skill-slot-level' data-class-hidden='ActiveSkill.HideLevel'>{{ ActiveSkill.LevelText }}</span>" +
                    "<span class='skill-slot-cd-text'>{{ ActiveSkill.CooldownText }}</span>" +
                    "</div></div></div></div></div>",
                StylesheetSources = new List<string> {
                    "*{box-sizing:border-box}" +
                    ".hud-root{position:fixed;inset:0;display:flex;flex-direction:column;justify-content:space-between;font-family:sans-serif;color:#f3f4f6}" +
                    ".hud-bottom{display:flex;flex-direction:column;gap:0;width:100%;padding:0 24px 16px}" +
                    ".skill-bar{display:flex;align-items:flex-end;justify-content:center;gap:20px}" +
                    ".skill-group{display:flex;align-items:flex-end;gap:4px}" +
                    ".skill-slot{position:relative;width:52px;height:52px;border-radius:6px;overflow:hidden;background:rgba(10,14,22,.8);border:2px solid rgba(255,255,255,.25)}" +
                    ".skill-slot.active{width:60px;height:60px;border-color:rgba(255,255,255,.4)}" +
                    ".skill-slot.on-cooldown{border-color:rgba(255,255,255,.15)}" +
                    ".skill-slot.on-cooldown .skill-slot-icon{opacity:1}" +
                    ".skill-slot.active-now{border-color:#22c55e;box-shadow:0 0 10px rgba(34,197,94,.4)}" +
                    ".skill-slot-icon{position:absolute;inset:0;display:block;width:100%;height:100%;object-fit:cover;object-position:center;background:#1c2435;pointer-events:none}" +
                    ".skill-slot-cd-sweep{position:absolute;inset:0;background:transparent;pointer-events:none}" +
                    ".skill-slot-key{position:absolute;bottom:2px;left:4px;font-size:10px;font-weight:900}" +
                    ".skill-slot-level{position:absolute;top:2px;right:4px;font-size:10px;font-weight:900}" +
                    ".skill-slot-level.hidden{display:none}" +
                    ".skill-slot-cd-text{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:900;font-family:monospace;font-variant-numeric:tabular-nums;line-height:1;text-align:center}"
                },
                MediaContext = MediaContext.Default(1225, 677),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            int failures = 0;
            failures += SkillIconStep(state, controller, 0.000, false, false, "", "background: transparent", out var firstBox, out var firstPaint);
            failures += SkillIconStep(state, controller, 0.016, true, true, "8",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)",
                out var cooldownBox, out var cooldownPaint);
            failures += SkillIconStep(state, controller, 0.032, true, false, "7",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 252deg, rgba(255, 255, 255, 0.42) 252deg, rgba(255, 255, 255, 0.18) 360deg)",
                out var tickBox, out var tickPaint);
            failures += SkillIconStep(state, controller, 0.048, false, false, "", "background: transparent", out var readyBox, out var readyPaint);

            failures += AssertSameRect("cooldown icon box", firstBox, cooldownBox);
            failures += AssertSameRect("tick icon box", firstBox, tickBox);
            failures += AssertSameRect("ready icon box", firstBox, readyBox);
            failures += AssertSameRect("cooldown icon paint", firstPaint, cooldownPaint);
            failures += AssertSameRect("tick icon paint", firstPaint, tickPaint);
            failures += AssertSameRect("ready icon paint", firstPaint, readyPaint);

            Console.WriteLine(failures == 0
                ? "[reactive-paint] PASS game-HUD cooldown bindings keep skill icon geometry stable"
                : "[reactive-paint] FAIL game-HUD cooldown icon geometry regression");
            return failures;
        }

        static int SkillIconStep(UIDocumentState state, Controller controller, double t,
                                 bool onCooldown, bool activeNow, string text, string style,
                                 out Rect iconBoxRect, out Rect iconPaintRect) {
            controller.ActiveSkill.IsOnCooldown = onCooldown;
            controller.ActiveSkill.IsActive = activeNow;
            controller.ActiveSkill.CooldownText = text;
            controller.ActiveSkill.CooldownSweepStyle = style;
            UIDocumentLifecycle.Update(state, controller, t);

            var icon = state.Doc.GetElementById("icon");
            var iconBox = state.ElementToBox.Lookup(icon);
            iconBoxRect = iconBox != null
                ? new Rect(iconBox.X, iconBox.Y, iconBox.Width, iconBox.Height)
                : new Rect(double.NaN, double.NaN, double.NaN, double.NaN);
            iconPaintRect = FindImagePaintRect(state, "skill-icon");
            Console.WriteLine($"[reactive-paint] skillCooldown on={onCooldown} active={activeNow} text='{text}' box={Fmt(iconBoxRect)} paint={Fmt(iconPaintRect)}");

            int failures = 0;
            if (FirstTextElement(state.Doc.GetElementById("slot"), "skill-slot-cd-text") != text) {
                Console.WriteLine($"  expected cooldown text: {text}");
                failures++;
            }
            return failures;
        }

        static int Step(UIDocumentState state, Controller controller, double t, string percent, string debug, double expectedWidth) {
            controller.XpPercent = percent;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            var fill = state.Doc.GetElementById("fill");
            var debugEl = state.Doc.GetElementById("debug");
            string renderedStyle = fill.GetAttribute("style");
            string renderedText = FirstText(debugEl);
            Box fillBox = state.ElementToBox.Lookup(fill);
            double boxWidth = fillBox?.Width ?? double.NaN;
            double paintWidth = FindFillPaintWidth(state);

            Console.WriteLine($"[reactive-paint] percent={percent} style='{renderedStyle}' text='{renderedText}' box={boxWidth:0.###} paint={paintWidth:0.###}");

            int failures = 0;
            if (renderedStyle != "width: " + percent) {
                Console.WriteLine($"  expected style width: {percent}");
                failures++;
            }
            if (renderedText != debug) {
                Console.WriteLine($"  expected debug text: {debug}");
                failures++;
            }
            if (!Nearly(boxWidth, expectedWidth)) {
                Console.WriteLine($"  expected box width: {expectedWidth}");
                failures++;
            }
            if (!Nearly(paintWidth, expectedWidth)) {
                Console.WriteLine($"  expected paint width: {expectedWidth}");
                failures++;
            }
            return failures;
        }

        static int GradientStep(UIDocumentState state, Controller controller, double t,
                                string percent, string debug, double expectedWidth) {
            controller.XpPercent = percent;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            var fill = state.Doc.GetElementById("fill");
            var debugEl = state.Doc.GetElementById("debug");
            string renderedStyle = fill.GetAttribute("style");
            string renderedText = FirstText(debugEl);
            Box fillBox = state.ElementToBox.Lookup(fill);
            double boxWidth = fillBox?.Width ?? double.NaN;
            double paintWidth = FindGradientFillPaintWidth(state);

            Console.WriteLine($"[reactive-paint] gradientPercent={percent} style='{renderedStyle}' text='{renderedText}' box={boxWidth:0.###} paint={paintWidth:0.###}");

            int failures = 0;
            if (renderedStyle != "width: " + percent) {
                Console.WriteLine($"  expected style width: {percent}");
                failures++;
            }
            if (renderedText != debug) {
                Console.WriteLine($"  expected debug text: {debug}");
                failures++;
            }
            if (!Nearly(boxWidth, expectedWidth)) {
                Console.WriteLine($"  expected box width: {expectedWidth}");
                failures++;
            }
            if (!Nearly(paintWidth, expectedWidth)) {
                Console.WriteLine($"  expected gradient paint width: {expectedWidth}");
                failures++;
            }
            return failures;
        }

        static int TransformStep(UIDocumentState state, Controller controller, double t, string className, string debug, double expectedScaleX) {
            controller.XpFillClass = className;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            var fill = state.Doc.GetElementById("fill");
            var debugEl = state.Doc.GetElementById("debug");
            string renderedClass = fill.GetAttribute("class");
            string renderedText = FirstText(debugEl);
            double scaleX = FindFirstTransformScaleX(state);

            Console.WriteLine($"[reactive-paint] class={className} renderedClass='{renderedClass}' text='{renderedText}' scaleX={scaleX:0.###}");

            int failures = 0;
            if (renderedClass != "fill " + className) {
                Console.WriteLine($"  expected class: fill {className}");
                failures++;
            }
            if (renderedText != debug) {
                Console.WriteLine($"  expected debug text: {debug}");
                failures++;
            }
            if (!Nearly(scaleX, expectedScaleX)) {
                Console.WriteLine($"  expected transform scaleX: {expectedScaleX}");
                failures++;
            }
            return failures;
        }

        static int TransformStyleStep(UIDocumentState state, Controller controller, double t, string style, string debug, double expectedScaleX) {
            controller.XpFillStyle = style;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            var fill = state.Doc.GetElementById("fill");
            var debugEl = state.Doc.GetElementById("debug");
            string renderedStyle = fill.GetAttribute("style");
            string renderedText = FirstText(debugEl);
            double scaleX = FindFirstTransformScaleX(state);

            Console.WriteLine($"[reactive-paint] transformStyle='{renderedStyle}' text='{renderedText}' scaleX={scaleX:0.####}");

            int failures = 0;
            if (renderedStyle != style) {
                Console.WriteLine($"  expected style: {style}");
                failures++;
            }
            if (renderedText != debug) {
                Console.WriteLine($"  expected debug text: {debug}");
                failures++;
            }
            if (!Nearly(scaleX, expectedScaleX)) {
                Console.WriteLine($"  expected transform scaleX: {expectedScaleX}");
                failures++;
            }
            return failures;
        }

        static int CooldownStep(UIDocumentState state, Controller controller, double t, string style, double expectedAngle) {
            controller.CooldownSweepStyle = style;
            controller.XpDebugText = "tick " + t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            UIDocumentLifecycle.Update(state, controller, t);

            var sweep = state.Doc.GetElementById("sweep");
            string renderedStyle = sweep.GetAttribute("style");
            double angle = FindConicStopAngle(state);
            bool expectedTransparent = double.IsNaN(expectedAngle);

            Console.WriteLine($"[reactive-paint] cooldownStyle='{renderedStyle}' conicStop={angle:0.###}");

            int failures = 0;
            if (renderedStyle != style) {
                Console.WriteLine("  expected rendered cooldown style to match bound style");
                failures++;
            }
            if (expectedTransparent) {
                if (!double.IsNaN(angle)) {
                    Console.WriteLine("  expected no conic gradient paint after transparent style");
                    failures++;
                }
            } else if (!Nearly(angle, expectedAngle)) {
                Console.WriteLine($"  expected conic stop angle: {expectedAngle}");
                failures++;
            }
            return failures;
        }

        static int NestedCooldownStep(UIDocumentState state, Controller controller, SkillSlot slot, double t,
                                      string text, string style, double expectedAngle) {
            slot.CooldownText = text;
            slot.CooldownSweepStyle = style;
            UIDocumentLifecycle.Update(state, controller, t);

            string renderedText = FindFirstText(state.Doc);
            double angle = FindConicStopAngle(state);
            bool expectedTransparent = double.IsNaN(expectedAngle);
            Console.WriteLine($"[reactive-paint] nestedCooldown text='{renderedText}' conicStop={angle:0.###}");

            int failures = 0;
            if (renderedText != text) {
                Console.WriteLine($"  expected cooldown text: {text}");
                failures++;
            }
            if (expectedTransparent) {
                if (!double.IsNaN(angle)) {
                    Console.WriteLine("  expected no conic gradient paint after transparent style");
                    failures++;
                }
            } else if (!Nearly(angle, expectedAngle)) {
                Console.WriteLine($"  expected conic stop angle: {expectedAngle}");
                failures++;
            }
            return failures;
        }

        static string FirstText(Element e) {
            if (e == null) return null;
            for (int i = 0; i < e.Children.Count; i++) {
                if (e.Children[i] is TextNode text) return text.Data;
            }
            return null;
        }

        static string FindFirstText(Node node) {
            if (node == null) return null;
            if (node is TextNode text) return text.Data;
            for (int i = 0; i < node.Children.Count; i++) {
                var found = FindFirstText(node.Children[i]);
                if (found != null) return found;
            }
            return null;
        }

        static string FirstTextElement(Node node, string className) {
            var element = FindElementByClass(node, className);
            return FirstText(element);
        }

        static Element FindElementByClass(Node node, string className) {
            if (node == null) return null;
            if (node is Element e) {
                foreach (var c in e.ClassList) {
                    if (c == className) return e;
                }
            }
            for (int i = 0; i < node.Children.Count; i++) {
                var found = FindElementByClass(node.Children[i], className);
                if (found != null) return found;
            }
            return null;
        }

        static Rect FindImagePaintRect(UIDocumentState state, string handle) {
            var list = state.Painter.Convert(state.RootBox, state.Invalidation, state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                var commands = list.Commands;
                for (int i = 0; i < commands.Count; i++) {
                    if (commands[i] is FillRectCommand fill
                        && fill.Brush != null
                        && fill.Brush.Kind == BrushKind.Image
                        && fill.Brush.ImageHandle == handle) {
                        return fill.Bounds;
                    }
                }
                return new Rect(double.NaN, double.NaN, double.NaN, double.NaN);
            } finally {
                state.Painter.Return(list);
            }
        }

        static int AssertSameRect(string label, Rect expected, Rect actual) {
            if (Nearly(expected.X, actual.X)
                && Nearly(expected.Y, actual.Y)
                && Nearly(expected.Width, actual.Width)
                && Nearly(expected.Height, actual.Height)) {
                return 0;
            }
            Console.WriteLine($"  {label} moved: expected {Fmt(expected)} actual {Fmt(actual)}");
            return 1;
        }

        static string Fmt(Rect r) {
            return $"({r.X:0.###},{r.Y:0.###},{r.Width:0.###},{r.Height:0.###})";
        }

        static double FindFillPaintWidth(UIDocumentState state) {
            var list = state.Painter.Convert(state.RootBox, state.Invalidation, state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                double best = double.NaN;
                var commands = list.Commands;
                for (int i = 0; i < commands.Count; i++) {
                    if (commands[i] is FillRectCommand fill
                        && fill.Brush != null
                        && fill.Brush.Kind == BrushKind.SolidColor
                        && fill.Brush.Color.B > 0.8f
                        && fill.Bounds.Height >= 5.5
                        && fill.Bounds.Height <= 6.5) {
                        best = fill.Bounds.Width;
                    }
                }
                return best;
            } finally {
                state.Painter.Return(list);
            }
        }

        static double FindGradientFillPaintWidth(UIDocumentState state) {
            var list = state.Painter.Convert(state.RootBox, state.Invalidation, state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                double best = double.NaN;
                var commands = list.Commands;
                for (int i = 0; i < commands.Count; i++) {
                    if (commands[i] is FillRectCommand fill
                        && fill.Brush != null
                        && fill.Brush.Kind == BrushKind.Gradient
                        && fill.Bounds.Height >= 5.5
                        && fill.Bounds.Height <= 6.5) {
                        best = fill.Bounds.Width;
                    }
                }
                return best;
            } finally {
                state.Painter.Return(list);
            }
        }

        static double FindFirstTransformScaleX(UIDocumentState state) {
            var list = state.Painter.Convert(state.RootBox, state.Invalidation, state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                var commands = list.Commands;
                for (int i = 0; i < commands.Count; i++) {
                    if (commands[i] is PushTransformCommand transform) {
                        return transform.Transform.A;
                    }
                }
                return 1.0;
            } finally {
                state.Painter.Return(list);
            }
        }

        static double FindConicStopAngle(UIDocumentState state) {
            var list = state.Painter.Convert(state.RootBox, state.Invalidation, state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                var commands = list.Commands;
                for (int i = 0; i < commands.Count; i++) {
                    if (commands[i] is FillRectCommand fill
                        && fill.Brush != null
                        && fill.Brush.Kind == BrushKind.Gradient
                        && fill.Brush.GradientValue is ConicGradient conic
                        && conic.Stops != null
                        && conic.Stops.Count >= 2) {
                        return conic.Stops[1].Position;
                    }
                }
                return double.NaN;
            } finally {
                state.Painter.Return(list);
            }
        }

        static bool Nearly(double a, double b) {
            if (double.IsNaN(a) || double.IsNaN(b)) return false;
            return Math.Abs(a - b) <= 0.01;
        }
    }
}
