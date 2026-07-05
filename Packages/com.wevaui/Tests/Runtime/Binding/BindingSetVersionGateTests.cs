using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Tests.Events;

namespace Weva.Tests.Binding {
    // IBindingVersion gate: a controller that reports an unchanged version
    // (same instance, no binding add/remove since the last pass) makes
    // BindingSet.Update return immediately without resolving anything.
    public class BindingSetVersionGateTests {
        sealed class VersionedCtx : IBindingVersion {
            public int BindingVersion { get; set; }

            public int Getters;
            string name = "Alice";
            public string Name {
                get { Getters++; return name; }
                set { name = value; BindingVersion++; }
            }

            public List<string> Items = new();
        }

        sealed class PollingCtx {
            public int Getters;
            string name = "Alice";
            public string Name {
                get { Getters++; return name; }
                set { name = value; }
            }
        }

        static Document Html(string s) => HtmlParser.Parse(s);

        static Element FindTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Unchanged_version_skips_the_poll_entirely() {
            var doc = Html("<p>{{ Name }}</p>");
            var ctx = new VersionedCtx();
            var set = BindingScanner.Scan(doc, ctx);

            set.Update(ctx);
            int gettersAfterFirst = ctx.Getters;
            Assert.That(gettersAfterFirst, Is.GreaterThan(0), "first pass must resolve");

            for (int i = 0; i < 10; i++) {
                Assert.That(set.Update(ctx), Is.False);
            }
            Assert.That(ctx.Getters, Is.EqualTo(gettersAfterFirst),
                "gated updates must not touch the controller at all");
        }

        [Test]
        public void Version_bump_releases_the_gate_and_applies_the_change() {
            var doc = Html("<p>{{ Name }}</p>");
            var ctx = new VersionedCtx();
            var set = BindingScanner.Scan(doc, ctx);
            set.Update(ctx);

            ctx.Name = "Bob"; // setter bumps BindingVersion
            bool changed = set.Update(ctx);

            Assert.That(changed, Is.True);
            var p = FindTag(doc, "p");
            Assert.That(((TextNode)p.Children[0]).Data, Is.EqualTo("Bob"));
        }

        [Test]
        public void Non_versioned_controller_keeps_polling_every_update() {
            var doc = Html("<p>{{ Name }}</p>");
            var ctx = new PollingCtx();
            var set = BindingScanner.Scan(doc, ctx);
            set.Update(ctx);
            int after1 = ctx.Getters;
            set.Update(ctx);
            Assert.That(ctx.Getters, Is.GreaterThan(after1),
                "non-versioned contexts must keep the poll-every-frame behaviour");
        }

        [Test]
        public void Different_context_instance_releases_the_gate() {
            var doc = Html("<p>{{ Name }}</p>");
            var a = new VersionedCtx();
            var set = BindingScanner.Scan(doc, a);
            set.Update(a);

            // Same version number, different instance: must re-poll.
            var b = new VersionedCtx();
            b.Name = "Replaced";
            b.BindingVersion = a.BindingVersion;
            set.Update(b);

            var p = FindTag(doc, "p");
            Assert.That(((TextNode)p.Children[0]).Data, Is.EqualTo("Replaced"));
        }

        [Test]
        public void Repeat_items_mutation_without_bump_is_gated_until_bumped() {
            // Documents the contract: data mutations the controller does NOT
            // version-stamp stay invisible while the gate holds; bumping
            // applies them. Forgetting to bump is the controller's bug, not a
            // crash or stale-forever state.
            var doc = Html("<main><template data-each=\"Items as item\"><p>{{ item }}</p></template></main>");
            var ctx = new VersionedCtx();
            ctx.Items.Add("one");
            var set = BindingScanner.Scan(doc, ctx);
            set.Update(ctx);
            var main = FindTag(doc, "main");
            Assert.That(main.Children.Count, Is.EqualTo(2), "one row + template");

            ctx.Items.Add("two"); // no bump â€” gated
            set.Update(ctx);
            Assert.That(main.Children.Count, Is.EqualTo(2), "gate holds without a bump");

            ctx.BindingVersion++;
            set.Update(ctx);
            Assert.That(main.Children.Count, Is.EqualTo(3), "bump applies the pending mutation");
        }

        [Test]
        public void Live_mutation_scan_releases_the_gate_despite_unchanged_version() {
            // AttachLive + AppendChild scans new bindings into the set; the
            // next Update must render them even though the version is stable.
            var doc = Html("<main><p>{{ Name }}</p></main>");
            var ctx = new VersionedCtx();
            var set = BindingScanner.Scan(doc, ctx);
            var dispatcher = new EventDispatcher(doc, new FakeHitTester(), new FakeUIClock());
            set.Wire(dispatcher);
            set.AttachLive(doc, ctx);
            set.Update(ctx);

            var main = FindTag(doc, "main");
            var added = new Element("span");
            added.AppendChild(new TextNode("{{ Name }}!"));
            main.AppendChild(added);

            set.Update(ctx); // version unchanged, but structure is dirty

            Assert.That(((TextNode)added.Children[0]).Data, Is.EqualTo("Alice!"),
                "bindings scanned in via live mutation must render on the next update");
        }

        [Test]
        public void Purged_subtree_releases_the_gate() {
            var doc = Html("<main><p>{{ Name }}</p><span>{{ Name }}?</span></main>");
            var ctx = new VersionedCtx();
            var set = BindingScanner.Scan(doc, ctx);
            var dispatcher = new EventDispatcher(doc, new FakeHitTester(), new FakeUIClock());
            set.Wire(dispatcher);
            set.AttachLive(doc, ctx);
            set.Update(ctx);
            int getters = ctx.Getters;

            var main = FindTag(doc, "main");
            main.RemoveChild(FindTag(doc, "span"));

            set.Update(ctx); // purge marked structure dirty â†’ re-poll survivors
            Assert.That(ctx.Getters, Is.GreaterThan(getters));
        }

#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
        [Test]
        public void Gated_idle_updates_allocate_nothing_even_via_reflection() {
            // Tier 1 keeps the accessor fast path alloc-free; the version gate
            // extends the zero-alloc guarantee to ANY controller (reflection
            // fallback, value-type members) because nothing is resolved at all.
            var doc = Html("<main><template data-each=\"Items as item\"><p>{{ item }} {{ Name }}</p></template></main>");
            var ctx = new VersionedCtx();
            ctx.Items.Add("row");
            var set = BindingScanner.Scan(doc, ctx);
            set.Update(ctx);
            for (int i = 0; i < 10; i++) set.Update(ctx);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) {
                set.Update(ctx);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(delta, Is.EqualTo(0),
                $"1000 gated updates allocated {delta} bytes; expected 0");
        }
#endif
    }
}
