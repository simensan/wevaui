namespace Weva.Css {
    // CSS Properties and Values API Level 1 — `@property` rule.
    // Records the three required descriptors parsed from an at-rule of the form:
    //
    //   @property --my-prop {
    //     syntax: "<length>";
    //     initial-value: 0px;
    //     inherits: false;
    //   }
    //
    // All three descriptors are required; if any is missing the rule is
    // discarded at parse time (this class is not constructed). The parsed
    // rule is handed to AtPropertyRegistry.Register during stylesheet
    // compilation so the cascade can honour typed initial values and the
    // inherits flag.
    public sealed class AtPropertyRule : Rule {
        // The custom property name, including the leading `--`.
        public string Name;
        // The raw syntax string from the `syntax:` descriptor, e.g. `"<length>"`.
        public string Syntax;
        // The raw text of the `initial-value:` descriptor, e.g. `"0px"`.
        public string InitialValue;
        // Whether this property inherits. Corresponds to `inherits: true|false`.
        public bool Inherits;
    }
}
