using System;

namespace Weva.Binding {
    /// <summary>
    /// Marks a field or property as a data-binding target the document keeps in
    /// sync. Apply to a member of a controller to bind it into the UI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class UIBindAttribute : Attribute {
    }
}
