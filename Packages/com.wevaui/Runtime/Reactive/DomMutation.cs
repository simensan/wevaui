using Weva.Dom;

namespace Weva.Reactive {
    public readonly struct DomMutation {
        public readonly Node Target;
        public readonly Node Subject;
        public readonly DomMutationKind Kind;
        public readonly string AttributeName;
        public readonly string OldValue;
        public readonly string NewValue;

        public DomMutation(Node target, Node subject, DomMutationKind kind, string attributeName, string oldValue, string newValue) {
            Target = target;
            Subject = subject;
            Kind = kind;
            AttributeName = attributeName;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public static DomMutation ChildAdded(Node parent, Node child) =>
            new DomMutation(parent, child, DomMutationKind.ChildAdded, null, null, null);

        public static DomMutation ChildRemoved(Node parent, Node child) =>
            new DomMutation(parent, child, DomMutationKind.ChildRemoved, null, null, null);

        public static DomMutation AttributeAdded(Node target, string name, string newValue) =>
            new DomMutation(target, target, DomMutationKind.AttributeAdded, name, null, newValue);

        public static DomMutation AttributeRemoved(Node target, string name, string oldValue) =>
            new DomMutation(target, target, DomMutationKind.AttributeRemoved, name, oldValue, null);

        public static DomMutation AttributeChanged(Node target, string name, string oldValue, string newValue) =>
            new DomMutation(target, target, DomMutationKind.AttributeChanged, name, oldValue, newValue);

        public static DomMutation TextChanged(Node target, string oldValue, string newValue) =>
            new DomMutation(target, target, DomMutationKind.TextChanged, null, oldValue, newValue);

        public override string ToString() {
            var targetName = DescribeNode(Target);
            switch (Kind) {
                case DomMutationKind.ChildAdded:
                    return $"ChildAdded(target={targetName}, subject={DescribeNode(Subject)})";
                case DomMutationKind.ChildRemoved:
                    return $"ChildRemoved(target={targetName}, subject={DescribeNode(Subject)})";
                case DomMutationKind.AttributeAdded:
                    return $"AttributeAdded(target={targetName}, name={AttributeName}, value={Quote(NewValue)})";
                case DomMutationKind.AttributeRemoved:
                    return $"AttributeRemoved(target={targetName}, name={AttributeName}, oldValue={Quote(OldValue)})";
                case DomMutationKind.AttributeChanged:
                    return $"AttributeChanged(target={targetName}, name={AttributeName}, old={Quote(OldValue)}, new={Quote(NewValue)})";
                case DomMutationKind.TextChanged:
                    return $"TextChanged(target={targetName}, old={Quote(OldValue)}, new={Quote(NewValue)})";
                default:
                    return $"DomMutation({Kind})";
            }
        }

        static string DescribeNode(Node n) {
            if (n == null) return "null";
            if (n is Element e) return $"<{e.TagName}>";
            if (n is TextNode) return "#text";
            if (n is Document) return "#document";
            return n.GetType().Name;
        }

        static string Quote(string s) => s == null ? "null" : "\"" + s + "\"";
    }
}
