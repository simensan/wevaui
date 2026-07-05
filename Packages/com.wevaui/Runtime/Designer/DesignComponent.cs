using System.Collections.Generic;

namespace Weva.Designer
{
    /// <summary>
    /// A reusable component definition: a template subtree plus declared props and
    /// variants. Instances (<see cref="DesignNode.ComponentRef"/>) reference it by name;
    /// editing the component updates every instance. This replaces CSS classes as the
    /// reuse mechanism and sidesteps the cascade entirely.
    ///
    /// Props are <c>$name</c> placeholders the template's string fields (text / fill /
    /// colors / shadow) reference; the compiler substitutes effective values
    /// (defaults ⊕ variant ⊕ instance overrides) when it expands an instance. The
    /// template may mark one node as a slot (<see cref="DesignNode.IsSlot"/>) to receive
    /// the instance's children.
    /// </summary>
    public sealed class DesignComponent
    {
        public string Name;
        public DesignNode Template;

        /// <summary>Prop name → default value.</summary>
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>();

        /// <summary>Variant name → prop overrides for that variant.</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Variants
            = new Dictionary<string, Dictionary<string, string>>();

        public DesignComponent() { }

        public DesignComponent(string name, DesignNode template)
        {
            Name = name;
            Template = template;
        }

        public DesignComponent Prop(string name, string defaultValue)
        {
            Props[name] = defaultValue;
            return this;
        }

        public DesignComponent Variant(string variantName, Dictionary<string, string> props)
        {
            Variants[variantName] = props;
            return this;
        }
    }
}
