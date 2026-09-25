// The core's data binding, fed from a C# object graph the way the Godot
// addon feeds it from a Dictionary: `{{ path }}` in text and attributes,
// `data-class-<name>="path"`, `data-each="Items as item"` rows, and
// `data-model` controls that read from and write back into the same data.
//
// The core asks for a path and is handed text (weva_binding_source); it
// never sees the objects. Values resolve through nested
// IDictionary<string, object> / IList segments, a bool reads "true"/"false"
// (what the C# engine's bindings and data-class already understand),
// numbers read invariant. Writing back keeps the type already at the path,
// so a script that put an int in Settings.Volume gets an int back.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AOT;

namespace Weva.Native
{
    internal sealed unsafe class NativeBindings : IDisposable
    {
        private GCHandle _self;
        private NativeDocument _doc;

        /// <summary>The data the document binds to: dictionaries, lists and scalars.</summary>
        public IDictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Optional resolver consulted before Data: returns the value at a
        /// path, or null for a path it does not know (a list answers
        /// data-each through its IList count). A null falls through to Data,
        /// so a controller's [UIBind] members and a model dictionary can be
        /// bound side by side.
        /// </summary>
        public Func<string, object> Resolver { get; set; }

        /// <summary>
        /// Optional writer tried before Data when a data-model control commits:
        /// returns true when it took the path (the controller owns it). A false
        /// falls through to Data.
        /// </summary>
        public Func<string, string, bool> Writer { get; set; }

        /// <summary>Raised when a data-model control wrote a value into Data: the path and the text.</summary>
        public event Action<string, string> DataChanged;

        public NativeBindings()
        {
        }

        public NativeBindings(IDictionary<string, object> data)
        {
            Data = data ?? new Dictionary<string, object>();
        }

        /// <summary>Installs this as the document's binding source and refreshes.</summary>
        public int Install(NativeDocument doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            var source = new weva_binding_source
            {
                user = (void*)GCHandle.ToIntPtr(_self),
                value = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, nuint, int*, nuint>)Marshal.GetFunctionPointerForDelegate(s_value),
                count = (delegate* unmanaged[Cdecl]<void*, byte*, int>)Marshal.GetFunctionPointerForDelegate(s_count),
            };
            WevaNative.weva_document_set_binding_source(doc.NativeHandle, &source);
            return Refresh();
        }

        /// <summary>
        /// Re-reads every binding and writes what changed into the document,
        /// then pushes data-model values into their controls. Returns how many
        /// nodes changed. Call when the data moved; nothing else can know.
        /// </summary>
        public int Refresh()
        {
            if (_doc == null) return 0;
            int changed = WevaNative.weva_document_refresh_bindings(_doc.NativeHandle);
            // The controls come last: data-each may only just have produced
            // the rows the models live on.
            changed += ApplyModels();
            return changed;
        }

        // Data -> control, only where they disagree: writing a field's own
        // value back into it would move the caret while someone types.
        private int ApplyModels()
        {
            int changed = 0;
            foreach (uint element in _doc.QueryAll("[data-model]"))
            {
                string path = ModelPathOf(element);
                if (path.Length == 0) continue;
                if (!TryResolve(path, out string wanted)) continue;
                if (_doc.ElementValue(element) == wanted) continue;
                // Compare the control's version, not the spelling: "true" sets
                // a checked box to "on", which is no change.
                ulong version = _doc.ElementFormVersion(element);
                if (_doc.SetElementValue(element, wanted) && _doc.ElementFormVersion(element) != version) changed++;
            }
            return changed;
        }

        /// <summary>The data path a data-model control binds, with data-each aliases unwound by the core.</summary>
        public string ModelPathOf(uint element)
        {
            string written = _doc.ElementAttribute(element, "data-model");
            if (written.Length == 0) return string.Empty;
            string resolved = _doc.ModelPath(element, written);
            return resolved.Length == 0 ? written : resolved;
        }

        /// <summary>
        /// Control -> data, for a VALUE_CHANGED or CHANGE event's target. Writes
        /// the control's value at its model path (keeping the type already
        /// there) and raises DataChanged. Returns false when nothing changed.
        /// </summary>
        public bool WriteBack(uint element)
        {
            if (_doc == null) return false;
            string path = ModelPathOf(element);
            if (path.Length == 0) return false;
            string text = _doc.ElementValue(element);
            bool written = Writer != null && Writer(path, text);
            if (!written && Resolver != null && Resolver(path) != null) return false;   // the resolver's, read-only
            if (!written && !WritePath(path, text)) return false;
            DataChanged?.Invoke(path, text);
            return true;
        }

        // ---- resolution ----------------------------------------------------

        public bool TryResolve(string path, out string text)
        {
            object value = ResolveObject(path);
            if (value == null)
            {
                text = null;
                return false;
            }
            text = Format(value);
            return true;
        }

