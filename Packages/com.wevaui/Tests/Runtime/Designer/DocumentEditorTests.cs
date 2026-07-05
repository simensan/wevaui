using NUnit.Framework;
using Weva.Designer;
using Weva.Designer.Editing;

namespace Weva.Tests.Designer
{
    /// <summary>
    /// Coverage for the undo/redo command history — the single write-path into the
    /// Design Document. Verifies reversibility of property + structural edits,
    /// coalescing (a drag = one undo), batches, redo invalidation, dirty/version
    /// tracking, and that mutations actually reach the compiled output.
    /// </summary>
    public class DocumentEditorTests
    {
        static (DocumentEditor ed, DesignNode root) NewEditor()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            return (new DocumentEditor(new DesignDocument(root)), root);
        }

        // --- Property edits ---

        [Test]
        public void Edit_then_undo_restores_then_redo_reapplies()
        {
            var (ed, root) = NewEditor();
            ed.SetGap(root, 16);
            Assert.That(root.Gap.Px, Is.EqualTo(16));

            ed.Undo();
            Assert.That(root.Gap.Px, Is.EqualTo(0));

            ed.Redo();
            Assert.That(root.Gap.Px, Is.EqualTo(16));
        }

        [Test]
        public void Can_undo_and_redo_flags_track_state()
        {
            var (ed, root) = NewEditor();
            Assert.That(ed.CanUndo, Is.False);
            Assert.That(ed.CanRedo, Is.False);

            ed.SetFill(root, "#fff");
            Assert.That(ed.CanUndo, Is.True);
            Assert.That(ed.CanRedo, Is.False);

            ed.Undo();
            Assert.That(ed.CanUndo, Is.False);
            Assert.That(ed.CanRedo, Is.True);
        }

        [Test]
        public void Undo_redo_labels_are_exposed()
        {
            var (ed, root) = NewEditor();
            ed.SetRadius(root, 8);
            Assert.That(ed.NextUndoLabel, Is.EqualTo("Set radius"));
            ed.Undo();
            Assert.That(ed.NextRedoLabel, Is.EqualTo("Set radius"));
        }

        [Test]
        public void New_edit_clears_the_redo_stack()
        {
            var (ed, root) = NewEditor();
            ed.SetGap(root, 10);
            ed.Undo();
            Assert.That(ed.CanRedo, Is.True);

            ed.SetFill(root, "#000");
            Assert.That(ed.CanRedo, Is.False);
        }

        // --- Coalescing ---

        [Test]
        public void Consecutive_same_target_edits_coalesce_to_one_undo()
        {
            var (ed, root) = NewEditor();
            ed.SetGap(root, 4);
            ed.SetGap(root, 8);
            ed.SetGap(root, 12);
            Assert.That(root.Gap.Px, Is.EqualTo(12));

            ed.Undo(); // single step back to the original
            Assert.That(root.Gap.Px, Is.EqualTo(0));
            Assert.That(ed.CanUndo, Is.False);

            ed.Redo();
            Assert.That(root.Gap.Px, Is.EqualTo(12));
        }

        [Test]
        public void Different_properties_do_not_coalesce()
        {
            var (ed, root) = NewEditor();
            ed.SetGap(root, 8);
            ed.SetFill(root, "#abc");

            ed.Undo();
            Assert.That(root.Fill, Is.Null);
            Assert.That(root.Gap.Px, Is.EqualTo(8)); // gap still applied — separate undo step
        }

        [Test]
        public void Same_property_on_different_nodes_does_not_coalesce()
        {
            var (ed, root) = NewEditor();
            var a = new DesignNode("a");
            var b = new DesignNode("b");
            ed.AppendChild(root, a);
            ed.AppendChild(root, b);
            ed.SetGap(a, 5);
            ed.SetGap(b, 9);

            ed.Undo();
            Assert.That(b.Gap.Px, Is.EqualTo(0));
            Assert.That(a.Gap.Px, Is.EqualTo(5));
        }

        // --- Structural edits ---

        [Test]
        public void Append_child_is_undoable()
        {
            var (ed, root) = NewEditor();
            var child = new DesignNode("child");
            ed.AppendChild(root, child);
            Assert.That(root.Children, Has.Count.EqualTo(1));

            ed.Undo();
            Assert.That(root.Children, Has.Count.EqualTo(0));

            ed.Redo();
            Assert.That(root.Children, Has.Count.EqualTo(1));
            Assert.That(root.Children[0], Is.SameAs(child));
        }

