using System;
using System.Collections.Generic;
using Weva.Reactive;

namespace Weva.Dom {
    public abstract class Node : IVersioned {
        readonly List<Node> children = new();

        public Node Parent { get; internal set; }
        public Document OwnerDocument { get; internal set; }
        public IReadOnlyList<Node> Children => children;

        public long Version { get; private set; }

        // Fires after a mutation is applied. Bubbles: handlers attached to ancestors
        // (and therefore to OwnerDocument) see mutations from their entire subtree.
        // Target on the event is always the original mutated node, never the bubbler.
        public event Action<DomMutation> Mutated;

        protected void BumpVersion() {
            Version++;
        }

        protected void RaiseMutationBubbling(DomMutation m) {
            for (var n = this; n != null; n = n.Parent) {
                n.Mutated?.Invoke(m);
            }
        }

        public void AppendChild(Node child) {
            if (child == null) throw new ArgumentNullException(nameof(child));
            if (child == this) throw new InvalidOperationException("Cannot append node to itself.");
            if (HasAncestor(child)) throw new InvalidOperationException("Cannot append an ancestor as a child.");
            if (child.Parent == this) {
                int oldIndex = children.IndexOf(child);
                if (oldIndex < 0) throw new InvalidOperationException("Child parent link is inconsistent.");
                if (oldIndex == children.Count - 1) return;
                children.RemoveAt(oldIndex);
                children.Add(child);
                BumpVersion();
                child.BumpVersion();
                RaiseMutationBubbling(DomMutation.ChildAdded(this, child));
                return;
            }
            child.Parent?.RemoveChild(child);
            child.Parent = this;
            child.OwnerDocument = OwnerDocument;
            children.Add(child);
            PropagateOwnerDocument(child);
            BumpVersion();
            child.BumpVersion();
            RaiseMutationBubbling(DomMutation.ChildAdded(this, child));
        }

        public void InsertBefore(Node child, Node referenceChild) {
            if (child == null) throw new ArgumentNullException(nameof(child));
            if (referenceChild == null) {
                AppendChild(child);
                return;
            }
            if (referenceChild.Parent != this) throw new InvalidOperationException("Reference child is not a child of this node.");
            if (child == referenceChild) return;
            if (child == this) throw new InvalidOperationException("Cannot insert node into itself.");
            if (HasAncestor(child)) throw new InvalidOperationException("Cannot insert an ancestor as a child.");

            if (child.Parent == this) {
                int oldIndex = children.IndexOf(child);
                int referenceIndex = children.IndexOf(referenceChild);
                if (oldIndex < 0) throw new InvalidOperationException("Child parent link is inconsistent.");
                if (referenceIndex < 0) throw new InvalidOperationException("Reference child is no longer attached.");
                if (oldIndex == referenceIndex || oldIndex + 1 == referenceIndex) return;

                children.RemoveAt(oldIndex);
                if (oldIndex < referenceIndex) referenceIndex--;
                children.Insert(referenceIndex, child);
                BumpVersion();
                child.BumpVersion();
                RaiseMutationBubbling(DomMutation.ChildAdded(this, child));
                return;
            }

            child.Parent?.RemoveChild(child);
            int idx = children.IndexOf(referenceChild);
            if (idx < 0) throw new InvalidOperationException("Reference child is no longer attached.");
            child.Parent = this;
            child.OwnerDocument = OwnerDocument;
            children.Insert(idx, child);
            PropagateOwnerDocument(child);
            BumpVersion();
            child.BumpVersion();
            RaiseMutationBubbling(DomMutation.ChildAdded(this, child));
        }

        public bool RemoveChild(Node child) {
            if (child == null) return false;
            int idx = children.IndexOf(child);
            if (idx < 0) return false;
            // Fire BEFORE unlinking so the parent chain is intact for observers and bubbling.
            BumpVersion();
            child.BumpVersion();
            RaiseMutationBubbling(DomMutation.ChildRemoved(this, child));
            children.RemoveAt(idx);
            child.Parent = null;
            // Detach the OwnerDocument from the removed subtree. Without
            // this, descendants of the detached node keep reporting the
            // old document — InvalidationTracker.Detach paths would
            // dispatch into a torn-down doc, and OwnerDocument-keyed
            // lookups confuse orphans with fresh trees. AppendChild
            // re-propagates the destination doc when the orphan is
            // re-attached, so the null state is purely a "currently
            // detached" marker.
            child.OwnerDocument = null;
            PropagateOwnerDocument(child);
            return true;
        }

        bool HasAncestor(Node candidate) {
            for (var n = Parent; n != null; n = n.Parent) {
                if (n == candidate) return true;
            }
            return false;
        }

        static void PropagateOwnerDocument(Node node) {
            foreach (var c in node.Children) {
                c.OwnerDocument = node.OwnerDocument;
                PropagateOwnerDocument(c);
            }
        }
    }
}
