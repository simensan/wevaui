using System;
using System.Collections.Generic;
using System.Reflection;
using Weva.Dom;

namespace Weva.Binding {
    public static class UIElementBinder {
        const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static IReadOnlyList<string> Populate(object controller, Document doc) {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var warnings = new List<string>();
            var t = controller.GetType();
            for (var cur = t; cur != null; cur = cur.BaseType) {
                var fields = cur.GetFields(FieldFlags);
                for (int i = 0; i < fields.Length; i++) {
                    var f = fields[i];
                    var attr = f.GetCustomAttribute<UIElementAttribute>();
                    if (attr == null) continue;
                    var id = attr.Id;
                    if (string.IsNullOrEmpty(id)) {
                        warnings.Add($"Field '{cur.Name}.{f.Name}' has [UIElement] with empty id.");
                        continue;
                    }
                    var element = doc.GetElementById(id);
                    if (element == null) {
                        warnings.Add($"No element with id='{id}' found for field '{cur.Name}.{f.Name}'.");
                        continue;
                    }
                    if (!f.FieldType.IsInstanceOfType(element)) {
                        warnings.Add($"Element with id='{id}' is of type {element.GetType().Name}, but field '{cur.Name}.{f.Name}' expects {f.FieldType.Name}.");
                        continue;
                    }
                    f.SetValue(controller, element);
                }
            }
            return warnings;
        }
    }
}
