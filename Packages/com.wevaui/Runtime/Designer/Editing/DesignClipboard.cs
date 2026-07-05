namespace Weva.Designer.Editing
{
    /// <summary>
    /// Copy / cut / paste of node subtrees. Holds a detached deep copy, so the payload
    /// is a snapshot — later edits to the source (or pasting multiple times) never alias
    /// shared state. Paste/cut route through <see cref="DocumentEditor"/> so they are
    /// single undo steps.
    ///
    /// This is the in-memory model (headless-testable). The Unity editor layer bridges
    /// it to the OS clipboard via the text serializer for cross-instance copy/paste.
    /// </summary>
    public sealed class DesignClipboard
    {
        DesignNode _payload;

        public bool HasContent => _payload != null;

        /// <summary>Capture a detached snapshot of <paramref name="node"/>'s subtree.</summary>
        public void Copy(DesignNode node)
        {
            _payload = node?.Clone();
        }

        /// <summary>Copy then remove (single undo step for the removal).</summary>
        public void Cut(DocumentEditor editor, DesignNode parent, DesignNode child)
        {
            if (child == null) return;
            Copy(child);
            editor.RemoveChild(parent, child);
        }

        /// <summary>Paste a fresh copy into <paramref name="parent"/> at <paramref name="index"/>. Returns it.</summary>
        public DesignNode Paste(DocumentEditor editor, DesignNode parent, int index)
        {
            if (_payload == null || parent == null) return null;
            DesignNode clone = _payload.Clone(); // fresh copy each paste
            editor.InsertChild(parent, clone, index);
            return clone;
        }

        /// <summary>Paste appended as the last child of <paramref name="parent"/>.</summary>
        public DesignNode PasteInto(DocumentEditor editor, DesignNode parent)
        {
            if (parent == null) return null;
            return Paste(editor, parent, parent.Children.Count);
        }

        public void Clear() => _payload = null;
    }
}
