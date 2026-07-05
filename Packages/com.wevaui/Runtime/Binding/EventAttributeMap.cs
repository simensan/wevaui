using System.Collections.Generic;
using Weva.Events;

namespace Weva.Binding {
    internal static class EventAttributeMap {
        static readonly Dictionary<string, EventKind> map = new(System.StringComparer.OrdinalIgnoreCase) {
            { "on-click",        EventKind.Click        },
            { "on-change",       EventKind.Change       },
            { "on-input",        EventKind.Input        },
            { "on-submit",       EventKind.Submit       },
            { "on-focus",        EventKind.Focus        },
            { "on-blur",         EventKind.Blur         },
            { "on-pointerdown",  EventKind.PointerDown  },
            { "on-pointermove",  EventKind.PointerMove  },
            { "on-pointerup",    EventKind.PointerUp    },
            { "on-pointerleave", EventKind.PointerLeave },
        };

        public static bool TryGet(string attrName, out EventKind kind) {
            if (attrName == null) { kind = default; return false; }
            return map.TryGetValue(attrName, out kind);
        }

        public static bool IsKnownEventAttribute(string attrName) {
            return attrName != null && map.ContainsKey(attrName);
        }

        public static IEnumerable<string> KnownAttributeNames => map.Keys;
    }
}
