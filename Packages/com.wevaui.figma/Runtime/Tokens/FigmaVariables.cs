using System.Collections.Generic;
using Weva.Figma.Json;

namespace Weva.Figma.Tokens
{
    public enum FigmaVariableType
    {
        Unknown,
        Color,
        Float,
        String,
        Bool,
    }

    public sealed class FigmaVariableMode
    {
        public string ModeId;
        public string Name;
    }

    public sealed class FigmaVariableCollection
    {
        public string Id;
        public string Name;
        public string DefaultModeId;
        public readonly List<FigmaVariableMode> Modes = new List<FigmaVariableMode>();
        public readonly List<string> VariableIds = new List<string>();

        public bool IsMultiMode => Modes.Count > 1;
    }

    public sealed class FigmaVariable
    {
        public string Id;
        public string Name;
        public string CollectionId;
        public FigmaVariableType Type = FigmaVariableType.Unknown;
        public readonly List<string> Scopes = new List<string>();

        /// <summary>modeId → raw JSON value (a color object, number, string, bool, or VARIABLE_ALIAS object).</summary>
        public readonly Dictionary<string, JsonValue> ValuesByMode = new Dictionary<string, JsonValue>();
    }

    /// <summary>
    /// The parsed contents of Figma's "Get local variables" response
    /// (<c>GET /v1/files/:key/variables/local</c>) — or any payload that mirrors
    /// its <c>meta.variables</c> / <c>meta.variableCollections</c> shape, such as
    /// a plugin-side export. Parsing is decoupled from the source so both
    /// transports feed the same model.
    /// </summary>
    public sealed class FigmaVariablesDocument
    {
        public readonly Dictionary<string, FigmaVariable> Variables = new Dictionary<string, FigmaVariable>();
        public readonly Dictionary<string, FigmaVariableCollection> Collections = new Dictionary<string, FigmaVariableCollection>();

        public static FigmaVariablesDocument Parse(string json)
            => Parse(JsonParser.Parse(json));

        public static FigmaVariablesDocument Parse(JsonValue root)
        {
            var doc = new FigmaVariablesDocument();
            // Accept the full REST envelope ({status, error, meta:{…}}) or a bare
            // {variables, variableCollections} object.
            JsonValue meta = root.Has("meta") ? root["meta"] : root;

            foreach (var kv in meta["variableCollections"].Members)
            {
                JsonValue c = kv.Value;
                var coll = new FigmaVariableCollection
                {
                    Id = c["id"].AsString(kv.Key),
                    Name = c["name"].AsString(""),
                    DefaultModeId = c["defaultModeId"].AsString(null),
                };
                foreach (JsonValue m in c["modes"].Items)
                {
                    coll.Modes.Add(new FigmaVariableMode
                    {
                        ModeId = m["modeId"].AsString(null),
                        Name = m["name"].AsString(""),
                    });
                }
                foreach (JsonValue vid in c["variableIds"].Items)
                {
                    string id = vid.AsString(null);
                    if (id != null) coll.VariableIds.Add(id);
                }
                if (coll.DefaultModeId == null && coll.Modes.Count > 0)
                    coll.DefaultModeId = coll.Modes[0].ModeId;
                doc.Collections[coll.Id] = coll;
            }

            foreach (var kv in meta["variables"].Members)
            {
                JsonValue v = kv.Value;
                var variable = new FigmaVariable
                {
                    Id = v["id"].AsString(kv.Key),
                    Name = v["name"].AsString(""),
                    CollectionId = v["variableCollectionId"].AsString(null),
                    Type = ParseType(v["resolvedType"].AsString(v["variableType"].AsString(null))),
                };
                foreach (JsonValue s in v["scopes"].Items)
                {
                    string scope = s.AsString(null);
                    if (scope != null) variable.Scopes.Add(scope);
                }
                foreach (var mv in v["valuesByMode"].Members)
                    variable.ValuesByMode[mv.Key] = mv.Value;
                doc.Variables[variable.Id] = variable;
            }

            return doc;
        }

        static FigmaVariableType ParseType(string t)
        {
            switch (t)
            {
                case "COLOR": return FigmaVariableType.Color;
                case "FLOAT": return FigmaVariableType.Float;
                case "STRING": return FigmaVariableType.String;
                case "BOOLEAN": return FigmaVariableType.Bool;
                default: return FigmaVariableType.Unknown;
            }
        }
    }
}
