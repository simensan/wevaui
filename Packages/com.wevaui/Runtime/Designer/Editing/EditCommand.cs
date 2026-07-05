using System;
using System.Collections.Generic;

namespace Weva.Designer.Editing
{
    /// <summary>A reversible edit to the Design Document.</summary>
    public interface IEditCommand
    {
        string Label { get; }
        void Apply();
        void Revert();
    }

    /// <summary>
    /// A command defined by a forward (<paramref name="apply"/>) and inverse
    /// (<paramref name="revert"/>) delegate, captured by the editor at mutation time.
    /// <see cref="MergeKey"/> lets consecutive edits to the same target coalesce into
    /// one undo step (e.g. a slider drag = a single undo), see <see cref="DocumentEditor"/>.
    /// </summary>
    public sealed class DelegateCommand : IEditCommand
    {
        public string Label { get; }
        public string MergeKey { get; }

        Action _apply;
        readonly Action _revert;

        public DelegateCommand(string label, Action apply, Action revert, string mergeKey = null)
        {
            Label = label;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _revert = revert ?? throw new ArgumentNullException(nameof(revert));
            MergeKey = mergeKey;
        }

        public void Apply() => _apply();
        public void Revert() => _revert();

        /// <summary>Used by coalescing: keep the original revert, advance the forward action.</summary>
        internal void ReplaceApply(Action apply) => _apply = apply;
    }

    /// <summary>Several commands treated as one undo step (a transaction / batch).</summary>
    public sealed class CompositeCommand : IEditCommand
    {
        public string Label { get; }
        readonly List<IEditCommand> _commands;

        public CompositeCommand(string label, List<IEditCommand> commands)
        {
            Label = label;
            _commands = commands;
        }

        public void Apply()
        {
            for (int i = 0; i < _commands.Count; i++) _commands[i].Apply();
        }

        public void Revert()
        {
            for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Revert();
        }
    }
}
