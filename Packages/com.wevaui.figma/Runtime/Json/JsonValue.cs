using System;
using System.Collections.Generic;
using System.Globalization;

namespace Weva.Figma.Json
{
    /// <summary>
    /// The kind of a <see cref="JsonValue"/> node.
    /// </summary>
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// A small, immutable JSON DOM node. Produced by <see cref="JsonParser"/>.
    ///
    /// Access is deliberately lenient: indexing a missing key or wrong-typed
    /// node returns <see cref="Null"/> / a supplied fallback rather than
    /// throwing, because the Figma payload omits fields freely and chained
    /// access (<c>node["absoluteBoundingBox"]["width"].AsDouble()</c>) should
    /// degrade to a default instead of an NRE. Use <see cref="Has"/> /
    /// <see cref="TryGet"/> when presence actually matters.
    /// </summary>
    public sealed class JsonValue
    {
        /// <summary>Shared immutable null/missing sentinel.</summary>
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);

        public JsonKind Kind { get; }

        readonly bool _bool;
        readonly double _number;
        readonly string _string;
        readonly List<JsonValue> _array;
        readonly Dictionary<string, JsonValue> _object;

        JsonValue(JsonKind kind)
        {
            Kind = kind;
        }

        JsonValue(bool b) { Kind = JsonKind.Bool; _bool = b; }
        JsonValue(double n) { Kind = JsonKind.Number; _number = n; }
        JsonValue(string s) { Kind = JsonKind.String; _string = s; }
        JsonValue(List<JsonValue> a) { Kind = JsonKind.Array; _array = a; }
        JsonValue(Dictionary<string, JsonValue> o) { Kind = JsonKind.Object; _object = o; }

        public static JsonValue NewBool(bool b) => new JsonValue(b);
        public static JsonValue NewNumber(double n) => new JsonValue(n);
        public static JsonValue NewString(string s) => s == null ? Null : new JsonValue(s);
        public static JsonValue NewArray(List<JsonValue> items) => new JsonValue(items ?? new List<JsonValue>());
        public static JsonValue NewObject(Dictionary<string, JsonValue> members) => new JsonValue(members ?? new Dictionary<string, JsonValue>());

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        // ---- scalar accessors (lenient) -----------------------------------

        public bool AsBool(bool fallback = false)
        {
            switch (Kind)
            {
                case JsonKind.Bool: return _bool;
                case JsonKind.Number: return _number != 0;
                default: return fallback;
            }
        }

        public double AsDouble(double fallback = 0)
            => Kind == JsonKind.Number ? _number : fallback;

        public float AsFloat(float fallback = 0)
            => Kind == JsonKind.Number ? (float)_number : fallback;

        public int AsInt(int fallback = 0)
            => Kind == JsonKind.Number ? (int)Math.Round(_number) : fallback;

        public string AsString(string fallback = null)
        {
            switch (Kind)
            {
                case JsonKind.String: return _string;
                case JsonKind.Number: return _number.ToString(CultureInfo.InvariantCulture);
                case JsonKind.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        // ---- container access (lenient) -----------------------------------

        /// <summary>Array element count, or object member count, else 0.</summary>
        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array) return _array.Count;
                if (Kind == JsonKind.Object) return _object.Count;
                return 0;
            }
        }

        /// <summary>Array elements in order. Empty for non-arrays.</summary>
        public IReadOnlyList<JsonValue> Items
            => _array ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();

        /// <summary>Object members. Empty for non-objects.</summary>
        public IEnumerable<KeyValuePair<string, JsonValue>> Members
            => _object ?? EmptyMembers;

        static readonly Dictionary<string, JsonValue> EmptyMembers = new Dictionary<string, JsonValue>();

        /// <summary>Object key lookup. Returns <see cref="Null"/> if absent or not an object.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_object != null && key != null && _object.TryGetValue(key, out var v)) return v;
                return Null;
            }
        }

        /// <summary>Array index. Returns <see cref="Null"/> if out of range or not an array.</summary>
        public JsonValue this[int index]
        {
            get
            {
                if (_array != null && index >= 0 && index < _array.Count) return _array[index];
                return Null;
            }
        }

        public bool Has(string key)
            => _object != null && key != null && _object.ContainsKey(key);

        public bool TryGet(string key, out JsonValue value)
        {
            if (_object != null && key != null && _object.TryGetValue(key, out value)) return true;
            value = Null;
            return false;
        }
    }
}
