using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Parsing;
using Weva.Tests.Events;

namespace Weva.Tests.Forms {
    // TG18 coverage. ContextMenu.Show()/Dismiss/keyboard nav/separator+disabled
    // skipping/selection. These pin the documented public surface
    // (Show/Dismiss/Root) and the wired-up listener contract (outside click +
    // Escape + Enter dismiss).
    public class ContextMenuTests {
        EventDispatcher Build(Document doc, FakeHitTester ht) {
            return new EventDispatcher(doc, ht, new FakeUIClock());
        }

        // Build a doc with a single <main> root and a hit-tester pre-populated
        // with the main rect so outside-click dispatch always hits a target.
        static (Document doc, Element main, FakeHitTester ht) NewDoc() {
            var doc = HtmlParser.Parse("<main id='m'>body</main>");
            var main = doc.GetElementById("m");
            var ht = new FakeHitTester();
            ht.Add(main, 0, 0, 1000, 1000);
            return (doc, main, ht);
        }

        // Helper: count direct element children of the menu root whose class
        // contains itemClass (defaults to "ui-menu-item"). Separators are
        // emitted under a different class.
        static int CountItems(Element menuRoot, string itemClass = "ui-menu-item") {
            int n = 0;
            foreach (var c in menuRoot.Children) {
                if (c is Element e) {
                    var cls = e.GetAttribute("class") ?? "";
                    if (cls.Contains(itemClass) && !cls.Contains("ui-menu-separator")) n++;
                }
            }
            return n;
        }

        // Helper: index of the currently focused (.is-focused) item in the menu
        // root's child list (only counting Element children). -1 if none.
        static int FocusedItemDataIndex(Element menuRoot) {
            foreach (var c in menuRoot.Children) {
                if (c is Element e) {
                    var cls = e.GetAttribute("class") ?? "";
                    if (cls.Contains("is-focused")) {
                        var idx = e.GetAttribute("data-menu-index");
                        if (int.TryParse(idx, out var v)) return v;
                    }
                }
            }
            return -1;
        }

        [Test]
        public void Show_attaches_menu_at_xy_with_two_items() {
            var (doc, _, ht) = NewDoc();
            var d = Build(doc, ht);
            var items = new List<MenuItem> {
                MenuItem.Item("Copy", () => { }),
                MenuItem.Item("Paste", () => { }),
            };

            var menu = ContextMenu.Show(doc, d, null, 42.5, 99, items);

            Assert.That(menu, Is.Not.Null);
            Assert.That(menu.Root.Parent, Is.SameAs(doc));
            Assert.That(CountItems(menu.Root), Is.EqualTo(2));
            var style = menu.Root.GetAttribute("style") ?? "";
            Assert.That(style, Does.Contain("position:fixed"));
            Assert.That(style, Does.Contain("left:42.5px"));
            Assert.That(style, Does.Contain("top:99px"));
        }

        [Test]
        public void OutsideClick_dismisses_and_removes_menu_from_doc() {
            // Click on <main> at (10, 10) — main is registered in the hit
            // tester but the menu's items are not, so this is an "outside"
            // pointerdown from the menu's perspective.
            var (doc, _, ht) = NewDoc();
            var d = Build(doc, ht);
            var items = new List<MenuItem> {
                MenuItem.Item("A", () => { }),
                MenuItem.Item("B", () => { }),
            };
            var menu = ContextMenu.Show(doc, d, null, 0, 0, items);
            Assert.That(menu.Root.Parent, Is.SameAs(doc));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);

            Assert.That(menu.Root.Parent, Is.Null, "menu should be detached after outside click");
        }

        [Test]
        public void InsideClick_on_menu_item_does_not_trigger_outside_click_dismiss() {
            // Inside-click semantics: the OnOutsideClick handler walks the
            // event target up to the menu root and bails. The dismiss in
            // this scenario should come from Activate(), not from the
            // outside-click path. Wire a hit-tester for the menu item so
            // the pointerdown actually targets it.
            var (doc, _, ht) = NewDoc();
            var d = Build(doc, ht);
            int selected = 0;
            var items = new List<MenuItem> {
                MenuItem.Item("X", () => selected++),
            };
            var menu = ContextMenu.Show(doc, d, null, 0, 0, items);
            // Register the rendered first menu item element in the hit tester.
            Element firstItem = null;
            foreach (var c in menu.Root.Children) { if (c is Element e) { firstItem = e; break; } }
            ht.Add(firstItem, 500, 500, 100, 30);

            d.DispatchPointerDown(550, 510, 0, KeyModifiers.None);
            // Still attached — pointerdown alone should not dismiss when
            // it lands inside the menu.
            Assert.That(menu.Root.Parent, Is.SameAs(doc));
            // PointerUp triggers Click, which invokes Activate -> Dismiss.
            d.DispatchPointerUp(550, 510, 0, KeyModifiers.None);
            Assert.That(menu.Root.Parent, Is.Null);
            Assert.That(selected, Is.EqualTo(1));
        }

