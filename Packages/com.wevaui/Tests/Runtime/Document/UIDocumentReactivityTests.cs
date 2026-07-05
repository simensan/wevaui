using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Weva.Binding;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Paint;
using Weva.Paint.Images;
#if WEVA_URP_BATCHER_TESTS
using Weva.Rendering;
using Weva.Rendering.URP;
#endif

namespace Weva.Tests.Documents {
    public class UIDocumentReactivityTests {
        sealed class XpController {
            [UIBind] public string XpPercent = "0%";
            [UIBind] public string XpFillStyle = "transform: scaleX(0)";
            [UIBind] public string XpDebugText = "XP 0/0";
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

        sealed class StubImageSource : IImageSource {
            public int Width => 16;
            public int Height => 16;
        }

        static UIDocumentState NewState() {
            return new UIDocumentBuilder {
                DocumentSource = "<main><p class='hello'>hi</p><p>two</p><span>three</span></main>",
                StylesheetSources = new List<string> { "p { color: red; } span { color: blue; }" },
                MediaContext = MediaContext.Default(800, 600)
            }.Build();
        }

        [Test]
        public void Idle_loop_produces_pure_cascade_cache_hits_after_warmup() {
            var s = NewState();
            UIDocumentLifecycle.Update(s, null, 0.0);
            UIDocumentLifecycle.Update(s, null, 0.016);  // settle

            s.Cascade.ResetCacheStats();
            s.LayoutEngine.ResetCacheStats();
            s.Painter.ResetCacheStats();

            UIDocumentLifecycle.Update(s, null, 0.032);
            UIDocumentLifecycle.Update(s, null, 0.048);

            // No mutations -> the cascade should not have been queried at all
            // (idle frames skip layout, which is what triggers cascade lookups
            // through the styleOf delegate). The misses count must stay 0.
            Assert.That(s.Cascade.CacheMisses, Is.EqualTo(0));
            Assert.That(s.LayoutEngine.CacheMisses, Is.EqualTo(0));
            Assert.That(s.Painter.CacheMisses, Is.EqualTo(0));
        }

        [Test]
        public void Mutation_then_settle_returns_to_zero_misses() {
            var s = NewState();
            UIDocumentLifecycle.Update(s, null, 0.0);

            Weva.Dom.Element first = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { first = e; break; }
            first.SetAttribute("class", "changed");

            UIDocumentLifecycle.Update(s, null, 0.016);

            s.Cascade.ResetCacheStats();
            s.LayoutEngine.ResetCacheStats();
            s.Painter.ResetCacheStats();

            UIDocumentLifecycle.Update(s, null, 0.032);
            Assert.That(s.LayoutEngine.CacheMisses, Is.EqualTo(0));
        }

        [Test]
        public void Bound_inline_percent_width_updates_absolute_fill_geometry() {
            var controller = new XpController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='dbg'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div id='fill' class='xp-fill' style='width: {{ XpPercent }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.xp-bar{position:relative;width:100%;height:6px;overflow:hidden}" +
                    ".xp-fill{position:absolute;top:0;left:0;height:100%;background:#60a5fa}"
                },
                MediaContext = MediaContext.Default(400, 200),
                Controller = controller
            }.Build();

            UIDocumentLifecycle.Update(state, controller, 0.0);

            controller.XpPercent = "50%";
            controller.XpDebugText = "XP 5/10";
            UIDocumentLifecycle.Update(state, controller, 0.016);

            var fill = state.Doc.GetElementById("fill");
            var fillBox = state.ElementToBox.Lookup(fill);
            Assert.That(fillBox, Is.Not.Null);
            Assert.That(fillBox.Width, Is.EqualTo(100).Within(0.001));

            controller.XpPercent = "25%";
            controller.XpDebugText = "XP 1/4";
            UIDocumentLifecycle.Update(state, controller, 0.032);

