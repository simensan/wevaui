using Weva.Reactive;

namespace Weva.Dom {
    public sealed class TextNode : Node {
        string data;

        // Parse-time raw text. Stays put when Data is overwritten — the binding
        // scanner reads this to find `{{ }}` markers after Data has been rendered.
        public string Source { get; }

        public string Data {
            get => data;
            set {
                if (data == value) return;
                var old = data;
                data = value;
                BumpVersion();
                RaiseMutationBubbling(DomMutation.TextChanged(this, old, value));
            }
        }

        public TextNode(string data) {
            this.data = data ?? "";
            Source = this.data;
        }
    }
}