        [Test]
        public void Escape_key_dismisses_menu() {
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            // Focus the main element so the keydown has a routing target
            // that includes <main> on its capture path (where the menu
            // attaches its listener).
            d.Focus(main);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { })
            });

            d.DispatchKeyDown("Escape", "Escape", KeyModifiers.None, false);

            Assert.That(menu.Root.Parent, Is.Null);
        }

        [Test]
        public void ArrowDown_then_ArrowUp_navigates_to_next_and_back() {
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { }),
                MenuItem.Item("B", () => { }),
                MenuItem.Item("C", () => { }),
            });

            // Initial focus on first focusable item (index 0).
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(0));

            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(1));

            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(2));

            d.DispatchKeyDown("ArrowUp", "ArrowUp", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(1));
        }

        [Test]
        public void ArrowDown_on_last_item_wraps_to_first() {
            // Pins observed behaviour: StepFocus() rotates around items[]
            // (i >= items.Count → i = 0), so ArrowDown past the end wraps
            // back to the first focusable.
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { }),
                MenuItem.Item("B", () => { }),
            });
            // End key jumps to last focusable.
            d.DispatchKeyDown("End", "End", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(1));

            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(0),
                "ArrowDown past last item should wrap to first focusable");
        }

        [Test]
        public void ArrowUp_on_first_item_wraps_to_last() {
            // Symmetric wrap: ArrowUp from index 0 should land on the
            // last focusable (also pins the rotation semantics).
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { }),
                MenuItem.Item("B", () => { }),
                MenuItem.Item("C", () => { }),
            });
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(0));

            d.DispatchKeyDown("ArrowUp", "ArrowUp", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(2));
        }

        [Test]
        public void Disabled_item_is_skipped_by_keyboard_navigation() {
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { }),
                MenuItem.Item("B", () => { }, disabled: true),
                MenuItem.Item("C", () => { }),
            });
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(0));

            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            // Index 1 is disabled — focus should hop over to index 2.
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(2));

            d.DispatchKeyDown("ArrowUp", "ArrowUp", KeyModifiers.None, false);
            // Symmetric: ArrowUp from 2 hops over the disabled 1 back to 0.
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(0));
        }

        [Test]
        public void Separator_item_is_skipped_by_keyboard_navigation() {
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { }),
                MenuItem.Separator(),
                MenuItem.Item("C", () => { }),
            });
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(0));

            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(2),
                "separator at index 1 must be skipped by arrow nav");
        }

        [Test]
        public void Enter_activates_focused_item_and_dismisses() {
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            int aCount = 0, bCount = 0;
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => aCount++),
                MenuItem.Item("B", () => bCount++),
            });

            // Move to B then press Enter.
            d.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(FocusedItemDataIndex(menu.Root), Is.EqualTo(1));
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, false);

            Assert.That(bCount, Is.EqualTo(1));
            Assert.That(aCount, Is.EqualTo(0));
            Assert.That(menu.Root.Parent, Is.Null, "Enter activation should dismiss");
        }

        [Test]
        public void Enter_on_disabled_focused_item_is_inert() {
            // FindFirstFocusable() skips disabled items, so the initial
            // focus is never on a disabled row — but exercise the
            // defensive Activate() guard: programmatically forcing focus
            // onto a disabled row is impossible via public surface, so
            // pin the related guarantee that Enter with no focusable
            // items is inert.
            var (doc, main, ht) = NewDoc();
            var d = Build(doc, ht);
            d.Focus(main);
            int sel = 0;
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Separator(),
                MenuItem.Item("X", () => sel++, disabled: true),
            });
            // No focusable items → focusedIndex stays at -1; Enter should
            // not throw and should not dismiss.
            d.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, false);
            Assert.That(sel, Is.EqualTo(0));
            Assert.That(menu.Root.Parent, Is.SameAs(doc));
        }

        [Test]
        public void Dismiss_is_idempotent() {
            // The dismissed flag short-circuits a second Dismiss(); calling
            // twice should not throw and should leave the menu detached.
            var (doc, _, ht) = NewDoc();
            var d = Build(doc, ht);
            var menu = ContextMenu.Show(doc, d, null, 0, 0, new List<MenuItem> {
                MenuItem.Item("A", () => { })
            });
            menu.Dismiss();
            Assert.DoesNotThrow(() => menu.Dismiss());
            Assert.That(menu.Root.Parent, Is.Null);
        }

        [Test]
        public void Show_with_null_items_throws() {
            var (doc, _, ht) = NewDoc();
            var d = Build(doc, ht);
            Assert.Throws<System.ArgumentNullException>(
                () => ContextMenu.Show(doc, d, null, 0, 0, null));
        }
    }
}
