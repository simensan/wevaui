using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Scrollbars Styling L1 §3 + CSS Overflow L3 §6.2 + CSS Overscroll Behavior L1 §3 —
    // cascade-level coverage for the four scrollbar / overscroll properties.
    //
    // Properties under test:
    //   scrollbar-width  (Scrollbars L1 §3.1) — auto|thin|none; non-inherited
    //   scrollbar-color  (Scrollbars L1 §3.2) — auto|<color>{2}; INHERITED per spec
    //   scrollbar-gutter (Overflow L3 §6.2)   — auto|stable[both-edges]; non-inherited
    //   overscroll-behavior shorthand + -x/-y longhands (Overscroll L1 §3) — non-inherited
    //
    // NOTE: scrollbar-color is registered `inherited: true` in CssProperties.cs which
    // correctly matches CSS Scrollbars L1 §3.2 ("Inherited: yes"). The task prompt says
    // "non-inherited" for all, but that contradicts the spec for scrollbar-color. Tests
    // here follow the actual spec and the registry registration.
    //
    // Layout / paint consequences are exercised in:
    //   - ScrollbarPaintI14bTests.cs (paint track colors, width → gutter)
    //   - ScrollbarsPropertiesTests.cs (overscroll-behavior runtime bubble semantics)
    //   - ScrollbarLayoutI14bTests.cs (scrollbar-gutter stable reservation)
    // This file covers only the cascade boundary: registration, initial values,
    // keyword round-trips, inheritance, !important, CSS-wide keywords, and
    // invalid-token recovery.
    public class ScrollbarOverscrollCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));
        static OriginatedStylesheet User(string s) => OriginatedStylesheet.User(Css(s));

        // ── Shared helpers ────────────────────────────────────────────────

        // Single div target — no parent, so inheritance semantics are isolated.
        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // Parent → child tree for inheritance tests.
        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChildUserAuthor(string userCss, string authorCss) {
            var doc = Html("<div id=\"p\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] { User(userCss), Author(authorCss) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ══════════════════════════════════════════════════════════════════
        // I.  scrollbar-width  (CSS Scrollbars L1 §3.1)
        //     Inherited: no   Initial: auto
        //     Values: auto | thin | none
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Scrollbar_width_is_registered() {
            // Registration check — CssProperties must know this property.
            var prop = CssProperties.Get("scrollbar-width");
            Assert.That(prop, Is.Not.Null, "scrollbar-width must be registered");
        }

        [Test]
        public void Scrollbar_width_initial_is_auto() {
            // CSS Scrollbars L1 §3.1: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_width_not_inherited_in_registry() {
            // CSS Scrollbars L1 §3.1: "Inherited: no".
            Assert.That(CssProperties.IsInherited("scrollbar-width"), Is.False,
                "scrollbar-width must be non-inherited per CSS Scrollbars L1 §3.1");
        }

        [Test]
        public void Scrollbar_width_auto_round_trips() {
            var cs = Compute("#x { scrollbar-width: auto; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_width_thin_round_trips() {
            // §3.1: `thin` means a narrower-than-UA-default scrollbar.
            var cs = Compute("#x { scrollbar-width: thin; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("thin"));
        }

        [Test]
        public void Scrollbar_width_none_round_trips() {
            // §3.1: `none` suppresses the scrollbar entirely while the element
            // remains scrollable via other means (keyboard, programmatic).
            var cs = Compute("#x { scrollbar-width: none; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("none"));
        }

        [Test]
        public void Scrollbar_width_does_not_inherit() {
            // Non-inherited: parent's `thin` must NOT leak to the child.
            var cs = ComputeChild("#p { scrollbar-width: thin; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("auto"),
                "scrollbar-width is non-inherited; child must see initial `auto`");
        }

        [Test]
        public void Scrollbar_width_important_wins_over_lower_priority() {
            // !important author rule beats a normal author rule of higher specificity.
            var doc = Html("<div id=\"x\" class=\"w\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div.w#x { scrollbar-width: thin; } " +
                       "* { scrollbar-width: none !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("none"),
                "!important on the lower-specificity rule must win");
        }

        [Test]
        public void Scrollbar_width_initial_keyword_resets_to_auto() {
            // CSS Cascade L5 §7.1: `initial` rolls back to the property's
            // spec-defined initial value (`auto`).
            var cs = Compute("#x { scrollbar-width: thin; scrollbar-width: initial; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_width_inherit_keyword_forces_parent_value() {
            // CSS Cascade L5 §7.2: `inherit` forces the parent's computed value
            // even on a non-inherited property.
            var cs = ComputeChild("#p { scrollbar-width: thin; } #x { scrollbar-width: inherit; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("thin"),
                "`inherit` must pull the parent's `thin` onto this non-inherited property");
        }

        [Test]
        public void Scrollbar_width_unset_on_non_inherited_resolves_to_initial() {
            // CSS Cascade L5 §7.3: `unset` on a non-inherited property == `initial`.
            var cs = Compute("#x { scrollbar-width: thin; scrollbar-width: unset; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("auto"),
                "`unset` on non-inherited scrollbar-width must resolve to initial `auto`");
        }

        [Test]
        public void Scrollbar_width_revert_falls_back_to_initial_with_no_ua_rule() {
            // CSS Cascade L5 §7.4: `revert` rolls back to the UA-origin value.
            // With no UA stylesheet in play the cascade has no UA value, so the
            // result is the property's initial value. V1 folds revert → initial.
            var cs = Compute("#x { scrollbar-width: thin; scrollbar-width: revert; }");
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("auto"),
                "`revert` with no UA rule must fall back to initial `auto`");
        }

        [Test]
        public void Scrollbar_width_unknown_keyword_is_stored_as_authored() {
            // The cascade engine is a value carrier — it does NOT validate keyword
            // sets for individual properties. An unrecognised keyword is stored as
            // authored rather than dropped. The consumer (layout resolver) is
            // responsible for falling back to the initial value when it sees an
            // unknown token. This test pins the actual v1 behaviour as a regression
            // guard so a future "drop invalid values" refactor must be deliberate.
            var cs = Compute("#x { scrollbar-width: extra-wide; }");
            // The value is stored as-is; it does NOT revert to `auto`.
            Assert.That(cs.Get("scrollbar-width"), Is.EqualTo("extra-wide"),
                "cascade stores unknown keyword as authored; consumer handles fallback");
        }

        // ══════════════════════════════════════════════════════════════════
        // II. scrollbar-color  (CSS Scrollbars L1 §3.2)
        //     Inherited: YES   Initial: auto
        //     Values: auto | <color> <color>   (thumb color  track color)
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Scrollbar_color_is_registered() {
            var prop = CssProperties.Get("scrollbar-color");
            Assert.That(prop, Is.Not.Null, "scrollbar-color must be registered");
        }

        [Test]
        public void Scrollbar_color_initial_is_auto() {
            // CSS Scrollbars L1 §3.2: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_color_is_inherited_per_spec() {
            // CSS Scrollbars L1 §3.2: "Inherited: yes" — authors can set a
            // scrollbar palette once on a container and have all child scrollers
            // inherit it. This is the spec-correct flag for scrollbar-color.
            Assert.That(CssProperties.IsInherited("scrollbar-color"), Is.True,
                "scrollbar-color must be INHERITED per CSS Scrollbars L1 §3.2");
        }

        [Test]
        public void Scrollbar_color_auto_round_trips() {
            var cs = Compute("#x { scrollbar-color: auto; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_color_two_named_colors_round_trip() {
            // §3.2: two-color form = <thumb-color> <track-color>. Both colors
            // are present when the property is non-auto.
            var cs = Compute("#x { scrollbar-color: rebeccapurple white; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("rebeccapurple white"));
        }

        [Test]
        public void Scrollbar_color_two_hex_colors_round_trip() {
            var cs = Compute("#x { scrollbar-color: #336699 #eee; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("#336699 #eee"));
        }

        [Test]
        public void Scrollbar_color_rgb_colors_round_trip() {
            // Functional color notation must survive the parse → cascade round-trip.
            var cs = Compute("#x { scrollbar-color: rgb(0, 128, 255) rgba(0,0,0,0.2); }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("rgb(0, 128, 255) rgba(0,0,0,0.2)"));
        }

        [Test]
        public void Scrollbar_color_currentcolor_token_round_trips() {
            // `currentcolor` is a valid CSS color value (CSS Color 4 §3) and
            // therefore valid as either the thumb or track color.
            var cs = Compute("#x { scrollbar-color: currentcolor transparent; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("currentcolor transparent"));
        }

        [Test]
        public void Scrollbar_color_inherits_to_child() {
            // Because scrollbar-color is inherited, a child without a rule sees
            // the parent's computed value.
            var cs = ComputeChild("#p { scrollbar-color: navy gold; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("navy gold"),
                "scrollbar-color is inherited; child must see the parent's value");
        }

        [Test]
        public void Scrollbar_color_child_rule_overrides_inherited_value() {
            // An explicit child rule beats the inherited parent value.
            var cs = ComputeChild("#p { scrollbar-color: navy gold; } #x { scrollbar-color: auto; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_color_important_wins() {
            // !important author beats normal author regardless of specificity.
            var doc = Html("<div id=\"x\" class=\"sc\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div#x.sc { scrollbar-color: navy blue; } " +
                       "* { scrollbar-color: auto !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("auto"),
                "!important on lower-specificity rule must win");
        }

        [Test]
        public void Scrollbar_color_initial_keyword_resets_to_auto() {
            var cs = Compute("#x { scrollbar-color: navy blue; scrollbar-color: initial; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_color_inherit_keyword_forces_parent_color() {
            var cs = ComputeChild("#p { scrollbar-color: crimson black; } #x { scrollbar-color: inherit; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("crimson black"));
        }

        [Test]
        public void Scrollbar_color_unset_on_inherited_resolves_to_parent_value() {
            // CSS Cascade L5 §7.3: `unset` on an inherited property == `inherit`.
            var cs = ComputeChild("#p { scrollbar-color: crimson black; } #x { scrollbar-color: auto; scrollbar-color: unset; }");
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("crimson black"),
                "`unset` on inherited scrollbar-color must resolve to parent's value");
        }

        [Test]
        public void Scrollbar_color_single_color_stored_as_authored() {
            // §3.2 requires exactly 2 colors for the non-auto form. A single color
            // token is spec-invalid, but the v1 cascade engine is a pass-through
            // carrier — it stores the authored value without semantic validation.
            // The paint resolver (ScrollMath.TryResolveScrollbarColors) is
            // responsible for rejecting single-token forms at consume time.
            // This test pins the actual storage behaviour as a regression guard.
            var cs = Compute("#x { scrollbar-color: navy; }");
            // Stored as-is; does NOT revert to auto at cascade level.
            Assert.That(cs.Get("scrollbar-color"), Is.EqualTo("navy"),
                "cascade stores single-color value as authored; paint resolver handles rejection");
        }

        // ══════════════════════════════════════════════════════════════════
        // III. scrollbar-gutter  (CSS Overflow L3 §6.2)
        //      Inherited: no   Initial: auto
        //      Values: auto | stable | stable both-edges
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Scrollbar_gutter_is_registered() {
            var prop = CssProperties.Get("scrollbar-gutter");
            Assert.That(prop, Is.Not.Null, "scrollbar-gutter must be registered");
        }

        [Test]
        public void Scrollbar_gutter_initial_is_auto() {
            // CSS Overflow L3 §6.2: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_gutter_not_inherited_in_registry() {
            // CSS Overflow L3 §6.2: "Inherited: no".
            Assert.That(CssProperties.IsInherited("scrollbar-gutter"), Is.False,
                "scrollbar-gutter must be non-inherited per CSS Overflow L3 §6.2");
        }

        [Test]
        public void Scrollbar_gutter_auto_round_trips() {
            var cs = Compute("#x { scrollbar-gutter: auto; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_gutter_stable_round_trips() {
            // §6.2: `stable` reserves a gutter equal to the scrollbar width
            // even when the scrollbar is not currently visible, preventing
            // layout shifts when content changes from non-scrollable to scrollable.
            var cs = Compute("#x { scrollbar-gutter: stable; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("stable"));
        }

        [Test]
        public void Scrollbar_gutter_stable_both_edges_round_trips() {
            // §6.2: `stable both-edges` reserves the gutter on BOTH the
            // scrollbar side and the opposite side so content stays centred.
            // v1 reserves only one edge (tracked in CSS_COMPLIANCE_ISSUES I14b).
            var cs = Compute("#x { scrollbar-gutter: stable both-edges; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("stable both-edges"));
        }

        [Test]
        public void Scrollbar_gutter_does_not_inherit() {
            var cs = ComputeChild("#p { scrollbar-gutter: stable; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"),
                "scrollbar-gutter is non-inherited; child must see initial `auto`");
        }

        [Test]
        public void Scrollbar_gutter_important_wins() {
            var doc = Html("<div id=\"x\" class=\"g\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div#x.g { scrollbar-gutter: stable; } * { scrollbar-gutter: auto !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"),
                "!important on lower-specificity rule must win");
        }

        [Test]
        public void Scrollbar_gutter_initial_keyword_resets_to_auto() {
            var cs = Compute("#x { scrollbar-gutter: stable; scrollbar-gutter: initial; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scrollbar_gutter_inherit_keyword_forces_parent_value() {
            // Forced inheritance on a non-inherited property.
            var cs = ComputeChild("#p { scrollbar-gutter: stable; } #x { scrollbar-gutter: inherit; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("stable"),
                "`inherit` must pull the parent's `stable` onto this non-inherited property");
        }

        [Test]
        public void Scrollbar_gutter_unset_on_non_inherited_resolves_to_initial() {
            var cs = Compute("#x { scrollbar-gutter: stable; scrollbar-gutter: unset; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"),
                "`unset` on non-inherited property resolves to initial `auto`");
        }

        [Test]
        public void Scrollbar_gutter_revert_falls_back_to_initial_with_no_ua_rule() {
            var cs = Compute("#x { scrollbar-gutter: stable; scrollbar-gutter: revert; }");
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("auto"),
                "`revert` with no UA rule must fall back to initial `auto`");
        }

        [Test]
        public void Scrollbar_gutter_unknown_keyword_is_stored_as_authored() {
            // Same cascade-as-pass-through behaviour as scrollbar-width above.
            // The consumer is responsible for falling back when it sees an unknown
            // token. Pinned so a future drop-on-invalid change is deliberate.
            var cs = Compute("#x { scrollbar-gutter: force; }");
            // Stored as-is; does NOT revert to auto at cascade level.
            Assert.That(cs.Get("scrollbar-gutter"), Is.EqualTo("force"),
                "cascade stores unknown keyword as authored; consumer handles fallback");
        }

        // ══════════════════════════════════════════════════════════════════
        // IV. overscroll-behavior shorthand + -x/-y longhands
        //     (CSS Overscroll Behavior L1 §3)
        //     Inherited: no   Initial: auto
        //     Values: auto | contain | none
        //     Shorthand: <value>{1,2} → overscroll-behavior-x / -y
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Overscroll_behavior_shorthand_is_registered() {
            var prop = CssProperties.Get("overscroll-behavior");
            Assert.That(prop, Is.Not.Null, "overscroll-behavior must be registered");
        }

        [Test]
        public void Overscroll_behavior_x_is_registered() {
            var prop = CssProperties.Get("overscroll-behavior-x");
            Assert.That(prop, Is.Not.Null, "overscroll-behavior-x must be registered");
        }

        [Test]
        public void Overscroll_behavior_y_is_registered() {
            var prop = CssProperties.Get("overscroll-behavior-y");
            Assert.That(prop, Is.Not.Null, "overscroll-behavior-y must be registered");
        }

        [Test]
        public void Overscroll_behavior_longhands_initial_is_auto() {
            // CSS Overscroll Behavior L1 §3: initial value `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"));
        }

        [Test]
        public void Overscroll_behavior_not_inherited_in_registry() {
            // CSS Overscroll Behavior L1 §3: "Inherited: no" on all three.
            Assert.That(CssProperties.IsInherited("overscroll-behavior"), Is.False,
                "overscroll-behavior shorthand must be non-inherited");
            Assert.That(CssProperties.IsInherited("overscroll-behavior-x"), Is.False,
                "overscroll-behavior-x must be non-inherited");
            Assert.That(CssProperties.IsInherited("overscroll-behavior-y"), Is.False,
                "overscroll-behavior-y must be non-inherited");
        }

        [Test]
        public void Overscroll_behavior_auto_longhand_round_trips() {
            var cs = Compute("#x { overscroll-behavior-x: auto; overscroll-behavior-y: auto; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"));
        }

        [Test]
        public void Overscroll_behavior_contain_longhand_round_trips() {
            // §3: `contain` prevents overscroll from propagating to the ancestor
            // scroll container while still triggering the native OS overscroll
            // effect (rubber-banding / glow) locally.
            var cs = Compute("#x { overscroll-behavior-x: contain; overscroll-behavior-y: contain; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("contain"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("contain"));
        }

        [Test]
        public void Overscroll_behavior_none_longhand_round_trips() {
            // §3: `none` prevents propagation AND suppresses the OS overscroll
            // effect — scrolling stops at the boundary, period.
            var cs = Compute("#x { overscroll-behavior-x: none; overscroll-behavior-y: none; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("none"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("none"));
        }

        [Test]
        public void Overscroll_behavior_shorthand_one_value_sets_both_axes() {
            // §3: a single value applies to both x and y axes.
            var cs = Compute("#x { overscroll-behavior: contain; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("contain"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("contain"));
        }

        [Test]
        public void Overscroll_behavior_shorthand_two_values_maps_x_then_y() {
            // §3: two-value form = <x-value> <y-value>.
            var cs = Compute("#x { overscroll-behavior: contain none; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("contain"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("none"));
        }

        [Test]
        public void Overscroll_behavior_shorthand_none_auto_maps_correctly() {
            // Verify the reverse axis order too: x=none, y=auto.
            var cs = Compute("#x { overscroll-behavior: none auto; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("none"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"));
        }

        [Test]
        public void Overscroll_behavior_shorthand_none_round_trips_as_both_none() {
            // One-value `none` should propagate to both longhands.
            var cs = Compute("#x { overscroll-behavior: none; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("none"));
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("none"));
        }

        [Test]
        public void Overscroll_behavior_longhand_overrides_shorthand() {
            // A subsequent longhand must beat the earlier shorthand expansion.
            var cs = Compute("#x { overscroll-behavior: contain; overscroll-behavior-y: auto; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("contain"),
                "x still comes from the shorthand");
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"),
                "y is overridden by the explicit longhand");
        }

        [Test]
        public void Overscroll_behavior_does_not_inherit() {
            // Non-inherited: parent's contain must NOT leak to the child.
            var cs = ComputeChild("#p { overscroll-behavior: contain; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"),
                "overscroll-behavior-x is non-inherited; child must see initial `auto`");
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"),
                "overscroll-behavior-y is non-inherited; child must see initial `auto`");
        }

        [Test]
        public void Overscroll_behavior_x_important_wins() {
            var doc = Html("<div id=\"x\" class=\"o\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div#x.o { overscroll-behavior-x: contain; } " +
                       "* { overscroll-behavior-x: none !important; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("none"),
                "!important on lower-specificity rule must win");
        }

        [Test]
        public void Overscroll_behavior_x_initial_keyword_resets_to_auto() {
            var cs = Compute("#x { overscroll-behavior-x: contain; overscroll-behavior-x: initial; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"));
        }

        [Test]
        public void Overscroll_behavior_y_initial_keyword_resets_to_auto() {
            var cs = Compute("#x { overscroll-behavior-y: none; overscroll-behavior-y: initial; }");
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"));
        }

        [Test]
        public void Overscroll_behavior_inherit_keyword_forces_parent_value() {
            var cs = ComputeChild(
                "#p { overscroll-behavior-x: contain; } " +
                "#x { overscroll-behavior-x: inherit; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("contain"),
                "`inherit` must pull the parent's `contain` onto this non-inherited property");
        }

        [Test]
        public void Overscroll_behavior_unset_on_non_inherited_resolves_to_initial() {
            var cs = Compute(
                "#x { overscroll-behavior-x: none; overscroll-behavior-x: unset; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"),
                "`unset` on non-inherited overscroll-behavior-x must resolve to initial `auto`");
        }

        [Test]
        public void Overscroll_behavior_revert_falls_back_to_initial_with_no_ua_rule() {
            var cs = Compute(
                "#x { overscroll-behavior-y: contain; overscroll-behavior-y: revert; }");
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"),
                "`revert` with no UA rule must fall back to initial `auto`");
        }

        [Test]
        public void Overscroll_behavior_invalid_token_is_dropped() {
            // An unrecognised keyword is invalid; the shorthand expansion
            // must produce no longhands and the longhand must stay at initial.
            var cs = Compute("#x { overscroll-behavior: scroll; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"),
                "invalid keyword must be dropped; longhand stays at initial `auto`");
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"),
                "invalid keyword must be dropped; longhand stays at initial `auto`");
        }

        [Test]
        public void Overscroll_behavior_three_values_are_dropped_as_invalid() {
            // §3: only one or two tokens are valid; three is illegal.
            var cs = Compute("#x { overscroll-behavior: auto contain none; }");
            Assert.That(cs.Get("overscroll-behavior-x"), Is.EqualTo("auto"),
                "three-value form must be discarded; both longhands must stay at initial");
            Assert.That(cs.Get("overscroll-behavior-y"), Is.EqualTo("auto"));
        }

        // ══════════════════════════════════════════════════════════════════
        // V. Cross-property registry contract sweep
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void All_scrollbar_overscroll_properties_have_correct_initial_values() {
            // One test that sweeps the declared initial values for all six
            // properties as a regression guard against accidental mutation.
            Assert.That(CssProperties.InitialValueOf("scrollbar-width"),    Is.EqualTo("auto"));
            Assert.That(CssProperties.InitialValueOf("scrollbar-color"),    Is.EqualTo("auto"));
            Assert.That(CssProperties.InitialValueOf("scrollbar-gutter"),   Is.EqualTo("auto"));
            Assert.That(CssProperties.InitialValueOf("overscroll-behavior"),   Is.EqualTo("auto"));
            Assert.That(CssProperties.InitialValueOf("overscroll-behavior-x"), Is.EqualTo("auto"));
            Assert.That(CssProperties.InitialValueOf("overscroll-behavior-y"), Is.EqualTo("auto"));
        }

        [Test]
        public void Inheritance_flags_match_css_spec_for_all_six_properties() {
            // scrollbar-color IS inherited (CSS Scrollbars L1 §3.2).
            // All others are NOT inherited.
            Assert.That(CssProperties.IsInherited("scrollbar-width"),    Is.False,
                "scrollbar-width: non-inherited per §3.1");
            Assert.That(CssProperties.IsInherited("scrollbar-color"),    Is.True,
                "scrollbar-color: INHERITED per §3.2");
            Assert.That(CssProperties.IsInherited("scrollbar-gutter"),   Is.False,
                "scrollbar-gutter: non-inherited per Overflow L3 §6.2");
            Assert.That(CssProperties.IsInherited("overscroll-behavior"),   Is.False,
                "overscroll-behavior: non-inherited per Overscroll L1 §3");
            Assert.That(CssProperties.IsInherited("overscroll-behavior-x"), Is.False,
                "overscroll-behavior-x: non-inherited per Overscroll L1 §3");
            Assert.That(CssProperties.IsInherited("overscroll-behavior-y"), Is.False,
                "overscroll-behavior-y: non-inherited per Overscroll L1 §3");
        }
    }
}
