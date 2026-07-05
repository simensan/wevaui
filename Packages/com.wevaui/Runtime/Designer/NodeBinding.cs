using System.Collections.Generic;

namespace Weva.Designer
{
    /// <summary>
    /// Data-binding for a <see cref="DesignNode"/> — the game-UI differentiator. The
    /// designer wires UI to game data visually ("this label = Player.Health", "repeat
    /// this card for each Inventory.Item", "show when …"); the compiler lowers it to the
    /// engine's binding markup: <c>{{ path }}</c> text, <c>data-each</c>/<c>data-key</c>
    /// repeat, <c>data-class-*</c> toggles, and <c>on-click</c>/etc event attributes
    /// (handled by BindingScanner against an <c>[UIBind]</c> controller).
    /// </summary>
    public sealed class NodeBinding
    {
        /// <summary>Bind text content to a path → emits <c>{{ path }}</c> (overrides literal text).</summary>
        public string Text;

        /// <summary>Repeat this node per item: "<c>items as item</c>" → <c>data-each</c>.</summary>
        public string RepeatEach;
        /// <summary>Optional stable key path for repeated items → <c>data-key</c>.</summary>
        public string RepeatKey;

        /// <summary>CSS class name → boolean path; toggled via <c>data-class-&lt;name&gt;</c>.</summary>
        public Dictionary<string, string> Classes;
        /// <summary>Event name (click/change/input/submit/focus/blur) → controller method → <c>on-&lt;event&gt;</c>.</summary>
        public Dictionary<string, string> Events;

        public bool IsEmpty =>
            string.IsNullOrEmpty(Text) && string.IsNullOrEmpty(RepeatEach) && string.IsNullOrEmpty(RepeatKey)
            && (Classes == null || Classes.Count == 0) && (Events == null || Events.Count == 0);

        public void BindClass(string className, string path)
        {
            Classes ??= new Dictionary<string, string>();
            Classes[className] = path;
        }

        public void BindEvent(string eventName, string method)
        {
            Events ??= new Dictionary<string, string>();
            Events[eventName] = method;
        }

        public NodeBinding Clone()
        {
            var c = new NodeBinding { Text = Text, RepeatEach = RepeatEach, RepeatKey = RepeatKey };
            if (Classes != null) c.Classes = new Dictionary<string, string>(Classes);
            if (Events != null) c.Events = new Dictionary<string, string>(Events);
            return c;
        }

        /// <summary>The canonical set of event names the engine recognizes (for editor pickers).</summary>
        public static readonly string[] EventNames = { "click", "change", "input", "submit", "focus", "blur" };
    }
}
