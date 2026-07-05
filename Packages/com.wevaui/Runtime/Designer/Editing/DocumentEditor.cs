using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Weva.Designer.Editing
{
    /// <summary>
    /// The single write-path into a <see cref="DesignDocument"/>. Every mutation goes
    /// through here so it is undo/redo-able, marks the document dirty, bumps a version
    /// (for recompile/invalidation) and fires <see cref="Changed"/>. The editor GUI,
    /// clipboard and (later) data-binding all build on this — nothing pokes the IR
    /// directly. (Production bar: reversible + persists.)
    ///
    /// Consecutive edits sharing a merge key coalesce into one undo step, so a drag
    /// gesture is a single undo. Batches group unrelated edits into one transaction.
    /// </summary>
    public sealed class DocumentEditor
    {
        public DesignDocument Document { get; }

        readonly List<IEditCommand> _undo = new List<IEditCommand>();
        readonly List<IEditCommand> _redo = new List<IEditCommand>();

        List<IEditCommand> _batch;
        string _batchLabel;

        /// <summary>Monotonic counter; bumps on every applied change (for recompile triggers).</summary>
        public int Version { get; private set; }

        /// <summary>True when there are changes since the last <see cref="MarkSaved"/>.</summary>
        public bool IsDirty { get; private set; }

        /// <summary>Raised after any applied mutation, undo or redo.</summary>
        public event Action Changed;

        public DocumentEditor(DesignDocument document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string NextUndoLabel => CanUndo ? _undo[_undo.Count - 1].Label : null;
        public string NextRedoLabel => CanRedo ? _redo[_redo.Count - 1].Label : null;

        // --- History ---

        public void Undo()
        {
            if (!CanUndo) return;
            IEditCommand cmd = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            cmd.Revert();
            _redo.Add(cmd);
            MarkChanged();
        }

        public void Redo()
        {
            if (!CanRedo) return;
            IEditCommand cmd = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            cmd.Apply();
            _undo.Add(cmd);
            MarkChanged();
        }

        public void ClearHistory()
        {
            _undo.Clear();
            _redo.Clear();
        }

        /// <summary>Mark the current state as the saved baseline (clears the dirty flag).</summary>
        public void MarkSaved() => IsDirty = false;

        // --- Batch / transaction ---

        public void BeginBatch(string label)
        {
            if (_batch != null) throw new InvalidOperationException("batch already open");
            _batch = new List<IEditCommand>();
            _batchLabel = label;
        }

        public void EndBatch()
        {
            if (_batch == null) throw new InvalidOperationException("no batch open");
            List<IEditCommand> commands = _batch;
            string label = _batchLabel;
            _batch = null;
            _batchLabel = null;
            if (commands.Count == 0) return; // nothing recorded — don't pollute history
            _undo.Add(new CompositeCommand(label, commands));
            _redo.Clear();
        }

        // --- Core mutation primitive ---

        /// <summary>
        /// Apply a mutation and record it for undo. If <paramref name="mergeKey"/> is
        /// non-null and matches the most recent command (and no redo is pending), the
        /// edits coalesce into a single undo step.
        /// </summary>
        public void Mutate(string label, string mergeKey, Action apply, Action revert)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            if (revert == null) throw new ArgumentNullException(nameof(revert));

            if (_batch != null)
            {
                apply();
                _batch.Add(new DelegateCommand(label, apply, revert, mergeKey));
                MarkChanged();
                return;
            }

            if (mergeKey != null && _redo.Count == 0 && _undo.Count > 0
                && _undo[_undo.Count - 1] is DelegateCommand top && top.MergeKey == mergeKey)
            {
                apply();
                top.ReplaceApply(apply); // keep original revert; advance to the latest value
                MarkChanged();
                return;
            }

            var cmd = new DelegateCommand(label, apply, revert, mergeKey);
            cmd.Apply();
            _undo.Add(cmd);
            _redo.Clear();
            MarkChanged();
        }

        void MarkChanged()
        {
            Version++;
            IsDirty = true;
            Changed?.Invoke();
        }

        static string Key(string prop, DesignNode n) => prop + ":" + RuntimeHelpers.GetHashCode(n);

        // --- Typed property mutations (coalescing on per-node merge keys) ---

        public void SetName(DesignNode n, string value)
        {
            string old = n.Name;
            Mutate("Rename", Key("name", n), () => n.Name = value, () => n.Name = old);
        }

        public void SetText(DesignNode n, string value)
        {
            string old = n.Text;
            Mutate("Edit text", Key("text", n), () => n.Text = value, () => n.Text = old);
        }

        public void SetLayout(DesignNode n, LayoutMode value)
        {
            LayoutMode old = n.Layout;
            Mutate("Set layout", null, () => n.Layout = value, () => n.Layout = old);
        }

        public void SetWrap(DesignNode n, bool value)
        {
            bool old = n.Wrap;
            Mutate("Set wrap", null, () => n.Wrap = value, () => n.Wrap = old);
        }

        public void SetGridColumns(DesignNode n, int value)
        {
            int old = n.GridColumns;
            Mutate("Set grid columns", Key("gridColumns", n), () => n.GridColumns = value, () => n.GridColumns = old);
        }

        public void SetMainAlign(DesignNode n, MainAlign value)
        {
            MainAlign old = n.MainAlign;
            Mutate("Set main align", null, () => n.MainAlign = value, () => n.MainAlign = old);
        }

        public void SetCrossAlign(DesignNode n, CrossAlign value)
        {
            CrossAlign old = n.CrossAlign;
            Mutate("Set cross align", null, () => n.CrossAlign = value, () => n.CrossAlign = old);
        }

        public void SetGap(DesignNode n, Dim value)
        {
            Dim old = n.Gap;
            Mutate("Set gap", Key("gap", n), () => n.Gap = value, () => n.Gap = old);
        }

        public void SetFill(DesignNode n, string value)
        {
            string old = n.Fill;
            Mutate("Set fill", Key("fill", n), () => n.Fill = value, () => n.Fill = old);
        }

        public void SetStroke(DesignNode n, string value)
        {
            string old = n.Stroke;
            Mutate("Set stroke", Key("stroke", n), () => n.Stroke = value, () => n.Stroke = old);
        }

        public void SetStrokeWidth(DesignNode n, double value)
        {
            double old = n.StrokeWidth;
            Mutate("Set stroke width", Key("strokeWidth", n), () => n.StrokeWidth = value, () => n.StrokeWidth = old);
        }

        public void SetBackgroundImage(DesignNode n, string url)
        {
            string old = n.BackgroundImage;
            Mutate("Set background image", Key("bgImage", n), () => n.BackgroundImage = url, () => n.BackgroundImage = old);
        }

        public void SetBackgroundSize(DesignNode n, BackgroundSize value)
        {
            BackgroundSize old = n.BackgroundSize;
            Mutate("Set background size", null, () => n.BackgroundSize = value, () => n.BackgroundSize = old);
        }

        public void SetRadius(DesignNode n, Dim value)
        {
            Dim old = n.Radius;
            Mutate("Set radius", Key("radius", n), () => n.Radius = value, () => n.Radius = old);
        }

        /// <summary>Set the four corner radii (null = inherit the uniform Radius); undoable.</summary>
        public void SetCornerRadii(DesignNode n, Dim? tl, Dim? tr, Dim? br, Dim? bl)
        {
            Dim? otl = n.RadiusTopLeft, otr = n.RadiusTopRight, obr = n.RadiusBottomRight, obl = n.RadiusBottomLeft;
            Mutate("Set corner radii", Key("corners", n),
                () => { n.RadiusTopLeft = tl; n.RadiusTopRight = tr; n.RadiusBottomRight = br; n.RadiusBottomLeft = bl; },
                () => { n.RadiusTopLeft = otl; n.RadiusTopRight = otr; n.RadiusBottomRight = obr; n.RadiusBottomLeft = obl; });
        }

        public void SetFontSize(DesignNode n, Dim value)
        {
            Dim old = n.FontSize;
            Mutate("Set font size", Key("fontSize", n), () => n.FontSize = value, () => n.FontSize = old);
        }

        public void SetFontWeight(DesignNode n, FontWeight value)
        {
            FontWeight old = n.FontWeight;
            Mutate("Set font weight", null, () => n.FontWeight = value, () => n.FontWeight = old);
        }

        public void SetItalic(DesignNode n, bool value)
        {
            bool old = n.Italic;
            Mutate("Set italic", null, () => n.Italic = value, () => n.Italic = old);
        }

        public void SetTextAlign(DesignNode n, TextAlign value)
        {
            TextAlign old = n.TextAlign;
            Mutate("Set text align", null, () => n.TextAlign = value, () => n.TextAlign = old);
        }

        public void SetLineHeight(DesignNode n, double value)
        {
            double old = n.LineHeight;
            Mutate("Set line height", Key("lineHeight", n), () => n.LineHeight = value, () => n.LineHeight = old);
        }

        public void SetLetterSpacing(DesignNode n, double value)
        {
            double old = n.LetterSpacing;
            Mutate("Set letter spacing", Key("letterSpacing", n), () => n.LetterSpacing = value, () => n.LetterSpacing = old);
        }

        public void SetTextTransform(DesignNode n, TextTransform value)
        {
            TextTransform old = n.TextTransform;
            Mutate("Set text transform", null, () => n.TextTransform = value, () => n.TextTransform = old);
        }

        public void SetTextDecoration(DesignNode n, TextDecoration value)
        {
            TextDecoration old = n.TextDecoration;
            Mutate("Set text decoration", null, () => n.TextDecoration = value, () => n.TextDecoration = old);
        }

        public void SetShadow(DesignNode n, string value)
        {
            string old = n.Shadow;
            Mutate("Set shadow", Key("shadow", n), () => n.Shadow = value, () => n.Shadow = old);
        }

        public void SetTextShadow(DesignNode n, string value)
        {
            string old = n.TextShadow;
            Mutate("Set text shadow", Key("textShadow", n), () => n.TextShadow = value, () => n.TextShadow = old);
        }

        public void SetOverflow(DesignNode n, Overflow value)
        {
            Overflow old = n.Overflow;
            Mutate("Set overflow", null, () => n.Overflow = value, () => n.Overflow = old);
        }

        public void SetCursor(DesignNode n, Cursor value)
        {
            Cursor old = n.Cursor;
            Mutate("Set cursor", null, () => n.Cursor = value, () => n.Cursor = old);
        }

        public void SetTransition(DesignNode n, double ms)
        {
            double old = n.TransitionMs;
            Mutate("Set transition", Key("transition", n), () => n.TransitionMs = ms, () => n.TransitionMs = old);
        }

        public void SetRotation(DesignNode n, double degrees)
        {
            double old = n.Rotation;
            Mutate("Set rotation", Key("rotation", n), () => n.Rotation = degrees, () => n.Rotation = old);
        }

        public void SetScale(DesignNode n, double scale)
        {
            double old = n.Scale;
            Mutate("Set scale", Key("scale", n), () => n.Scale = scale, () => n.Scale = old);
        }

        public void SetOpacity(DesignNode n, double value)
        {
            double old = n.Opacity;
            Mutate("Set opacity", Key("opacity", n), () => n.Opacity = value, () => n.Opacity = old);
        }

        public void SetWidthMode(DesignNode n, SizeMode value)
        {
            SizeMode old = n.WidthMode;
            Mutate("Set width sizing", null, () => n.WidthMode = value, () => n.WidthMode = old);
        }

        public void SetHeightMode(DesignNode n, SizeMode value)
        {
            SizeMode old = n.HeightMode;
            Mutate("Set height sizing", null, () => n.HeightMode = value, () => n.HeightMode = old);
        }

        public void SetMinWidth(DesignNode n, double value)
        {
            double old = n.MinWidth;
            Mutate("Set min width", Key("minWidth", n), () => n.MinWidth = value, () => n.MinWidth = old);
        }

        public void SetMaxWidth(DesignNode n, double value)
        {
            double old = n.MaxWidth;
            Mutate("Set max width", Key("maxWidth", n), () => n.MaxWidth = value, () => n.MaxWidth = old);
        }

        public void SetMinHeight(DesignNode n, double value)
        {
            double old = n.MinHeight;
            Mutate("Set min height", Key("minHeight", n), () => n.MinHeight = value, () => n.MinHeight = old);
        }

        public void SetMaxHeight(DesignNode n, double value)
        {
            double old = n.MaxHeight;
            Mutate("Set max height", Key("maxHeight", n), () => n.MaxHeight = value, () => n.MaxHeight = old);
        }

        public void SetAspectRatio(DesignNode n, double value)
        {
            double old = n.AspectRatio;
            Mutate("Set aspect ratio", Key("aspectRatio", n), () => n.AspectRatio = value, () => n.AspectRatio = old);
        }

        public void SetPosition(DesignNode n, Position value)
        {
            Position old = n.Position;
            Mutate("Set position", null, () => n.Position = value, () => n.Position = old);
        }

        /// <summary>Set the four edge offsets of an absolute node (null = unpin that edge); undoable.</summary>
        public void SetOffsets(DesignNode n, Dim? top, Dim? right, Dim? bottom, Dim? left)
        {
            Dim? ot = n.OffTop, or = n.OffRight, ob = n.OffBottom, ol = n.OffLeft;
            Mutate("Set offsets", Key("offsets", n),
                () => { n.OffTop = top; n.OffRight = right; n.OffBottom = bottom; n.OffLeft = left; },
                () => { n.OffTop = ot; n.OffRight = or; n.OffBottom = ob; n.OffLeft = ol; });
        }

        public void SetPadding(DesignNode n, Dim t, Dim r, Dim b, Dim l)
        {
            Dim ot = n.PadTop, or = n.PadRight, ob = n.PadBottom, ol = n.PadLeft;
            Mutate("Set padding", Key("padding", n),
                () => { n.PadTop = t; n.PadRight = r; n.PadBottom = b; n.PadLeft = l; },
                () => { n.PadTop = ot; n.PadRight = or; n.PadBottom = ob; n.PadLeft = ol; });
        }

        // --- Interactive-state mutations ---

        /// <summary>Edit a node's override for an interactive state (undoable). Creates it if absent.</summary>
        public void MutateState(DesignNode n, InteractionState s, System.Action<StateStyle> edit)
        {
            StateStyle old = n.GetState(s)?.Clone();
            Mutate("Edit " + s + " state", null,
                () => edit(n.State(s)),
                () => n.SetStateStyle(s, old));
        }

        public void SetStateFill(DesignNode n, InteractionState s, string value)
            => MutateState(n, s, st => st.Fill = value);

        public void SetStateTextColor(DesignNode n, InteractionState s, string value)
            => MutateState(n, s, st => st.TextColor = value);

        public void SetStateShadow(DesignNode n, InteractionState s, string value)
            => MutateState(n, s, st => st.Shadow = value);

        public void SetStateStroke(DesignNode n, InteractionState s, string value)
            => MutateState(n, s, st => st.Stroke = value);

        public void SetStateStrokeWidth(DesignNode n, InteractionState s, double? value)
            => MutateState(n, s, st => st.StrokeWidth = value);

        public void SetStateRadius(DesignNode n, InteractionState s, Dim value)
            => MutateState(n, s, st => st.Radius = value);

        public void SetStateTextDecoration(DesignNode n, InteractionState s, TextDecoration? value)
            => MutateState(n, s, st => st.TextDecoration = value);

        public void SetStateFontWeight(DesignNode n, InteractionState s, FontWeight? value)
            => MutateState(n, s, st => st.FontWeight = value);

        public void SetStateOpacity(DesignNode n, InteractionState s, double value)
            => MutateState(n, s, st => st.Opacity = value);

        /// <summary>Remove a node's override for a state entirely (undoable).</summary>
        public void ClearState(DesignNode n, InteractionState s)
        {
            StateStyle old = n.GetState(s)?.Clone();
            if (old == null) return;
            Mutate("Clear " + s + " state", null, () => n.SetStateStyle(s, null), () => n.SetStateStyle(s, old));
        }

        // --- Data-binding mutations ---

        /// <summary>Edit a node's data binding (undoable). Creates it if absent, drops it if left empty.</summary>
        public void MutateBinding(DesignNode n, System.Action<NodeBinding> edit)
        {
            NodeBinding old = n.Binding?.Clone();
            Mutate("Edit binding", null,
                () => { edit(n.Bind()); if (n.Binding != null && n.Binding.IsEmpty) n.Binding = null; },
                () => n.Binding = old);
        }

        public void SetTextBind(DesignNode n, string path) => MutateBinding(n, b => b.Text = path);
        public void SetRepeat(DesignNode n, string each, string key = null)
            => MutateBinding(n, b => { b.RepeatEach = each; b.RepeatKey = key; });
        public void BindClass(DesignNode n, string className, string path) => MutateBinding(n, b => b.BindClass(className, path));
        public void BindEvent(DesignNode n, string eventName, string method) => MutateBinding(n, b => b.BindEvent(eventName, method));

        /// <summary>Remove all data binding from a node (undoable).</summary>
        public void ClearBinding(DesignNode n)
        {
            NodeBinding old = n.Binding?.Clone();
            if (old == null) return;
            Mutate("Clear binding", null, () => n.Binding = null, () => n.Binding = old);
        }

        // --- Component-instance mutations ---

        /// <summary>Add an instance of a component as a child of <paramref name="parent"/> (undoable).</summary>
        public DesignNode AddInstance(DesignNode parent, string componentName)
        {
            var inst = new DesignNode { ComponentRef = componentName };
            AppendChild(parent, inst);
            return inst;
        }

        public void SetVariant(DesignNode n, string variant)
        {
            string old = n.Variant;
            Mutate("Set variant", Key("variant", n), () => n.Variant = variant, () => n.Variant = old);
        }

        public void SetInstanceProp(DesignNode n, string name, string value)
        {
            Dictionary<string, string> old = n.Props != null ? new Dictionary<string, string>(n.Props) : null;
            Mutate("Set prop", Key("prop:" + name, n),
                () => n.SetProp(name, value),
                () => n.Props = old);
        }

        // --- Design-token mutations ---

        /// <summary>Add or recolor a named color token (undoable). Consecutive edits to the same
        /// token coalesce into one undo step, so dragging a token's colour is a single undo.</summary>
        public void SetColorToken(string name, string css)
        {
            if (string.IsNullOrEmpty(name)) return;
            Dictionary<string, string> colors = Document.Tokens.Colors;
            bool existed = colors.TryGetValue(name, out string old);
            if (existed && old == css) return;
            Mutate("Set color token", "colortoken:" + name,
                () => colors[name] = css,
                () => { if (existed) colors[name] = old; else colors.Remove(name); });
        }

        /// <summary>Remove a color token (undoable). Existing <c>{name}</c> references are left
        /// intact and fall back to the unknown-token sentinel until the token is re-added.</summary>
        public void RemoveColorToken(string name)
        {
            Dictionary<string, string> colors = Document.Tokens.Colors;
            if (!colors.TryGetValue(name, out string old)) return;
            Mutate("Remove color token", null,
                () => colors.Remove(name),
                () => colors[name] = old);
        }

        /// <summary>Rename a color token and rewrite every <c>{oldName}</c> reference in node
        /// fills / text colours (including interactive-state overrides) to <c>{newName}</c>, as a
        /// single undoable step. No-op if names are empty/equal, the source is missing, or the
        /// destination name is already taken.</summary>
        public void RenameColorToken(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;
            Dictionary<string, string> colors = Document.Tokens.Colors;
            if (!colors.TryGetValue(oldName, out string css) || colors.ContainsKey(newName)) return;
            string oldRef = "{" + oldName + "}", newRef = "{" + newName + "}";
            var setNew = new List<Action>();
            var setOld = new List<Action>();
            CollectTokenRefs(Document.Root, oldRef, newRef, setNew, setOld);
            Mutate("Rename color token", null,
                () => { colors.Remove(oldName); colors[newName] = css; foreach (Action a in setNew) a(); },
                () => { colors.Remove(newName); colors[oldName] = css; foreach (Action a in setOld) a(); });
        }

        static void CollectTokenRefs(DesignNode n, string oldRef, string newRef, List<Action> setNew, List<Action> setOld)
        {
            if (n == null) return;
            if (n.Fill == oldRef) { DesignNode t = n; setNew.Add(() => t.Fill = newRef); setOld.Add(() => t.Fill = oldRef); }
            if (n.TextColor == oldRef) { DesignNode t = n; setNew.Add(() => t.TextColor = newRef); setOld.Add(() => t.TextColor = oldRef); }
            foreach (InteractionState s in DesignNode.AllStates)
            {
                StateStyle st = n.GetState(s);
                if (st == null) continue;
                if (st.Fill == oldRef) { StateStyle ss = st; setNew.Add(() => ss.Fill = newRef); setOld.Add(() => ss.Fill = oldRef); }
                if (st.TextColor == oldRef) { StateStyle ss = st; setNew.Add(() => ss.TextColor = newRef); setOld.Add(() => ss.TextColor = oldRef); }
            }
            for (int i = 0; i < n.Children.Count; i++) CollectTokenRefs(n.Children[i], oldRef, newRef, setNew, setOld);
        }

        // --- Structural mutations ---

        public void InsertChild(DesignNode parent, DesignNode child, int index)
        {
            if (index < 0 || index > parent.Children.Count) index = parent.Children.Count;
            Mutate("Add element", null,
                () => parent.Children.Insert(index, child),
                () => parent.Children.RemoveAt(index));
        }

        public void AppendChild(DesignNode parent, DesignNode child)
            => InsertChild(parent, child, parent.Children.Count);

        public void RemoveChild(DesignNode parent, DesignNode child)
        {
            int index = parent.Children.IndexOf(child);
            if (index < 0) return;
            Mutate("Delete element", null,
                () => parent.Children.RemoveAt(index),
                () => parent.Children.Insert(index, child));
        }

        /// <summary>Insert a deep copy of <paramref name="child"/> right after it. Returns the copy.</summary>
        public DesignNode Duplicate(DesignNode parent, DesignNode child)
        {
            int index = parent.Children.IndexOf(child);
            if (index < 0) return null;
            DesignNode clone = child.Clone();
            InsertChild(parent, clone, index + 1);
            return clone;
        }

        public void MoveChild(DesignNode parent, int from, int to)
        {
            int count = parent.Children.Count;
            if (from < 0 || from >= count || to < 0 || to >= count || from == to) return;
            Mutate("Reorder", null,
                () => { DesignNode n = parent.Children[from]; parent.Children.RemoveAt(from); parent.Children.Insert(to, n); },
                () => { DesignNode n = parent.Children[to]; parent.Children.RemoveAt(to); parent.Children.Insert(from, n); });
        }

        /// <summary>
        /// Move <paramref name="node"/> to <paramref name="newIndex"/> under
        /// <paramref name="newParent"/> as a single undoable step. Handles both reordering
        /// within a parent and reparenting across parents (the drag-and-drop primitive).
        /// <paramref name="newIndex"/> is the slot in the destination's child list AS GIVEN
        /// (the index callers see before the node is removed); it is clamped, and adjusted for
        /// the removal when the source and destination parent are the same. Returns false
        /// (no-op) for illegal moves: the root, dropping onto itself, or dropping into the
        /// node's own subtree (which would orphan a cycle).
        /// </summary>
        public bool MoveNode(DesignNode node, DesignNode newParent, int newIndex)
        {
            if (node == null || newParent == null) return false;
            if (ReferenceEquals(node, Document.Root)) return false;   // root has no parent to leave
            if (IsInSubtree(newParent, node)) return false;           // self / descendant → cycle
            DesignNode oldParent = FindParent(Document.Root, node);
            if (oldParent == null) return false;
            int oldIndex = oldParent.Children.IndexOf(node);
            if (oldIndex < 0) return false;

            int count = newParent.Children.Count;
            int target = newIndex < 0 ? count : (newIndex > count ? count : newIndex);
            if (ReferenceEquals(oldParent, newParent))
            {
                if (target > oldIndex) target--;     // removing the node first shifts later slots down
                if (target == oldIndex) return false; // dropped back where it started
            }
            int finalTarget = target;
            Mutate("Move element", null,
                () => { oldParent.Children.Remove(node); newParent.Children.Insert(finalTarget, node); },
                () => { newParent.Children.Remove(node); oldParent.Children.Insert(oldIndex, node); });
            return true;
        }

        static DesignNode FindParent(DesignNode root, DesignNode target)
        {
            if (root == null) return null;
            for (int i = 0; i < root.Children.Count; i++)
            {
                if (ReferenceEquals(root.Children[i], target)) return root;
                DesignNode r = FindParent(root.Children[i], target);
                if (r != null) return r;
            }
            return null;
        }

        // True when `candidate` is `subtreeRoot` or any node beneath it.
        static bool IsInSubtree(DesignNode candidate, DesignNode subtreeRoot)
        {
            if (ReferenceEquals(candidate, subtreeRoot)) return true;
            for (int i = 0; i < subtreeRoot.Children.Count; i++)
                if (IsInSubtree(candidate, subtreeRoot.Children[i])) return true;
            return false;
        }
    }
}