        [Test]
        public void Remove_child_is_undoable_and_restores_position()
        {
            var (ed, root) = NewEditor();
            var a = new DesignNode("a");
            var b = new DesignNode("b");
            var c = new DesignNode("c");
            ed.AppendChild(root, a);
            ed.AppendChild(root, b);
            ed.AppendChild(root, c);

            ed.RemoveChild(root, b);
            Assert.That(root.Children, Has.Count.EqualTo(2));

            ed.Undo();
            Assert.That(root.Children, Has.Count.EqualTo(3));
            Assert.That(root.Children[1], Is.SameAs(b)); // restored to original index
        }

        [Test]
        public void Move_child_is_undoable()
        {
            var (ed, root) = NewEditor();
            var a = new DesignNode("a");
            var b = new DesignNode("b");
            var c = new DesignNode("c");
            ed.AppendChild(root, a);
            ed.AppendChild(root, b);
            ed.AppendChild(root, c);

            ed.MoveChild(root, 0, 2); // a to the end → b, c, a
            Assert.That(root.Children[2], Is.SameAs(a));

            ed.Undo();
            Assert.That(root.Children[0], Is.SameAs(a));
            Assert.That(root.Children[2], Is.SameAs(c));
        }

        // --- MoveNode (drag-and-drop primitive) ---

        static (DocumentEditor ed, DesignNode root, DesignNode a, DesignNode b, DesignNode fA, DesignNode fB) NewTree()
        {
            // root
            //  ├ frameA ─ a
            //  └ frameB ─ b
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            var ed = new DocumentEditor(new DesignDocument(root));
            var fA = new DesignNode("frameA") { Layout = LayoutMode.Column };
            var fB = new DesignNode("frameB") { Layout = LayoutMode.Column };
            var a = new DesignNode("a");
            var b = new DesignNode("b");
            ed.AppendChild(root, fA);
            ed.AppendChild(root, fB);
            ed.AppendChild(fA, a);
            ed.AppendChild(fB, b);
            ed.ClearHistory(); // start the move tests with a clean undo stack
            return (ed, root, a, b, fA, fB);
        }

