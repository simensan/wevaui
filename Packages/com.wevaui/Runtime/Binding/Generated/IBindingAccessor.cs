using System;
using System.Collections.Generic;

namespace Weva.Binding.Generated {
    // Implemented by types that opt into the IL2CPP-friendly source-generated
    // binding fast path. The Roslyn generator at
    // Packages/com.wevaui/Editor/Generators/UIBindGenerator.cs emits a partial
    // class implementing this interface for every type that declares
    // [UIBind] members or [UIElement] fields. BindingResolver prefers this
    // path over reflection when available; reflection remains the fallback.
    public interface IBindingAccessor {
        // Gets a [UIBind] member's current value, boxed.
        // Returns false if memberName is not a known [UIBind] member.
        bool TryGet(string memberName, out object value);

        // Writes a value to a [UIBind] member after type-checking.
        // Returns false if memberName is unknown OR if value is not assignable
        // to the member's declared type.
        bool TrySet(string memberName, object value);

        // Names of every declared [UIBind] field/property. Stable per type.
        IReadOnlyList<string> BoundMemberNames { get; }

        // Descriptors for every [UIElement(id)] field. Each entry is the id
        // attribute the field is bound to and the runtime type the assigned
        // element must be assignable to.
        IReadOnlyList<ElementBindingDescriptor> ElementBindings { get; }

        // Assigns the provided element to the [UIElement(id)]-tagged field.
        // Returns false if the id is unknown or if element is not assignable
        // to the field's declared type.
        bool TrySetElement(string id, object element);
    }

    public readonly struct ElementBindingDescriptor : IEquatable<ElementBindingDescriptor> {
        public readonly string Id;
        public readonly Type Expected;

        public ElementBindingDescriptor(string id, Type expected) {
            Id = id;
            Expected = expected;
        }

        public bool Equals(ElementBindingDescriptor other) =>
            string.Equals(Id, other.Id, StringComparison.Ordinal) && Expected == other.Expected;

        public override bool Equals(object obj) => obj is ElementBindingDescriptor d && Equals(d);

        public override int GetHashCode() {
            unchecked {
                int h = Id != null ? Id.GetHashCode() : 0;
                h = h * 31 + (Expected != null ? Expected.GetHashCode() : 0);
                return h;
            }
        }

        public static bool operator ==(ElementBindingDescriptor a, ElementBindingDescriptor b) => a.Equals(b);
        public static bool operator !=(ElementBindingDescriptor a, ElementBindingDescriptor b) => !a.Equals(b);
    }
}
