// [UIBind] over the core's data binding.
//
// The core asks the host for the value at a `{{ path }}` and is handed text
// (weva_binding_source, through NativeBindings.Resolver). The C# engine's
// binding layer answered that from a controller object: a member marked
// [UIBind] is a root, and a dotted path walks public members from there.
// This is that rule, host-side, for the core-backed WevaDocument -- the same
// controller a game wrote for 0.1.x binds the same way at 1.0.
//
// Only the FIRST segment must carry [UIBind]; the rest are ordinary public
// fields and properties, dictionary keys, or list indices. A path that does
// not resolve returns null, and NativeBindings falls back to its Data
// dictionary, so a model dictionary and a controller can be mixed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Weva.Binding;

namespace Weva.Native
{
    internal sealed class UIBindResolver
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly object _controller;
        private readonly Dictionary<string, MemberInfo> _roots = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        private readonly Dictionary<Type, Dictionary<string, MemberInfo>> _memberCache =
            new Dictionary<Type, Dictionary<string, MemberInfo>>();

        public UIBindResolver(object controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            for (Type t = controller.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(Members | BindingFlags.DeclaredOnly))
                    if (f.GetCustomAttribute<UIBindAttribute>() != null && !_roots.ContainsKey(f.Name)) _roots[f.Name] = f;
                foreach (PropertyInfo p in t.GetProperties(Members | BindingFlags.DeclaredOnly))
                    if (p.GetCustomAttribute<UIBindAttribute>() != null && p.CanRead && !_roots.ContainsKey(p.Name)) _roots[p.Name] = p;
            }
        }

        /// <summary>How many [UIBind] roots the controller declares.</summary>
        public int RootCount => _roots.Count;

        /// <summary>The value at a dotted path, or null when the first segment is not a [UIBind] root or a later one does not resolve.</summary>
        public object Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] segments = path.Split('.');
            if (!_roots.TryGetValue(segments[0].Trim(), out MemberInfo root)) return null;
            object current = Read(root, _controller);
            for (int i = 1; i < segments.Length && current != null; i++)
            {
                current = Step(current, segments[i].Trim());
            }
            return current;
        }

        /// <summary>
        /// Writes a data-model control's text back into the controller at a
        /// dotted path, keeping the type already there (an int field gets an
        /// int). Returns false when the path does not land on a writable
        /// member or the value is already that.
        /// </summary>
        public bool TryWrite(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] segments = path.Split('.');
            if (!_roots.TryGetValue(segments[0].Trim(), out MemberInfo root)) return false;
            if (segments.Length == 1) return WriteMember(root, _controller, text);

            // Resolve the parent with the same walk used by reads. A collection
            // entry is already a value, not a MemberInfo to read on the next
            // iteration (data-each paths commonly contain both kinds).
            object target = Read(root, _controller);
            for (int i = 1; i < segments.Length - 1 && target != null; i++)
                target = Step(target, segments[i].Trim());
            if (target == null) return false;
            string segment = segments[segments.Length - 1].Trim();
            if (target is IDictionary dict)
            {
                if (dict.IsReadOnly) return false;
                object existing = dict.Contains(segment) ? dict[segment] : null;
                object value = NativeBindings.Convert(existing, text);
                if (existing != null && Equals(existing, value)) return false;
                dict[segment] = value;
                return true;
            }
            if (target is IList list && int.TryParse(segment, out int index))
            {
                if (list.IsReadOnly || index < 0 || index >= list.Count) return false;
                object value = NativeBindings.Convert(list[index], text);
                if (Equals(list[index], value)) return false;
                list[index] = value;
                return true;
            }
            return WriteMember(MemberOf(target.GetType(), segment), target, text);
        }

        private static bool WriteMember(MemberInfo member, object target, string text)
        {
            if (target == null || member == null) return false;
            // A struct copy is not the controller's field: writing into it
            // would be lost, so only reference targets take a write.
            if (target.GetType().IsValueType) return false;
            object existing = Read(member, target);
            object value = NativeBindings.Convert(existing, text);
            if (existing == null)
            {
                Type type = member is FieldInfo f0 ? f0.FieldType : ((PropertyInfo)member).PropertyType;
                value = NativeBindings.ConvertTo(type, text);
                if (value == null) return false;
            }
            else if (Equals(existing, value)) return false;
            try
            {
                if (member is FieldInfo f)
                {
                    if (f.IsInitOnly) return false;
                    f.SetValue(target, value);
                    return true;
                }
                var p = (PropertyInfo)member;
                if (!p.CanWrite) return false;
                p.SetValue(target, value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private MemberInfo MemberOf(Type type, string segment)
        {
            if (!_memberCache.TryGetValue(type, out Dictionary<string, MemberInfo> members))
            {
                members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) members[f.Name] = f;
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (p.CanRead && p.GetIndexParameters().Length == 0) members[p.Name] = p;
                _memberCache[type] = members;
            }
            return members.TryGetValue(segment, out MemberInfo m) ? m : null;
        }

        private static object Read(MemberInfo member, object target)
        {
            try
            {
                return member is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)member).GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private object Step(object current, string segment)
        {
            if (segment.Length == 0) return null;
            if (current is IDictionary dict)
            {
                return dict.Contains(segment) ? dict[segment] : null;
            }
            if (current is IList list && int.TryParse(segment, out int index))
            {
                return index >= 0 && index < list.Count ? list[index] : null;
            }
            MemberInfo m = MemberOf(current.GetType(), segment);
            return m != null ? Read(m, current) : null;
        }
    }
}
