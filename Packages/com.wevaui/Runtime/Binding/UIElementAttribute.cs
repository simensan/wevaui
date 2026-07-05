using System;

namespace Weva.Binding {
    /// <summary>
    /// Binds a field to the document element with the given id, so the resolved
    /// element is injected into that field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class UIElementAttribute : Attribute {
        /// <summary>The id of the document element to bind this field to.</summary>
        public string Id;

        /// <summary>Bind the annotated field to the element with the given id.</summary>
        public UIElementAttribute(string id) {
            Id = id;
        }
    }
}
