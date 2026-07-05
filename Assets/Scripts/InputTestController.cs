using UnityEngine;
using UnityEngine.InputSystem;
using Weva;
using Weva.Dom;
using Weva.Events;

// Drives the inputtest.html focus/gamepad test bench. Keyboard Tab / Shift+Tab
// already move focus (the engine routes them through FocusManager via the
// built-in UnityInputController). This adds:
//   • gamepad d-pad / left-stick AND keyboard arrows → spatial focus nav
//   • A / Enter / Space → activate the focused control (synthetic click)
//   • B / Esc → close the pause menu
// and the menu open/close + focus-trap logic. The cyan :focus ring in the CSS
// shows which control is focused.
[RequireComponent(typeof(WevaDocument))]
public sealed class InputTestController : MonoBehaviour {
    WevaDocument doc;
    DirectionalNavigation nav;
    bool inited;
    bool menuOpen;
    Element returnFocus;

    const float RepeatDelay = 0.40f;
    const float RepeatRate = 0.12f;
    float repeatTimer;
    bool repeatActive;
    NavDirection lastDir;

    void Awake() => doc = GetComponent<WevaDocument>();

    void EnsureInit() {
        if (inited) return;
        if (doc == null || doc.Events == null || doc.Doc == null) return;
        if (doc.CurrentState?.ElementToBox == null) return;

        nav = new DirectionalNavigation(doc.Events, doc.Doc, NavRectOf) { IsHidden = Hidden };
        // Tab nav (FocusManager) honours the same hidden test so the closed
        // menu's buttons are never reachable by Tab either.
        doc.Events.IsHidden = Hidden;

        // Seed focus on the first toolbar button so the first nav input has an
        // anchor to move from.
        var first = doc.GetElementById("b-profile");
        if (first != null) doc.Events.Focus(first);
        inited = true;
    }

    void Update() {
        EnsureInit();
        if (!inited) return;

        var gp = Gamepad.current;
        var kb = Keyboard.current;

        // ── directional nav (gamepad + arrows) ───────────────────────────
        Vector2 v = Vector2.zero;
        if (gp != null) {
            v = gp.dpad.ReadValue();
            if (v.sqrMagnitude < 0.1f) v = gp.leftStick.ReadValue();
        }
        if (kb != null) {
            if (kb.upArrowKey.isPressed) v.y = 1;
            else if (kb.downArrowKey.isPressed) v.y = -1;
            if (kb.leftArrowKey.isPressed) v.x = -1;
            else if (kb.rightArrowKey.isPressed) v.x = 1;
        }

        if (v.sqrMagnitude > 0.3f) {
            var dir = ToDir(v);
            if (!repeatActive || dir != lastDir) {
                MoveFocus(dir);
                repeatTimer = RepeatDelay; repeatActive = true; lastDir = dir;
            } else {
                repeatTimer -= Time.unscaledDeltaTime;
                if (repeatTimer <= 0f) { MoveFocus(dir); repeatTimer = RepeatRate; }
            }
        } else {
            repeatActive = false;
        }

        // ── activate / cancel ─────────────────────────────────────────────
        bool submit = (gp != null && gp.buttonSouth.wasPressedThisFrame)
                   || (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame));
        bool cancel = (gp != null && gp.buttonEast.wasPressedThisFrame)
                   || (kb != null && kb.escapeKey.wasPressedThisFrame);

        if (submit) Activate();
        else if (cancel && menuOpen) CloseMenu();
    }

    void MoveFocus(NavDirection dir) {
        var f = doc.Events.FocusedElement;
        if (f == null) {
            var first = doc.GetElementById(menuOpen ? "m-resume" : "b-profile");
            if (first != null) doc.Events.Focus(first);
            return;
        }
        nav.MoveFocus(dir);
    }

    void Activate() {
        var f = doc.Events.FocusedElement;
        if (f == null) return;

        // Synthetic click at the focused control's centre — drives :active and
        // the real click pipeline (so on-click handlers would fire too).
        var c = CenterOf(f);
        if (c.HasValue) {
            doc.Events.DispatchPointerDown(c.Value.x, c.Value.y, 0, default);
            doc.Events.DispatchPointerUp(c.Value.x, c.Value.y, 0, default);
        }

        // Menu open/close routing by control id.
        if (f.Id == "btn-open") OpenMenu();
        else if (f.Id == "btn-close") CloseMenu();
    }

    void OpenMenu() {
        if (menuOpen) return;
        var menu = doc.GetElementById("menu");
        if (menu == null) return;
        returnFocus = doc.Events.FocusedElement;
        menu.SetAttribute("class", "menu open");
        menuOpen = true;
        var first = doc.GetElementById("m-resume");
        if (first != null) doc.Events.Focus(first);
    }

    void CloseMenu() {
        var menu = doc.GetElementById("menu");
        if (menu != null) menu.SetAttribute("class", "menu");
        menuOpen = false;
        var back = returnFocus ?? doc.GetElementById("btn-open");
        if (back != null) doc.Events.Focus(back);
    }

    // True when the element (or any ancestor) is display:none / visibility:hidden
    // — keeps the closed menu's items out of both Tab and directional nav.
    bool Hidden(Element e) {
        for (var n = e; n != null; n = n.Parent as Element) {
            var s = doc.Cascade?.GetComposedStyle(n, doc.State);
            if (s == null) continue;
            if (s.Get("display") == "none" || s.Get("visibility") == "hidden") return true;
        }
        return false;
    }

    NavRect? NavRectOf(Element e) {
        var c = AbsRect(e);
        if (!c.HasValue) return null;
        var r = c.Value;
        return new NavRect(r.x, r.y, r.w, r.h);
    }

    (double x, double y)? CenterOf(Element e) {
        var c = AbsRect(e);
        if (!c.HasValue) return null;
        var r = c.Value;
        return (r.x + r.w * 0.5, r.y + r.h * 0.5);
    }

    (double x, double y, double w, double h)? AbsRect(Element e) {
        var map = doc.CurrentState?.ElementToBox;
        if (map == null) return null;
        var box = map.Lookup(e);
        if (box == null) return null;
        double x = 0, y = 0;
        for (var b = box; b != null; b = b.Parent) { x += b.X; y += b.Y; }
        return (x, y, box.Width, box.Height);
    }

    static NavDirection ToDir(Vector2 v) {
        if (Mathf.Abs(v.x) > Mathf.Abs(v.y))
            return v.x > 0 ? NavDirection.Right : NavDirection.Left;
        return v.y > 0 ? NavDirection.Up : NavDirection.Down;
    }
}