        private object ResolveObject(string path)
        {
            if (Resolver != null)
            {
                object resolved = Resolver(path);
                if (resolved != null) return resolved;
            }
            object current = Data;
            foreach (string part in path.Split('.'))
            {
                if (!Step(current, part, out current)) return null;
            }
            return current;
        }

        private static bool Step(object current, string part, out object next)
        {
            next = null;
            if (current is IDictionary<string, object> dict)
            {
                return dict.TryGetValue(part, out next) && next != null;
            }
            if (current is IDictionary untyped)
            {
                if (!untyped.Contains(part)) return false;
                next = untyped[part];
                return next != null;
            }
            if (current is IList list && !(current is string))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return false;
                if (index < 0 || index >= list.Count) return false;
                next = list[index];
                return next != null;
            }
            return false;
        }

        public static string Format(object value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable n: return n.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        /// <summary>A control's text as the type of the value already at its path (a bool stays a bool, an int an int); the text itself for anything else.</summary>
        public static object Convert(object existing, string text)
        {
            return existing == null ? text : ConvertTo(existing.GetType(), text) ?? text;
        }

        /// <summary>A control's text as <paramref name="type"/>, or null when the type is not one a control can hold.</summary>
        public static object ConvertTo(Type type, string text)
        {
            if (type == typeof(string)) return text;
            if (type == typeof(bool))
            {
                string lower = text.ToLowerInvariant();
                return lower == "true" || lower == "1" || lower == "on";
            }
            if (type == typeof(int))
                return int.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out int iv) ? iv
                     : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double idv) ? (int)idv : 0;
            if (type == typeof(long))
                return long.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out long lv) ? lv
                     : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ldv) ? (long)ldv : 0L;
            if (type == typeof(float))
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv) ? fv : 0f;
            if (type == typeof(double))
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? dv : 0.0;
            if (type == typeof(object)) return text;
            return null;
        }

        private int Count(string path)
        {
            object value = ResolveObject(path);
            // A string is deliberately not a list of characters.
            return value is IList list && !(value is string) ? list.Count : -1;
        }

        private bool WritePath(string path, string text)
        {
            string[] parts = path.Split('.');
            if (parts.Length == 0) return false;
            object current = Data;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!Step(current, parts[i], out object next))
                {
                    // Make the dictionaries the path names but the data lacks.
                    if (!(current is IDictionary<string, object> holder)) return false;
                    next = new Dictionary<string, object>();
                    holder[parts[i]] = next;
                }
                current = next;
            }
            string leaf = parts[parts.Length - 1];
            object value = text;
            if (Step(current, leaf, out object existing))
            {
                value = Convert(existing, text);
                // A control's own live value coming back (or a spelling of the
                // same value) is not a change and must not fan out again.
                if (Equals(existing, value)) return false;
            }
            if (current is IDictionary<string, object> dict)
            {
                dict[leaf] = value;
                return true;
            }
            if (current is IList list && !(current is string) && int.TryParse(leaf, NumberStyles.Integer, CultureInfo.InvariantCulture, out int at) && at >= 0 && at < list.Count)
            {
                list[at] = value;
                return true;
            }
            return false;
        }

        // ---- the C callbacks ----------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nuint ValueFn(void* user, byte* path, byte* buffer, nuint capacity, int* found);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CountFn(void* user, byte* path);
        private static readonly ValueFn s_value = Value;
        private static readonly CountFn s_count = CountCallback;

        private static string PathOf(byte* path)
        {
            int n = 0;
            while (path[n] != 0) n++;
            return Encoding.UTF8.GetString(path, n);
        }

        [MonoPInvokeCallback(typeof(ValueFn))]
        private static nuint Value(void* user, byte* path, byte* buffer, nuint capacity, int* found)
        {
            var self = GCHandle.FromIntPtr((IntPtr)user).Target as NativeBindings;
            if (self == null || path == null)
            {
                if (found != null) *found = 0;
                return 0;
            }
            string text;
            try
            {
                if (!self.TryResolve(PathOf(path), out text))
                {
                    if (found != null) *found = 0;
                    return 0;
                }
            }
            catch (Exception)
            {
                if (found != null) *found = 0;
                return 0;
            }
            if (found != null) *found = 1;
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            if (buffer != null && capacity > 0)
            {
                int n = Math.Min(utf8.Length, (int)capacity - 1);
                if (n > 0) Marshal.Copy(utf8, 0, (IntPtr)buffer, n);
                buffer[n] = 0;
            }
            return (nuint)utf8.Length;
        }

        [MonoPInvokeCallback(typeof(CountFn))]
        private static int CountCallback(void* user, byte* path)
        {
            var self = GCHandle.FromIntPtr((IntPtr)user).Target as NativeBindings;
            if (self == null || path == null) return -1;
            try
            {
                return self.Count(PathOf(path));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public void Dispose()
        {
            if (_doc != null && _doc.IsAlive) WevaNative.weva_document_set_binding_source(_doc.NativeHandle, null);
            _doc = null;
            if (_self.IsAllocated) _self.Free();
        }
    }
}
