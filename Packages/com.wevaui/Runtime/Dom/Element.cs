using System.Collections.Generic;
using Weva.Reactive;

namespace Weva.Dom {
    public sealed class Element : Node {
        public string TagName { get; }
        public AttributeMap Attributes { get; }

        public Element(string tagName) {
            TagName = tagName;
            Attributes = new AttributeMap();
            Attributes.OnChanged = OnAttributeChanged;
        }

        public string GetAttribute(string name) => Attributes[name];

        internal string GetAttributeSource(string name) => Attributes.Source(name);

        public void SetAttribute(string name, string value) {
            Attributes[name] = value;
        }

        internal void SetAttributePreservingSource(string name, string value) {
            Attributes.SetValuePreservingSource(name, value);
        }

        public bool RemoveAttribute(string name) {
            return Attributes.Remove(name);
        }

        public bool HasAttribute(string name) => Attributes.Contains(name);

        public string Id => Attributes["id"];
        public string ClassName => Attributes["class"];

        // Shared so each ClassList enumeration doesn't allocate a fresh delimiter array.
        static readonly char[] s_ClassListSeparators = { ' ', '\t', '\n', '\r', '\f' };

        public IEnumerable<string> ClassList {
            get {
                var c = ClassName;
                if (string.IsNullOrEmpty(c)) yield break;
                foreach (var s in c.Split(s_ClassListSeparators, System.StringSplitOptions.RemoveEmptyEntries)) {
                    yield return s;
                }
            }
        }

        void OnAttributeChanged(string name, string oldValue, string newValue) {
            BumpVersion();
            DomMutation m;
            if (oldValue == null) {
                m = DomMutation.AttributeAdded(this, name, newValue);
            } else if (newValue == null) {
                m = DomMutation.AttributeRemoved(this, name, oldValue);
            } else {
                m = DomMutation.AttributeChanged(this, name, oldValue, newValue);
            }
            RaiseMutationBubbling(m);
        }
    }
}
