namespace Weva.Designer
{
    /// <summary>
    /// The root of the authoring IR: a tree of <see cref="DesignNode"/> plus the
    /// shared <see cref="DesignTokens"/>. The editor mutates this; the
    /// <see cref="DesignCompiler"/> turns it into Weva HTML/CSS. This is the
    /// keystone of the editor architecture (see WEVA_EDITOR_PLAN.md) — nothing
    /// edits CSS directly; every change flows through this model.
    ///
    /// Components, per-breakpoint overrides and states are later milestones; this
    /// type is intentionally small so those can extend it without churn.
    /// </summary>
    public sealed class DesignDocument
    {
        public DesignNode Root;
        public readonly DesignTokens Tokens = new DesignTokens();

        /// <summary>Reusable component definitions, by name. Instances reference these.</summary>
        public readonly System.Collections.Generic.Dictionary<string, DesignComponent> Components
            = new System.Collections.Generic.Dictionary<string, DesignComponent>();

        public DesignDocument AddComponent(DesignComponent c)
        {
            Components[c.Name] = c;
            return this;
        }

        public DesignDocument() { }

        public DesignDocument(DesignNode root) { Root = root; }

        /// <summary>Compile to Weva HTML/CSS. Convenience wrapper over <see cref="DesignCompiler"/>.</summary>
        public DesignCompileResult Compile() => new DesignCompiler().Compile(this);
    }
}