        [Test]
        public void MoveNode_reparents_across_parents()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            bool ok = ed.MoveNode(a, fB, 0); // move `a` into frameB before `b`
            Assert.That(ok, Is.True);
            Assert.That(fA.Children, Has.Count.EqualTo(0));
            Assert.That(fB.Children, Has.Count.EqualTo(2));
            Assert.That(fB.Children[0], Is.SameAs(a));
            Assert.That(fB.Children[1], Is.SameAs(b));
        }

        [Test]
        public void MoveNode_reparent_is_a_single_undo()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            ed.MoveNode(a, fB, 0);
            ed.Undo(); // one step restores the original tree
            Assert.That(fA.Children, Has.Count.EqualTo(1));
            Assert.That(fA.Children[0], Is.SameAs(a));
            Assert.That(fB.Children, Has.Count.EqualTo(1));
            Assert.That(fB.Children[0], Is.SameAs(b));

            ed.Redo();
            Assert.That(fA.Children, Has.Count.EqualTo(0));
            Assert.That(fB.Children[0], Is.SameAs(a));
        }

        [Test]
        public void MoveNode_reorders_within_same_parent_using_pre_removal_index()
        {
            var root = new DesignNode("root") { Layout = LayoutMode.Column };
            var ed = new DocumentEditor(new DesignDocument(root));
            var a = new DesignNode("a"); var b = new DesignNode("b");
            var c = new DesignNode("c"); var d = new DesignNode("d");
            ed.AppendChild(root, a); ed.AppendChild(root, b);
            ed.AppendChild(root, c); ed.AppendChild(root, d);
            ed.ClearHistory();

            // Drop b (index 1) at the slot after c (caller-visible index 3) → a, c, b, d
            bool ok = ed.MoveNode(b, root, 3);
            Assert.That(ok, Is.True);
            Assert.That(root.Children[0], Is.SameAs(a));
            Assert.That(root.Children[1], Is.SameAs(c));
            Assert.That(root.Children[2], Is.SameAs(b));
            Assert.That(root.Children[3], Is.SameAs(d));

            ed.Undo();
            Assert.That(root.Children[0], Is.SameAs(a));
            Assert.That(root.Children[1], Is.SameAs(b));
            Assert.That(root.Children[2], Is.SameAs(c));
            Assert.That(root.Children[3], Is.SameAs(d));
        }

        [Test]
        public void MoveNode_to_same_slot_is_a_noop()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            bool ok = ed.MoveNode(a, fA, 0); // already the only child at index 0
            Assert.That(ok, Is.False);
            Assert.That(ed.CanUndo, Is.False); // nothing recorded
        }

        [Test]
        public void MoveNode_into_own_descendant_is_rejected()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            // frameA contains a; trying to move frameA *into* a would create a cycle.
            bool ok = ed.MoveNode(fA, a, 0);
            Assert.That(ok, Is.False);
            Assert.That(fA.Children[0], Is.SameAs(a)); // unchanged
            Assert.That(ed.CanUndo, Is.False);
        }

        [Test]
        public void MoveNode_into_itself_is_rejected()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            Assert.That(ed.MoveNode(fA, fA, 0), Is.False);
        }

        [Test]
        public void MoveNode_root_is_rejected()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            Assert.That(ed.MoveNode(root, fA, 0), Is.False);
        }

        [Test]
        public void MoveNode_clamps_out_of_range_index_to_append()
        {
            var (ed, root, a, b, fA, fB) = NewTree();
            bool ok = ed.MoveNode(a, fB, 999); // past the end → appended
            Assert.That(ok, Is.True);
            Assert.That(fB.Children[fB.Children.Count - 1], Is.SameAs(a));
        }

        // --- Flex alignment ---

        [Test]
        public void SetMainAlign_is_undoable()
        {
            var (ed, root) = NewEditor();
            Assert.That(root.MainAlign, Is.EqualTo(MainAlign.Start));
            ed.SetMainAlign(root, MainAlign.SpaceBetween);
            Assert.That(root.MainAlign, Is.EqualTo(MainAlign.SpaceBetween));
            ed.Undo();
            Assert.That(root.MainAlign, Is.EqualTo(MainAlign.Start));
            ed.Redo();
            Assert.That(root.MainAlign, Is.EqualTo(MainAlign.SpaceBetween));
        }

        [Test]
        public void SetCrossAlign_is_undoable_and_reaches_compiled_css()
        {
            var (ed, root) = NewEditor();
            ed.SetCrossAlign(root, CrossAlign.Center);
            var css = new DesignCompiler().Compile(ed.Document).Css;
            Assert.That(css, Does.Contain("align-items: center"));
            ed.Undo();
            Assert.That(root.CrossAlign, Is.EqualTo(CrossAlign.Start));
        }

        // --- Color tokens ---

        [Test]
        public void SetColorToken_adds_then_undo_removes()
        {
            var (ed, root) = NewEditor();
            Assert.That(ed.Document.Tokens.Colors.ContainsKey("brand"), Is.False);

            ed.SetColorToken("brand", "#3b82f6");
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#3b82f6"));

            ed.Undo();
            Assert.That(ed.Document.Tokens.Colors.ContainsKey("brand"), Is.False); // adding a new token undoes to absent

            ed.Redo();
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#3b82f6"));
        }

        [Test]
        public void SetColorToken_recolor_undo_restores_previous_value()
        {
            var (ed, root) = NewEditor();
            ed.Document.Tokens.Color("brand", "#111111"); // pre-existing (not via editor)
            ed.SetColorToken("brand", "#ff0000");
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#ff0000"));

            ed.Undo();
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#111111")); // restored, not removed
        }

        [Test]
        public void SetColorToken_same_value_is_a_noop()
        {
            var (ed, root) = NewEditor();
            ed.SetColorToken("brand", "#abc");
            Assert.That(ed.CanUndo, Is.True);
            ed.Undo();
            Assert.That(ed.CanUndo, Is.False);
            ed.Document.Tokens.Color("brand", "#abc");
            ed.SetColorToken("brand", "#abc"); // unchanged → records nothing
            Assert.That(ed.CanUndo, Is.False);
        }

        [Test]
        public void SetColorToken_consecutive_edits_coalesce_to_one_undo()
        {
            var (ed, root) = NewEditor();
            ed.SetColorToken("brand", "#100");
            ed.SetColorToken("brand", "#200");
            ed.SetColorToken("brand", "#300"); // dragging a colour → one undo step
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#300"));
            ed.Undo();
            Assert.That(ed.Document.Tokens.Colors.ContainsKey("brand"), Is.False); // back to before the first edit
            Assert.That(ed.CanUndo, Is.False);
        }

        [Test]
        public void RemoveColorToken_is_undoable()
        {
            var (ed, root) = NewEditor();
            ed.Document.Tokens.Color("brand", "#3b82f6");
            ed.RemoveColorToken("brand");
            Assert.That(ed.Document.Tokens.Colors.ContainsKey("brand"), Is.False);
            ed.Undo();
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#3b82f6"));
        }

        [Test]
        public void RemoveColorToken_missing_is_a_noop()
        {
            var (ed, root) = NewEditor();
            ed.RemoveColorToken("nope");
            Assert.That(ed.CanUndo, Is.False);
        }

        [Test]
        public void RenameColorToken_rewrites_fill_and_textcolor_refs_undoably()
        {
            var (ed, root) = NewEditor();
            ed.Document.Tokens.Color("brand", "#3b82f6");
            var box = new DesignNode("box") { Fill = "{brand}" };
            var label = new DesignNode("label") { Text = "hi", TextColor = "{brand}" };
            ed.AppendChild(root, box);
            ed.AppendChild(box, label);
            root.Fill = "#000"; // a non-ref fill — must be left alone

            ed.RenameColorToken("brand", "primary");
            Assert.That(ed.Document.Tokens.Colors.ContainsKey("brand"), Is.False);
            Assert.That(ed.Document.Tokens.Colors["primary"], Is.EqualTo("#3b82f6"));
            Assert.That(box.Fill, Is.EqualTo("{primary}"));
            Assert.That(label.TextColor, Is.EqualTo("{primary}"));
            Assert.That(root.Fill, Is.EqualTo("#000")); // untouched

            ed.Undo();
            Assert.That(ed.Document.Tokens.Colors["brand"], Is.EqualTo("#3b82f6"));
            Assert.That(ed.Document.Tokens.Colors.ContainsKey("primary"), Is.False);
            Assert.That(box.Fill, Is.EqualTo("{brand}"));
            Assert.That(label.TextColor, Is.EqualTo("{brand}"));
        }

        [Test]
        public void RenameColorToken_rewrites_state_overrides()
        {
            var (ed, root) = NewEditor();
            ed.Document.Tokens.Color("brand", "#3b82f6");
            var box = new DesignNode("box");
            ed.AppendChild(root, box);
            ed.SetStateFill(box, InteractionState.Hover, "{brand}");

            ed.RenameColorToken("brand", "accent");
            Assert.That(box.GetState(InteractionState.Hover).Fill, Is.EqualTo("{accent}"));

            ed.Undo();
            Assert.That(box.GetState(InteractionState.Hover).Fill, Is.EqualTo("{brand}"));
        }

        [Test]
        public void RenameColorToken_to_taken_or_missing_name_is_a_noop()
        {
            var (ed, root) = NewEditor();
            ed.Document.Tokens.Color("a", "#1");
            ed.Document.Tokens.Color("b", "#2");
            ed.RenameColorToken("a", "b");   // dest taken
            Assert.That(ed.CanUndo, Is.False);
            Assert.That(ed.Document.Tokens.Colors["a"], Is.EqualTo("#1"));
            ed.RenameColorToken("missing", "c"); // source missing
            Assert.That(ed.CanUndo, Is.False);
        }

        // --- Batch ---

        [Test]
        public void Batch_groups_edits_into_a_single_undo()
        {
            var (ed, root) = NewEditor();
            ed.BeginBatch("Style");
            ed.SetFill(root, "#123");
            ed.SetRadius(root, 6);
            ed.SetOpacity(root, 0.8);
            ed.EndBatch();

            Assert.That(root.Fill, Is.EqualTo("#123"));

            ed.Undo(); // one step reverts all three
            Assert.That(root.Fill, Is.Null);
            Assert.That(root.Radius.Px, Is.EqualTo(0));
            Assert.That(root.Opacity, Is.EqualTo(1));
            Assert.That(ed.CanUndo, Is.False);
        }

        [Test]
        public void Empty_batch_does_not_pollute_history()
        {
            var (ed, _) = NewEditor();
            ed.BeginBatch("nothing");
            ed.EndBatch();
            Assert.That(ed.CanUndo, Is.False);
        }

        // --- Dirty / version / events ---

        [Test]
        public void Editing_marks_dirty_until_saved()
        {
            var (ed, root) = NewEditor();
            Assert.That(ed.IsDirty, Is.False);

            ed.SetGap(root, 4);
            Assert.That(ed.IsDirty, Is.True);

            ed.MarkSaved();
            Assert.That(ed.IsDirty, Is.False);
        }

        [Test]
        public void Version_increments_on_each_applied_change()
        {
            var (ed, root) = NewEditor();
            int v0 = ed.Version;
            ed.SetGap(root, 4);
            ed.Undo();
            Assert.That(ed.Version, Is.GreaterThan(v0 + 1)); // edit + undo both bump
        }

        [Test]
        public void Changed_event_fires_on_mutation()
        {
            var (ed, root) = NewEditor();
            int fired = 0;
            ed.Changed += () => fired++;
            ed.SetGap(root, 4);
            ed.Undo();
            Assert.That(fired, Is.EqualTo(2));
        }

        // --- Integration with the compiler ---

        [Test]
        public void Mutations_reach_the_compiled_output()
        {
            var (ed, root) = NewEditor();
            var child = new DesignNode("card");
            ed.AppendChild(root, child);
            ed.SetFill(child, "#ff0000");

            Assert.That(ed.Document.Compile().Css, Does.Contain("background: #ff0000"));

            ed.Undo();
            Assert.That(ed.Document.Compile().Css, Does.Not.Contain("background: #ff0000"));
        }
    }
}
