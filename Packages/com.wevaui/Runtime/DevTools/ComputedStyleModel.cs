using System;
using System.Collections.Generic;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.DevTools {
    // Chrome DevTools "Computed" pane — sorted property/value list with box-model
    // numbers, filterable by a substring on the property name.
    //
    // No Unity APIs — headless-testable.
    public sealed class ComputedStyleModel {
        // Sorted by property name, ascending (Chrome Computed pane order).
        readonly List<ComputedEntry> allEntries = new List<ComputedEntry>(64);

        // Last-built box model. Zeroed when box was null.
        public BoxModelNumbers BoxModel { get; private set; }

        // Build from a ComputedStyle + optional Box. The style is enumerated
        // via ComputedStyle.Enumerate() (same as StyleInspector.Dump).
        // Null values are excluded (CSS-wide keyword / unset artifacts).
        public void Build(ComputedStyle style, Box box) {
            allEntries.Clear();
            BoxModel = box != null ? new BoxModelNumbers(box) : BoxModelNumbers.Zero;

            if (style == null) return;

            foreach (var kv in style.Enumerate()) {
                if (kv.Value == null) continue;
                allEntries.Add(new ComputedEntry(kv.Key, kv.Value));
            }

            // Sort by property name, case-insensitive (matches Chrome Computed tab).
            allEntries.Sort(static (a, b) =>
                string.Compare(a.Property, b.Property, StringComparison.OrdinalIgnoreCase));
        }

        // Return entries matching the filter string (case-insensitive substring on
        // property name). Pass null or empty string to return all entries.
        public IReadOnlyList<ComputedEntry> Filter(string substring) {
            if (string.IsNullOrEmpty(substring)) return allEntries;

            var result = new List<ComputedEntry>(allEntries.Count);
            foreach (var e in allEntries) {
                if (e.Property.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0) {
                    result.Add(e);
                }
            }
            return result;
        }

        // Total number of computed entries (unfiltered).
        public int Count => allEntries.Count;
    }

    // One property/value pair from the computed style.
    public sealed class ComputedEntry {
        public readonly string Property;
        public readonly string Value;

        internal ComputedEntry(string property, string value) {
            Property = property;
            Value = value;
        }
    }
}