            fillBox = state.ElementToBox.Lookup(fill);
            Assert.That(fillBox.Width, Is.EqualTo(50).Within(0.001));
        }

#if WEVA_URP_BATCHER_TESTS
        [Test]
        public void Bound_inline_percent_width_updates_batched_urp_geometry() {
            var controller = new XpController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='dbg'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div id='fill' class='xp-fill' style='width: {{ XpPercent }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.xp-bar{position:relative;width:100%;height:6px;overflow:hidden}" +
                    ".xp-fill{position:absolute;top:0;left:0;height:6px;background:#60a5fa}"
                },
                MediaContext = MediaContext.Default(400, 200),
                Controller = controller
            }.Build();
            var backend = new BatchedURPRenderBackend();

            AssertBatchedXpWidth(state, controller, backend, 0.000, "50%", "XP 5/10", 100f);
            AssertBatchedXpWidth(state, controller, backend, 0.016, "0%", "XP 0/25", 0f);
            AssertBatchedXpWidth(state, controller, backend, 0.032, "28%", "XP 7/25", 56f);
        }

        [Test]
        public void Bound_inline_percent_width_repaints_through_render_graph_source_cache() {
            UIPaintSourceRegistry.Clear();
            var controller = new XpController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='dbg'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div id='fill' class='xp-fill' style='width: {{ XpPercent }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.xp-bar{position:relative;width:100%;height:6px;overflow:hidden}" +
                    ".xp-fill{position:absolute;top:0;left:0;height:6px;background:linear-gradient(90deg,#3b82f6,#60a5fa)}"
                },
                MediaContext = MediaContext.Default(400, 200),
                Controller = controller
            }.Build();

            var go = new GameObject("uidocument-rendergraph-xp-test");
            var resources = new UIBatchedResources();
            UIRenderGraphPass pass = null;
            try {
                var doc = go.AddComponent<WevaDocument>();
                typeof(WevaDocument).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(doc, state);
                typeof(WevaDocument).GetField("controller", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(doc, controller);
                UIPaintSourceRegistry.Register(doc);

                var backend = new BatchedURPRenderBackend();
                pass = new UIRenderGraphPass(backend, resources);

                AssertRenderGraphXpWidth(doc, state, controller, pass, backend, 0.000, "50%", "XP 5/10", 100f);
                Assert.That(doc.NeedsRepaint, Is.False);

                AssertRenderGraphXpWidth(doc, state, controller, pass, backend, 0.016, "0%", "XP 0/25", 0f);
                Assert.That(doc.NeedsRepaint, Is.False);

                AssertRenderGraphXpWidth(doc, state, controller, pass, backend, 0.032, "28%", "XP 7/25", 56f);
                Assert.That(doc.NeedsRepaint, Is.False);
            } finally {
                UIPaintSourceRegistry.Clear();
                pass?.Dispose();
                resources.Dispose();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Bound_inline_percent_width_repaints_after_document_disable_resume() {
            UIPaintSourceRegistry.Clear();
            var controller = new XpController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='dbg'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div id='fill' class='xp-fill' style='width: {{ XpPercent }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.xp-bar{position:relative;width:100%;height:6px;overflow:hidden}" +
                    ".xp-fill{position:absolute;top:0;left:0;height:6px;background:linear-gradient(90deg,#3b82f6,#60a5fa)}"
                },
                MediaContext = MediaContext.Default(400, 200),
                Controller = controller
            }.Build();

            var go = new GameObject("uidocument-rendergraph-resume-xp-test");
            var resources = new UIBatchedResources();
            UIRenderGraphPass pass = null;
            try {
                var doc = go.AddComponent<WevaDocument>();
                typeof(WevaDocument).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(doc, state);
                typeof(WevaDocument).GetField("controller", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(doc, controller);
                UIPaintSourceRegistry.Register(doc);

                var backend = new BatchedURPRenderBackend();
                pass = new UIRenderGraphPass(backend, resources);

                AssertRenderGraphXpWidth(doc, state, controller, pass, backend, 0.000, "50%", "XP 5/10", 100f);

                go.SetActive(false);
                controller.XpPercent = "0%";
                controller.XpDebugText = "XP 0/25";
                go.SetActive(true);

                AssertRenderGraphXpWidth(doc, state, controller, pass, backend, 0.016, "0%", "XP 0/25", 0f);
                Assert.That(doc.NeedsRepaint, Is.False);

                AssertRenderGraphXpWidth(doc, state, controller, pass, backend, 0.032, "28%", "XP 7/25", 56f);
                Assert.That(doc.NeedsRepaint, Is.False);
            } finally {
                UIPaintSourceRegistry.Clear();
                pass?.Dispose();
                resources.Dispose();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Bound_inline_percent_width_repaints_after_real_document_unregister_and_resume() {
            UIPaintSourceRegistry.Clear();
            var controller = new XpController();
            var hudGo = new GameObject("real-uidocument-hud-resume-test");
            var overlayGo = new GameObject("real-uidocument-overlay-test");
            var resources = new UIBatchedResources();
            UIRenderGraphPass pass = null;
            try {
                var hudDoc = CreateRuntimeDocument(hudGo, controller,
                    "<main class='hud'><span id='dbg'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div id='fill' class='xp-fill' style='width: {{ XpPercent }}'></div></div></main>",
                    ".hud{width:200px}.xp-bar{position:relative;width:100%;height:6px;overflow:hidden}" +
                    ".xp-fill{position:absolute;top:0;left:0;height:6px;background:linear-gradient(90deg,#3b82f6,#60a5fa)}");

                var overlayController = new object();
                CreateRuntimeDocument(overlayGo, overlayController,
                    "<main class='overlay'><div class='panel'></div></main>",
                    ".overlay{width:320px;height:180px}.panel{width:100%;height:100%;background:rgba(0,0,0,.5)}");

                var backend = new BatchedURPRenderBackend();
                pass = new UIRenderGraphPass(backend, resources);

                AssertRealDocumentXpWidth(hudDoc, controller, pass, backend, "50%", "XP 5/10", 100f);

                hudGo.SetActive(false);
                controller.XpPercent = "0%";
                controller.XpDebugText = "XP 0/25";
                pass.EmitAllPaintSources();
                Assert.That(UIPaintSourceRegistry.Count, Is.EqualTo(1), "disabled HUD document must unregister while overlay remains");

                hudGo.SetActive(true);
                hudDoc.SetController(controller);
                AssertRealDocumentXpWidth(hudDoc, controller, pass, backend, "0%", "XP 0/25", 0f);

                AssertRealDocumentXpWidth(hudDoc, controller, pass, backend, "28%", "XP 7/25", 56f);
            } finally {
                UIPaintSourceRegistry.Clear();
                pass?.Dispose();
                resources.Dispose();
                Object.DestroyImmediate(hudGo);
                Object.DestroyImmediate(overlayGo);
            }
        }

        [Test]
        public void Cooldown_bindings_keep_batched_skill_icon_geometry_stable() {
            var tex = new Texture2D(16, 16);
            try {
                var registry = new InMemoryImageRegistry();
                registry.Register("skill-icon", new Texture2DImageSource(tex));
                var controller = new XpController {
                    ActiveSkill = new SkillSlot {
                        IconPath = "skill-icon",
                        LevelText = "",
                        HideLevel = true,
                        CooldownText = "",
                        CooldownSweepStyle = "background: transparent",
                        IsOnCooldown = false,
                        IsActive = false
                    }
                };
                var state = new UIDocumentBuilder {
                    DocumentSource =
                        "<div class='hud-root'><div class='hud-bottom'><div class='skill-bar'>" +
                        "<div class='skill-group skills-active'>" +
                        "<div id='slot' class='skill-slot active' data-class-on-cooldown='ActiveSkill.IsOnCooldown' data-class-active-now='ActiveSkill.IsActive'>" +
                        "<img id='icon' class='skill-slot-icon' src='{{ ActiveSkill.IconPath }}' />" +
                        "<div class='skill-slot-cd-sweep' style='{{ ActiveSkill.CooldownSweepStyle }}'></div>" +
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
                    Controller = controller,
                    ImageRegistry = registry
                }.Build();
                var backend = new BatchedURPRenderBackend { ImageRegistry = registry };

                var ready = AssertBatchedSkillIcon(state, controller, backend, 0.000, false, false, "", "background: transparent");
                var active = AssertBatchedSkillIcon(state, controller, backend, 0.016, true, true, "8",
                    "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)");
                var ticking = AssertBatchedSkillIcon(state, controller, backend, 0.032, true, false, "7",
                    "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 252deg, rgba(255, 255, 255, 0.42) 252deg, rgba(255, 255, 255, 0.18) 360deg)");
                var readyAgain = AssertBatchedSkillIcon(state, controller, backend, 0.048, false, false, "", "background: transparent");

                AssertSameImageRect(ready, active);
                AssertSameImageRect(ready, ticking);
                AssertSameImageRect(ready, readyAgain);
            } finally {
                Object.DestroyImmediate(tex);
            }
        }

        // REACT-1 fixed 2026-06-06. ClassBinding was preemptively marking
        // InvalidationKind.Structure on every class change, which caused
        // TryLayoutSubtree's first guard (HasAny(Structure) → return false) to
        // fire unconditionally — even for paint-only class toggles like
        // border-color / box-shadow. The fix removes Structure from ClassBinding's
        // invalidation mark; the cascade engine (CascadeEngine.ComputeOrHit /
        // ApplyLayoutInvalidation) adds Structure back if the class flip crosses a
        // display: none ↔ shown boundary, which runs before Layout() is called so
        // the guard still fires correctly for structural changes.
        [Test]
        public void Active_cooldown_does_not_shift_passive_skill_icons_in_shared_flex_bar() {
            var passiveTex = new Texture2D(16, 16);
            var activeTex = new Texture2D(16, 16);
            var statTex = new Texture2D(16, 16);
            try {
                var registry = new InMemoryImageRegistry();
                registry.Register("passive-a", new Texture2DImageSource(passiveTex));
                registry.Register("passive-b", new Texture2DImageSource(passiveTex));
                registry.Register("active-icon", new Texture2DImageSource(activeTex));
                registry.Register("ultimate-icon", new Texture2DImageSource(activeTex));
                registry.Register("stat-icon", new Texture2DImageSource(statTex));

                var controller = new XpController {
                    ActiveSkill = new SkillSlot {
                        IconPath = "active-icon",
                        LevelText = "",
                        HideLevel = true,
                        CooldownText = "",
                        CooldownSweepStyle = "background: transparent",
                        IsOnCooldown = false,
                        IsActive = false
                    }
                };
                controller.PassiveSkills.Add(new SkillSlot {
                    Id = "passive-a",
                    IconPath = "passive-a",
                    LevelText = "2",
                    HideLevel = false
                });
                controller.PassiveSkills.Add(new SkillSlot {
                    Id = "passive-b",
                    IconPath = "passive-b",
                    LevelText = "1",
                    HideLevel = false
                });

                var state = new UIDocumentBuilder {
                    DocumentSource =
                        "<div class='hud-root'><div class='hud-bottom'><div class='skill-bar'>" +
                        "<span class='level-badge'>{{ XpDebugText }}</span>" +
                        "<div class='skill-group skills-passive'>" +
                        "<template data-each='PassiveSkills as skill' data-key='Id'>" +
                        "<div class='skill-slot passive' data-class-on-cooldown='skill.IsOnCooldown'>" +
                        "<img class='skill-slot-icon' src='{{ skill.IconPath }}' />" +
                        "<span class='skill-slot-level' data-class-hidden='skill.HideLevel'>{{ skill.LevelText }}</span>" +
                        "</div>" +
                        "</template>" +
                        "</div>" +
                        "<div class='skill-group skills-active'>" +
                        "<div class='skill-slot active' data-class-on-cooldown='ActiveSkill.IsOnCooldown' data-class-active-now='ActiveSkill.IsActive'>" +
                        "<img class='skill-slot-icon' src='{{ ActiveSkill.IconPath }}' />" +
                        "<div class='skill-slot-cd-sweep' style='{{ ActiveSkill.CooldownSweepStyle }}'></div>" +
                        "<span class='skill-slot-key'>E</span>" +
                        "<span class='skill-slot-level' data-class-hidden='ActiveSkill.HideLevel'>{{ ActiveSkill.LevelText }}</span>" +
                        "<span class='skill-slot-cd-text'>{{ ActiveSkill.CooldownText }}</span>" +
                        "</div>" +
                        "<div class='skill-slot ultimate'>" +
                        "<img class='skill-slot-icon' src='ultimate-icon' />" +
                        "<span class='skill-slot-key'>R</span>" +
                        "</div>" +
                        "</div>" +
                        "<div class='skill-group stats-group'>" +
                        "<div class='stat-slot'><img class='stat-slot-icon' src='stat-icon' /></div>" +
                        "</div>" +
                        "</div></div></div>",
                    StylesheetSources = new List<string> {
                        "*{box-sizing:border-box}" +
                        ".hud-root{position:fixed;inset:0;display:flex;flex-direction:column;justify-content:space-between;font-family:sans-serif;color:#f3f4f6}" +
                        ".hud-bottom{display:flex;flex-direction:column;gap:0;width:100%;padding:0 24px 16px}" +
                        ".skill-bar{display:flex;align-items:flex-end;justify-content:center;gap:20px}" +
                        ".skill-group{display:flex;align-items:flex-end;gap:4px}" +
                        ".level-badge{padding:3px 10px;background:rgba(10,14,22,.85);border:1px solid rgba(96,165,250,.4);border-radius:4px;font-size:13px;font-weight:900;white-space:nowrap}" +
                        ".skill-slot{position:relative;width:52px;height:52px;border-radius:6px;overflow:hidden;background:rgba(10,14,22,.8);border:2px solid rgba(255,255,255,.25)}" +
                        ".skill-slot.active,.skill-slot.ultimate{width:60px;height:60px;border-color:rgba(255,255,255,.4)}" +
                        ".skill-slot.on-cooldown{border-color:rgba(255,255,255,.15)}" +
                        ".skill-slot.on-cooldown .skill-slot-icon{opacity:1}" +
                        ".skill-slot.active-now{border-color:#22c55e;box-shadow:0 0 10px rgba(34,197,94,.4)}" +
                        ".skill-slot.passive{width:44px;height:44px;border-color:rgba(255,255,255,.2)}" +
                        ".skill-slot-icon{position:absolute;inset:0;display:block;width:100%;height:100%;object-fit:cover;object-position:center;background:#1c2435;pointer-events:none}" +
                        ".skill-slot-cd-sweep{position:absolute;inset:0;background:transparent;pointer-events:none}" +
                        ".skill-slot-key{position:absolute;bottom:2px;left:4px;font-size:10px;font-weight:900}" +
                        ".skill-slot-level{position:absolute;top:2px;right:4px;font-size:10px;font-weight:900}" +
                        ".skill-slot-level.hidden{display:none}" +
                        ".skill-slot-cd-text{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:900;font-family:monospace;font-variant-numeric:tabular-nums;line-height:1;text-align:center}" +
                        ".stat-slot{position:relative;width:40px;height:40px;border-radius:4px;background:rgba(10,14,22,.7);border:1px solid rgba(255,255,255,.15);overflow:hidden}" +
                        ".stat-slot-icon{position:absolute;inset:0;display:block;width:100%;height:100%;object-fit:cover;object-position:center;background:#1c2435}"
                    },
                    MediaContext = MediaContext.Default(1225, 677),
                    Controller = controller,
                    ImageRegistry = registry
                }.Build();
                var backend = new BatchedURPRenderBackend { ImageRegistry = registry };

                var ready = AssertBatchedSkillIcon(state, controller, backend, 0.000,
                    "passive-a", false, false, "", "background: transparent");
                state.LayoutEngine.ResetSubtreeSkipStats();
                var cooling = AssertBatchedSkillIcon(state, controller, backend, 0.016,
                    "passive-a", true, true, "8",
                    "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)");
                Assert.That(state.LayoutEngine.SubtreeSkipHits, Is.GreaterThan(0),
                    "paint-only cooldown class/style/text changes should not force a full shared flex-row rebuild");
                var readyAgain = AssertBatchedSkillIcon(state, controller, backend, 0.032,
                    "passive-a", false, false, "", "background: transparent");

                AssertSameImageRect(ready, cooling);
                AssertSameImageRect(ready, readyAgain);
            } finally {
                Object.DestroyImmediate(passiveTex);
                Object.DestroyImmediate(activeTex);
                Object.DestroyImmediate(statTex);
            }
        }
#endif

        [Test]
        public void Bound_inline_transform_updates_after_levelup_reset() {
            var controller = new XpController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main class='hud'><span id='dbg'>{{ XpDebugText }}</span>" +
                    "<div id='bar' class='xp-bar'><div id='fill' class='xp-fill' style='{{ XpFillStyle }}'></div></div></main>",
                StylesheetSources = new List<string> {
                    ".hud{width:200px}.xp-bar{width:100%;height:6px;overflow:hidden}" +
                    ".xp-fill{display:block;width:100%;height:6px;background:#60a5fa;transform:scaleX(0);transform-origin:left center}"
                },
                MediaContext = MediaContext.Default(400, 200),
                Controller = controller
            }.Build();

            AssertScaleAfterUpdate(state, controller, 0.000, "transform: scaleX(0.5)", "XP 5/10", 0.5);
            AssertScaleAfterUpdate(state, controller, 0.016, "transform: scaleX(0)", "XP 0/25", 0.0);
            AssertScaleAfterUpdate(state, controller, 0.032, "transform: scaleX(0.0123)", "XP 1/81", 0.0123);
            AssertScaleAfterUpdate(state, controller, 0.048, "transform: scaleX(0.0246)", "XP 2/81", 0.0246);
        }

        [Test]
        public void Nested_cooldown_bindings_update_when_same_view_model_mutates() {
            var controller = new XpController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<main><div id='slot'><div id='sweep' class='sweep' style='{{ ActiveSkill.CooldownSweepStyle }}'></div>" +
                    "<span id='text'>{{ ActiveSkill.CooldownText }}</span></div></main>",
                StylesheetSources = new List<string> {
                    "main{width:80px}.sweep{display:block;width:60px;height:60px}#text{display:block;width:20px;height:16px}"
                },
                MediaContext = MediaContext.Default(100, 100),
                Controller = controller
            }.Build();

            AssertCooldownAfterUpdate(state, controller, controller.ActiveSkill, 0.000, "8",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.8);
            AssertCooldownAfterUpdate(state, controller, controller.ActiveSkill, 0.016, "7",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 252deg, rgba(255, 255, 255, 0.42) 252deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.7);
            AssertCooldownAfterUpdate(state, controller, controller.ActiveSkill, 0.032, "",
                "background: transparent",
                double.NaN);
        }

        [Test]
        public void Repeated_cooldown_bindings_update_when_same_item_mutates() {
            var controller = new XpController();
            var slot = new SkillSlot { Id = "passive_0" };
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
                Controller = controller
            }.Build();

            AssertCooldownAfterUpdate(state, controller, slot, 0.000, "8",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 288deg, rgba(255, 255, 255, 0.42) 288deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.8);
            AssertCooldownAfterUpdate(state, controller, slot, 0.016, "7",
                "background: conic-gradient(from 0deg, rgba(2, 5, 12, 0.92) 0deg, rgba(6, 10, 22, 0.86) 252deg, rgba(255, 255, 255, 0.42) 252deg, rgba(255, 255, 255, 0.18) 360deg)",
                0.7);
            AssertCooldownAfterUpdate(state, controller, slot, 0.032, "",
                "background: transparent",
                double.NaN);
        }

        [Test]
        public void Versioned_image_registry_mutation_marks_document_repaint_needed() {
            var registry = new InMemoryImageRegistry();
            var state = new UIDocumentBuilder {
                DocumentSource = "<main><img src='icon'></main>",
                StylesheetSources = new List<string> { "main{width:100px;height:100px}img{width:16px;height:16px}" },
                MediaContext = MediaContext.Default(100, 100),
                ImageRegistry = registry
            }.Build();
            UIDocumentLifecycle.Update(state, null, 0);

            var go = new GameObject("uidocument-repaint-test");
            try {
                var doc = go.AddComponent<WevaDocument>();
                var field = typeof(WevaDocument).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null);
                field.SetValue(doc, state);

                state.HasEmittedPaint = true;
                state.PaintInvalidated = false;
                state.Invalidation.Clear();
                state.LastPaintedImageRegistryVersion = registry.Version;

                Assert.That(doc.NeedsRepaint, Is.False);
                registry.Register("icon", new StubImageSource());
                Assert.That(doc.NeedsRepaint, Is.True);
            } finally {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Render_viewport_reconciles_document_layout_before_paint() {
            var state = new UIDocumentBuilder {
                DocumentSource = "<main id='app'></main>",
                StylesheetSources = new List<string> { "#app{width:100vw;height:10px;background:#fff}" },
                MediaContext = MediaContext.Default(1920, 1080)
            }.Build();
            UIDocumentLifecycle.Update(state, null, 0);

            var app = state.Doc.GetElementById("app");
            Assert.That(state.ElementToBox.Lookup(app).Width, Is.EqualTo(1920).Within(0.001));

            var go = new GameObject("uidocument-render-viewport-test");
            try {
                var doc = go.AddComponent<WevaDocument>();
                typeof(WevaDocument).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(doc, state);

                state.HasEmittedPaint = true;
                state.PaintInvalidated = false;
                state.Invalidation.Clear();

                doc.PrepareForRenderViewport(1677, 883);

                Assert.That(state.LayoutContext.ViewportWidthPx, Is.EqualTo(1677).Within(0.001));
                Assert.That(state.LayoutContext.ViewportHeightPx, Is.EqualTo(883).Within(0.001));
                Assert.That(state.ElementToBox.Lookup(app).Width, Is.EqualTo(1677).Within(0.001));
                Assert.That(doc.NeedsRepaint, Is.True);
            } finally {
                Object.DestroyImmediate(go);
            }
        }

        static void AssertScaleAfterUpdate(UIDocumentState state, XpController controller,
                                           double t, string style, string debug, double expectedScaleX) {
            controller.XpFillStyle = style;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            var fill = state.Doc.GetElementById("fill");
            Assert.That(fill.GetAttribute("style"), Is.EqualTo(style));

            var list = state.Painter.Convert(state.RootBox, state.Invalidation,
                state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                double scaleX = 1.0;
                for (int i = 0; i < list.Commands.Count; i++) {
                    if (list.Commands[i] is PushTransformCommand push) {
                        scaleX = push.Transform.A;
                        break;
                    }
                }
                Assert.That(scaleX, Is.EqualTo(expectedScaleX).Within(0.0001));
            } finally {
                state.Painter.Return(list);
            }
        }

#if WEVA_URP_BATCHER_TESTS
        static void AssertBatchedXpWidth(UIDocumentState state, XpController controller, BatchedURPRenderBackend backend,
                                         double t, string percent, string debug, float expectedWidth) {
            controller.XpPercent = percent;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            var list = state.Painter.Convert(state.RootBox, state.Invalidation,
                state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                backend.BeginFrame();
                ((IRenderBackend)backend).Submit(list);
                backend.EndFrame();

                float width = FindXpFillBatchWidth(backend.Batcher, expectedWidth);
                Assert.That(width, Is.EqualTo(expectedWidth).Within(1.1f), DescribeBatches(backend.Batcher));
            } finally {
                state.Painter.Return(list);
            }
        }

        static void AssertRenderGraphXpWidth(WevaDocument doc, UIDocumentState state, XpController controller,
                                             UIRenderGraphPass pass, BatchedURPRenderBackend backend,
                                             double t, string percent, string debug, float expectedWidth) {
            controller.XpPercent = percent;
            controller.XpDebugText = debug;
            UIDocumentLifecycle.Update(state, controller, t);

            Assert.That(doc.NeedsRepaint, Is.True, "controller binding update must make WevaDocument visible to the retained render pass");
            pass.EmitAllPaintSources();

            float width = FindXpFillBatchWidth(backend.Batcher, expectedWidth);
            Assert.That(width, Is.EqualTo(expectedWidth).Within(1.1f), DescribeBatches(backend.Batcher));
        }

        static WevaDocument CreateRuntimeDocument(GameObject go, object controller, string html, string css) {
            go.SetActive(false);
            var doc = go.AddComponent<WevaDocument>();
            SetPrivateField(doc, "documentAsset", new TextAsset(html));
            SetPrivateField(doc, "stylesheetAssets", new[] { new TextAsset(css) });
            go.SetActive(true);
            doc.SetController(controller);
            InvokeDocumentUpdate(doc);
            return doc;
        }

        static void AssertRealDocumentXpWidth(WevaDocument doc, XpController controller, UIRenderGraphPass pass,
                                              BatchedURPRenderBackend backend, string percent, string debug,
                                              float expectedWidth) {
            controller.XpPercent = percent;
            controller.XpDebugText = debug;
            Assert.That(GetPrivateField(doc, "controller"), Is.SameAs(controller));
            Assert.That(doc.Bindings, Is.Not.Null);
            Assert.That(doc.Bindings.AttributeBindings.Count, Is.GreaterThan(0));
            InvokeDocumentUpdate(doc);

            var fill = doc.GetElementById("fill");
            Assert.That(fill, Is.Not.Null);
            Assert.That(fill.GetAttribute("style"), Is.EqualTo("width: " + percent));

            var fillBox = doc.CurrentState?.ElementToBox?.Lookup(fill);
            Assert.That(fillBox, Is.Not.Null);
            Assert.That((float)fillBox.Width, Is.EqualTo(expectedWidth).Within(0.001f));

            Assert.That(doc.NeedsRepaint, Is.True, "runtime WevaDocument resume/update must be visible to the retained render pass");
            pass.EmitAllPaintSources();

            float width = FindXpFillBatchWidth(backend.Batcher, expectedWidth);
            Assert.That(width, Is.EqualTo(expectedWidth).Within(1.1f), DescribeBatches(backend.Batcher));
            Assert.That(doc.NeedsRepaint, Is.False);
        }

        static Vector4 AssertBatchedSkillIcon(UIDocumentState state, XpController controller,
                                              BatchedURPRenderBackend backend, double t,
                                              bool onCooldown, bool activeNow, string cooldownText, string cooldownStyle) {
            return AssertBatchedSkillIcon(state, controller, backend, t,
                "skill-icon", onCooldown, activeNow, cooldownText, cooldownStyle);
        }

        static Vector4 AssertBatchedSkillIcon(UIDocumentState state, XpController controller,
                                              BatchedURPRenderBackend backend, double t,
                                              string imageHandle, bool onCooldown, bool activeNow,
                                              string cooldownText, string cooldownStyle) {
            controller.ActiveSkill.IsOnCooldown = onCooldown;
            controller.ActiveSkill.IsActive = activeNow;
            controller.ActiveSkill.CooldownText = cooldownText;
            controller.ActiveSkill.CooldownSweepStyle = cooldownStyle;
            UIDocumentLifecycle.Update(state, controller, t);

            var list = state.Painter.Convert(state.RootBox, state.Invalidation,
                state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                backend.BeginFrame();
                backend.SubtreeSnapshotSink = state.SubtreeSnapshotSink;
                ((IRenderBackend)backend).Submit(list);
                backend.SubtreeSnapshotSink = null;
                backend.EndFrame();
                return FindSkillIconBatchRect(backend.Batcher, imageHandle);
            } finally {
                backend.SubtreeSnapshotSink = null;
                state.Painter.Return(list);
            }
        }

        static Vector4 FindSkillIconBatchRect(UIBatcher batcher, string imageHandle) {
            for (int b = 0; b < batcher.Batches.Count; b++) {
                var batch = batcher.Batches[b];
                if (batch.Key.Brush != UIQuadBrush.Image || batch.Key.ImageHandle != imageHandle) continue;
                Assert.That(batch.Count, Is.GreaterThan(0));
                var inst = batch.Instances[0];
                return new Vector4(
                    inst.PosSize.x - inst.PosSize.z,
                    inst.PosSize.y - inst.PosSize.w,
                    inst.PosSize.z * 2f,
                    inst.PosSize.w * 2f);
            }
            Assert.Fail("Expected an image batch for " + imageHandle + ". " + DescribeBatches(batcher));
            return default;
        }

        static void AssertSameImageRect(Vector4 expected, Vector4 actual) {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f), "skill icon x shifted");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f), "skill icon y shifted");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.001f), "skill icon width changed");
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(0.001f), "skill icon height changed");
        }

        static void InvokeDocumentUpdate(WevaDocument doc) {
            var update = typeof(WevaDocument).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(update, Is.Not.Null);
            update.Invoke(doc, null);
        }

        static void SetPrivateField(object target, string name, object value) {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        static object GetPrivateField(object target, string name) {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            return field.GetValue(target);
        }

        static float FindXpFillBatchWidth(UIBatcher batcher, float expectedWidth) {
            float best = float.NaN;
            float bestDelta = float.PositiveInfinity;
            for (int b = 0; b < batcher.Batches.Count; b++) {
                var batch = batcher.Batches[b];
                for (int i = 0; i < batch.Count; i++) {
                    var inst = batch.Instances[i];
                    if (inst.PosSize.w > 4.1f) continue;
                    float width = inst.PosSize.z * 2f;
                    if (expectedWidth <= 0f && width <= 0.001f) return 0f;
                    if (width <= 0.001f) continue;
                    if (width >= 199f && expectedWidth < 199f) continue;
                    float delta = System.Math.Abs(width - expectedWidth);
                    if (delta < bestDelta) {
                        bestDelta = delta;
                        best = width;
                    }
                }
            }
            return expectedWidth <= 0f && float.IsNaN(best) ? 0f : best;
        }

        static string DescribeBatches(UIBatcher batcher) {
            var lines = new System.Text.StringBuilder();
            lines.Append("batches=").Append(batcher.Batches.Count);
            for (int b = 0; b < batcher.Batches.Count; b++) {
                var batch = batcher.Batches[b];
                lines.Append(" batch").Append(b).Append("(").Append(batch.Key.Brush).Append(" count=").Append(batch.Count).Append(")");
                for (int i = 0; i < batch.Count; i++) {
                    var inst = batch.Instances[i];
                    lines.Append(" [w=").Append(inst.PosSize.z * 2f)
                        .Append(",h=").Append(inst.PosSize.w * 2f)
                        .Append(",brush=").Append(inst.BrushParams.x)
                        .Append(",color=").Append(inst.Color)
                        .Append("]");
                }
            }
            return lines.ToString();
        }
#endif

        static void AssertCooldownAfterUpdate(UIDocumentState state, XpController controller, SkillSlot slot,
                                              double t, string text, string style, double expectedAngle) {
            slot.CooldownText = text;
            slot.CooldownSweepStyle = style;
            UIDocumentLifecycle.Update(state, controller, t);

            var list = state.Painter.Convert(state.RootBox, state.Invalidation,
                state.BoxLookup, state.LayoutEngine.ScrollContainer, state.State);
            try {
                Assert.That(FindFirstText(state.Doc), Is.EqualTo(text));
                double angle = double.NaN;
                for (int i = 0; i < list.Commands.Count; i++) {
                    if (list.Commands[i] is FillRectCommand fill
                        && fill.Brush != null
                        && fill.Brush.Kind == BrushKind.Gradient
                        && fill.Brush.GradientValue is ConicGradient conic
                        && conic.Stops != null
                        && conic.Stops.Count >= 2) {
                        angle = conic.Stops[1].Position;
                        break;
                    }
                }

                if (double.IsNaN(expectedAngle)) {
                    Assert.That(angle, Is.NaN);
                } else {
                    Assert.That(angle, Is.EqualTo(expectedAngle).Within(0.0001));
                }
            } finally {
                state.Painter.Return(list);
            }
        }

        static string FindFirstText(Weva.Dom.Node node) {
            if (node == null) return null;
            if (node is Weva.Dom.TextNode text) return text.Data;
            for (int i = 0; i < node.Children.Count; i++) {
                var found = FindFirstText(node.Children[i]);
                if (found != null) return found;
            }
            return null;
        }
    }
}
