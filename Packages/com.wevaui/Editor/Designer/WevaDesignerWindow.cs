#if WEVA_URP
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using Weva.Designer;
using Weva.Designer.Editing;
using Weva.Designer.Serialization;
using Weva.Designer.Templates;
using Weva.EditorTools.Panels;
using Weva.Events;

namespace Weva.EditorTools.Designer {
    // The Weva visual designer — a non-coder UI editor whose chrome is itself rendered by the
    // Weva engine (WevaEditorPanel). The center canvas shows the user's DesignDocument compiled
    // live to real Weva HTML/CSS (DesignCompiler), so WYSIWYG is literal. The left panel is the
    // layer tree (click to select); the right panel is the inspector for the selected node.
    //
    // Architecture: the editor never edits CSS — it mutates a DesignDocument through the
    // DocumentEditor command layer (undo/redo, dirty tracking). On every change we Repaint; the
    // Html getter re-bakes the full chrome + recompiled canvas reflecting current state. Events
    // (layer clicks, toolbar buttons) bind to the Model controller via on-click handlers; the
    // handler reads the clicked element's data-nid to resolve which node was hit.
    //
    // This is milestone M2 of WEVA_EDITOR_PLAN.md (live read-only canvas + layer tree + select +
    // inspector + undo/redo). Direct-manipulation authoring on the canvas is M3.
    //
    // Window ▸ Weva ▸ Designer
    public sealed class WevaDesignerWindow : WevaEditorPanel {
        [MenuItem("Window/Weva/Designer")]
        static void Open() {
            var w = GetWindow<WevaDesignerWindow>("Weva Designer");
            w.minSize = new UnityEngine.Vector2(720, 460);
        }

        DocumentEditor editor;
        readonly Model model = new Model();
        // Pre-order index of the selected node, recorded during the layer walk. The compiler
        // emits one class `wN` per node in the SAME pre-order, so wN ↔ the layer's nN id —
        // letting the canvas inject a selection outline + click hook onto the matching element.
        int selectedNodeIndex = -1;

        DocumentEditor Editor {
            get {
                if (editor == null) LoadDocument(DesignTemplates.MainMenu());
                return editor;
            }
        }

        // Swap the whole document (template load / new). Detaches the old editor's Changed
        // subscription, wires the new one, and resets the selection to the new root.
        void LoadDocument(DesignDocument doc) {
            if (editor != null) editor.Changed -= Repaint;
            editor = new DocumentEditor(doc);
            model.Ed = editor;
            model.Selected = doc.Root;
            model.Repaint = Repaint;
            model.LoadTemplate = LoadTemplate;
            model.BoxOf = BoxFor;
            model.Save = () => SaveDocument(false);
            model.SaveAs = () => SaveDocument(true);
            model.Open = OpenDocument;
            model.Now = () => EditorApplication.timeSinceStartup;   // for double-click detection
            editor.Changed += Repaint;
            Repaint();
        }

        void LoadTemplate(string name) {
            DesignDocument doc;
            switch (name) {
                case "Blank":      doc = DesignTemplates.Blank(); break;
                case "Combat HUD": doc = DesignTemplates.CombatHud(); break;
                case "Settings":   doc = DesignTemplates.SettingsPanel(); break;
                default:           doc = DesignTemplates.MainMenu(); break;
            }
            LoadDocument(doc);
        }

        // ── Persistence (save / open .json via DesignSerializer) ───────────────────────────────
        // Last path written/read this session — drives Save (vs Save As) and the dialog's
        // starting directory.
        string lastSavePath;

        // Save to lastSavePath if known, else prompt. Returns false if the user cancelled.
        bool SaveDocument(bool forcePrompt) {
            string path = (!forcePrompt && !string.IsNullOrEmpty(lastSavePath)) ? lastSavePath : null;
            if (path == null) {
                string dir = string.IsNullOrEmpty(lastSavePath) ? "" : System.IO.Path.GetDirectoryName(lastSavePath);
                string file = string.IsNullOrEmpty(lastSavePath) ? "design" : System.IO.Path.GetFileNameWithoutExtension(lastSavePath);
                path = EditorUtility.SaveFilePanel("Save Weva Design", dir, file, "json");
                if (string.IsNullOrEmpty(path)) return false;
            }
            try {
                System.IO.File.WriteAllText(path, DesignSerializer.Serialize(Editor.Document));
                lastSavePath = path;
                Editor.MarkSaved();
                Repaint();
                return true;
            } catch (System.Exception ex) {
                EditorUtility.DisplayDialog("Save failed", ex.Message, "OK");
                return false;
            }
        }

        void OpenDocument() {
            string dir = string.IsNullOrEmpty(lastSavePath) ? "" : System.IO.Path.GetDirectoryName(lastSavePath);
            string path = EditorUtility.OpenFilePanel("Open Weva Design", dir, "json");
            if (string.IsNullOrEmpty(path)) return;
            try {
                var doc = DesignSerializer.Deserialize(System.IO.File.ReadAllText(path));
                LoadDocument(doc);
                lastSavePath = path;
                Editor.MarkSaved();   // freshly loaded == clean baseline
            } catch (System.Exception ex) {
                EditorUtility.DisplayDialog("Open failed", "Could not read this design:\n" + ex.Message, "OK");
            }
        }

        protected override object Controller => model;

        // ── Inline name / text editing (IMGUI edit bar) ────────────────────────────────────────
        // A real text field can't live in the Weva chrome: the panel rebuilds the whole document
        // on every keystroke (each edit → Changed → Repaint), so an in-Weva <input> would lose
        // focus and caret every frame. An IMGUI field drawn here keeps its own focus/caret across
        // those rebuilds (its control identity is the OnPanelChrome layout, not the Weva DOM).
        // Keystrokes typed into a focused field are consumed by IMGUI before HandleInput, so they
        // don't leak to the document's key dispatcher.
        // Buffer for the IMGUI token-rename field: edit freely, commit on the Rename button so a
        // rename (which rewrites refs across the tree) is one undo step, not one per keystroke.
        string tokenNameBuffer;
        string tokenNameBufferFor;

        protected override void OnPanelChrome() {
            HandleShortcuts();            // global keyboard shortcuts (consumed before the DOM sees them)
            if (editor == null) return;   // before the first render the document isn't loaded yet

            // Token-edit mode: rename the active token here (the inspector shows its colour picker).
            if (model.EditingToken != null) {
                using (new UnityEngine.GUILayout.HorizontalScope(EditorStyles.toolbar)) {
                    if (tokenNameBufferFor != model.EditingToken) { tokenNameBuffer = model.EditingToken; tokenNameBufferFor = model.EditingToken; }
                    UnityEngine.GUILayout.Label("Token", EditorStyles.miniLabel, UnityEngine.GUILayout.Width(40));
                    tokenNameBuffer = EditorGUILayout.TextField(tokenNameBuffer, EditorStyles.toolbarTextField, UnityEngine.GUILayout.Width(180));
                    if (UnityEngine.GUILayout.Button("Rename", EditorStyles.toolbarButton, UnityEngine.GUILayout.Width(64))) {
                        string nn = tokenNameBuffer?.Trim();
                        if (!string.IsNullOrEmpty(nn) && nn != model.EditingToken) {
                            string old = model.EditingToken;
                            editor.RenameColorToken(old, nn);
                            if (editor.Document.Tokens.Colors.ContainsKey(nn)) model.EditingToken = nn; // followed the rename
                            tokenNameBufferFor = null; // resync next frame
                        }
                    }
                    UnityEngine.GUILayout.FlexibleSpace();
                }
                return;
            }

            // Data-binding text editor: type a path / method / list expression for the selected
            // bind target. IMGUI TextField (focus-stable across rebuilds, like name/text).
            if (model.EditingBind != null) {
                using (new UnityEngine.GUILayout.HorizontalScope(EditorStyles.toolbar)) {
                    UnityEngine.GUILayout.Label(model.EditingBindLabel, EditorStyles.miniLabel, UnityEngine.GUILayout.Width(80));
                    string cur = model.BindCurrent();
                    string nv = EditorGUILayout.TextField(cur, EditorStyles.toolbarTextField, UnityEngine.GUILayout.MinWidth(220));
                    if (nv != cur) model.ApplyBind(nv);
                    if (UnityEngine.GUILayout.Button("Done", EditorStyles.toolbarButton, UnityEngine.GUILayout.Width(48)))
                        model.EditingBind = null;
                    UnityEngine.GUILayout.FlexibleSpace();
                }
                return;
            }

            // Type-in numeric editor: clicking (not dragging) a scrub value arms model.EditingField.
            // A DelayedFloatField keeps focus across the panel's per-keystroke rebuilds (same reason
            // the name/text fields are IMGUI), and commits on Enter / focus-loss.
            if (model.EditingField != null) {
                using (new UnityEngine.GUILayout.HorizontalScope(EditorStyles.toolbar)) {
                    UnityEngine.GUILayout.Label(model.EditingFieldLabel, EditorStyles.miniLabel, UnityEngine.GUILayout.Width(80));
                    double cur = model.ScrubValue(model.EditingField);
                    float nv = EditorGUILayout.DelayedFloatField((float)cur, UnityEngine.GUILayout.Width(90));
                    if ((double)nv != cur) model.ApplyScrubValue(model.EditingField, nv);
                    if (UnityEngine.GUILayout.Button("Done", EditorStyles.toolbarButton, UnityEngine.GUILayout.Width(48)))
                        model.EditingField = null;
                    UnityEngine.GUILayout.FlexibleSpace();
                }
                return;
            }

            var n = model.Selected;
            using (new UnityEngine.GUILayout.HorizontalScope(EditorStyles.toolbar)) {
                if (n == null) {
                    UnityEngine.GUILayout.Label("Select a layer to edit its name / text", EditorStyles.miniLabel);
                    UnityEngine.GUILayout.FlexibleSpace();
                    return;
                }
                // Delayed fields commit only on Enter / focus-loss, so typing doesn't rebuild the
                // panel on every keystroke (which was dropping focus mid-edit). Named controls let
                // OnEditText / double-click force focus here reliably.
                UnityEngine.GUILayout.Label("Name", EditorStyles.miniLabel, UnityEngine.GUILayout.Width(36));
                UnityEngine.GUI.SetNextControlName("wd-name");
                string curName = n.Name ?? "";
                string newName = EditorGUILayout.DelayedTextField(curName, EditorStyles.toolbarTextField, UnityEngine.GUILayout.Width(160));
                if (newName != curName) editor.SetName(n, string.IsNullOrEmpty(newName) ? null : newName);

                if (n.IsText) {
                    UnityEngine.GUILayout.Label("Text", EditorStyles.miniLabel, UnityEngine.GUILayout.Width(30));
                    UnityEngine.GUI.SetNextControlName("wd-text");
                    string curText = n.Text ?? "";
                    string newText = EditorGUILayout.DelayedTextField(curText, EditorStyles.toolbarTextField, UnityEngine.GUILayout.MinWidth(220));
                    if (newText != curText) editor.SetText(n, newText);
                }
                UnityEngine.GUILayout.FlexibleSpace();
            }

            // Honor a focus request from the "Edit text" button / double-clicking a text node.
            if (model.ConsumeFocusText()) EditorGUI.FocusTextInControl("wd-text");
        }

        // Global keyboard shortcuts. Runs at the top of OnPanelChrome (an IMGUI pass) so we can
        // consume the key via Event.Use() before HandleInput would dispatch it into the document.
        // Skipped while an IMGUI text field (name / text / token rename) is being edited.
        void HandleShortcuts() {
            if (editor == null) return;
            var e = UnityEngine.Event.current;
            if (e == null || e.type != UnityEngine.EventType.KeyDown) return;
            // Never steal keys while a text field is focused/editing — otherwise typing in the
            // Name/Text bar (or any IMGUI field) would trigger delete/undo/etc.
            if (EditorGUIUtility.editingTextField) return;
            if (!string.IsNullOrEmpty(UnityEngine.GUI.GetNameOfFocusedControl())) return;
            bool ctrl = e.control || e.command;
            var key = e.keyCode;
            if (ctrl && key == UnityEngine.KeyCode.Z) { if (e.shift) model.OnRedo(null); else model.OnUndo(null); e.Use(); }
            else if (ctrl && key == UnityEngine.KeyCode.Y) { model.OnRedo(null); e.Use(); }
            else if (ctrl && key == UnityEngine.KeyCode.D) { model.OnDuplicate(null); e.Use(); }
            else if (key == UnityEngine.KeyCode.Delete) { model.OnDelete(null); e.Use(); }   // not Backspace (a text key)
            else if (key == UnityEngine.KeyCode.Escape) { model.OnEscape(); e.Use(); }
            else if (key == UnityEngine.KeyCode.UpArrow || key == UnityEngine.KeyCode.DownArrow
                     || key == UnityEngine.KeyCode.LeftArrow || key == UnityEngine.KeyCode.RightArrow) {
                model.NudgeSelected(key, e.shift); e.Use();
            }
        }

        protected override string Html {
            get {
                var ed = Editor;
                var doc = ed.Document;
                model.IdMap.Clear();
                selectedNodeIndex = -1;
                // Build the nN ↔ node map up front (pre-order, matching the compiler's wN order)
                // so the canvas hooks work even when the left panel is showing Tokens, not Layers.
                int idCounter = 0;
                if (doc.Root != null) BuildIdMap(doc.Root, ref idCounter);

                var compiled = SafeCompile(doc);

                var sb = new StringBuilder(8192);
                sb.Append("<style>").Append(ChromeCss).Append("</style>");
                // The compiled design's own scoped CSS (token :root vars + per-node classes).
                sb.Append("<style>").Append(compiled.Css).Append("</style>");

                sb.Append("<div class='wd-root' on-pointermove='OnRootMove' on-pointerup='OnRootUp'>");
                AppendToolbar(sb, ed);
                sb.Append("<div class='wd-body'>");
                // Layers first so IdMap + selectedNodeIndex are populated before the canvas
                // injects per-node click hooks (the canvas reuses the same nN id space).
                AppendLayers(sb, doc);
                sb.Append("<div class='wd-divider' data-div='left' on-pointerdown='OnDividerDown'></div>");
                // Live measurement badge for a Fixed-sized selection (updates during resize).
                var selNode = model.Selected;
                string sizeBadge = (selNode != null && selNode.WidthMode == SizeMode.Fixed && selNode.HeightMode == SizeMode.Fixed
                                    && selNode.Width > 0 && selNode.Height > 0)
                    ? ((int)selNode.Width) + " × " + ((int)selNode.Height)
                    : null;
                AppendCanvas(sb, doc.Root, InjectCanvasHooks(compiled.Html, model.IdMap.Count, selectedNodeIndex, sizeBadge));
                sb.Append("<div class='wd-divider' data-div='right' on-pointerdown='OnDividerDown'></div>");
                AppendInspector(sb);
                sb.Append("</div>");          // close wd-body
                AppendMenus(sb, ed);          // floating New dropdown / right-click context menu
                sb.Append("</div>");          // close wd-root
                return sb.ToString();
            }
        }

        static DesignCompileResult SafeCompile(DesignDocument doc) {
            try { return new DesignCompiler().Compile(doc); }
            catch (System.Exception ex) {
                return new DesignCompileResult {
                    Css = "",
                    Html = "<div style='color:#ff6b6b;font-size:13px;padding:16px'>Compile error: "
                           + Esc(ex.Message) + "</div>",
                };
            }
        }

        // ── Toolbar ──────────────────────────────────────────────────────────────────────────
        void AppendToolbar(StringBuilder sb, DocumentEditor ed) {
            bool hasSel = model.Selected != null;
            bool notRoot = hasSel && !ReferenceEquals(model.Selected, ed.Document.Root);
            sb.Append("<div class='wd-toolbar'>");
            sb.Append("<span class='wd-brand'>Weva Designer</span>");
            sb.Append("<span class='wd-dirty'>").Append(ed.IsDirty ? "●" : "").Append("</span>");
            sb.Append("<span class='wd-tgap'></span>");
            // New ▾ — opens a dropdown of starter templates (see AppendMenus).
            sb.Append("<span class='wd-btn").Append(model.NewMenuOpen ? " wd-chip-on" : "")
              .Append("' on-click='OnToggleNewMenu'>New<span class='wd-caret'>▾</span></span>");
            AppendToolBtn(sb, "OnOpen", "Open", true);
            AppendToolBtn(sb, "OnSave", "Save", true);
            AppendToolBtn(sb, "OnSaveAs", "Save As", true);
            sb.Append("<span class='wd-tgap'></span>");
            AppendToolBtn(sb, "OnAddFrame", "+ Frame", hasSel);
            AppendToolBtn(sb, "OnAddText", "+ Text", hasSel);
            AppendToolBtn(sb, "OnDuplicate", "Duplicate", notRoot);
            AppendToolBtn(sb, "OnDelete", "Delete", notRoot);
            sb.Append("<span class='wd-spacer'></span>");
            AppendToolBtn(sb, "OnUndo", "Undo", ed.CanUndo);
            AppendToolBtn(sb, "OnRedo", "Redo", ed.CanRedo);
            sb.Append("</div>");
        }

        static void AppendToolBtn(StringBuilder sb, string method, string label, bool enabled, string dataVal = null) {
            sb.Append("<span class='wd-btn")
              .Append(enabled ? "" : " wd-btn-off").Append("'");
            if (enabled) sb.Append(" on-click='").Append(method).Append("'");
            if (dataVal != null) sb.Append(" data-val='").Append(Esc(dataVal)).Append("'");
            sb.Append(">").Append(label).Append("</span>");
        }

        // ── Floating menus (New dropdown + right-click context menu) ───────────────────────────
        // Rendered last, above everything, over a full-panel scrim that closes them on an
        // outside press. The New dropdown is anchored under its toolbar button; the context
        // menu opens at the right-click position. Both are absolute in the panel's coordinate
        // space (same space as PointerEvent X/Y), so positions line up with where the user clicked.
        void AppendMenus(StringBuilder sb, DocumentEditor ed) {
            if (!model.NewMenuOpen && !model.CtxMenuOpen) return;
            sb.Append("<div class='wd-scrim' on-pointerdown='OnCloseMenus'></div>");

            if (model.NewMenuOpen) {
                sb.Append("<div class='wd-menu' style='left:").Append((int)model.NewMenuX)
                  .Append("px;top:").Append((int)model.NewMenuY).Append("px'>");
                sb.Append("<div class='wd-menu-hdr'>New from template</div>");
                AppendMenuItem(sb, "OnLoadTemplate", "Blank", true, "Blank");
                AppendMenuItem(sb, "OnLoadTemplate", "Main Menu", true, "Main Menu");
                AppendMenuItem(sb, "OnLoadTemplate", "Combat HUD", true, "Combat HUD");
                AppendMenuItem(sb, "OnLoadTemplate", "Settings", true, "Settings");
                sb.Append("</div>");
            }

            if (model.CtxMenuOpen) {
                bool hasSel = model.Selected != null;
                bool notRoot = hasSel && !ReferenceEquals(model.Selected, ed.Document.Root);
                sb.Append("<div class='wd-menu' style='left:").Append((int)model.CtxX)
                  .Append("px;top:").Append((int)model.CtxY).Append("px'>");
                AppendMenuItem(sb, "OnAddFrame", "Add Frame", hasSel);
                AppendMenuItem(sb, "OnAddText", "Add Text", hasSel);
                sb.Append("<div class='wd-menu-sep'></div>");
                AppendMenuItem(sb, "OnDuplicate", "Duplicate", notRoot);
                AppendMenuItem(sb, "OnDelete", "Delete", notRoot);
                sb.Append("</div>");
            }
        }

        static void AppendMenuItem(StringBuilder sb, string method, string label, bool enabled, string dataVal = null) {
            sb.Append("<span class='wd-menu-item").Append(enabled ? "" : " wd-menu-item-off").Append("'");
            if (enabled) sb.Append(" on-click='").Append(method).Append("'");
            if (dataVal != null) sb.Append(" data-val='").Append(Esc(dataVal)).Append("'");
            sb.Append(">").Append(Esc(label)).Append("</span>");
        }

        // Build the nN → node map (and record the selected node's index) by a pre-order walk that
        // matches the compiler's wN ordering. Always run, independent of which left tab is shown.
        void BuildIdMap(DesignNode n, ref int counter) {
            int idx = counter++;
            model.IdMap["n" + idx] = n;
            if (ReferenceEquals(n, model.Selected)) selectedNodeIndex = idx;
            for (int i = 0; i < n.Children.Count; i++)
                BuildIdMap(n.Children[i], ref counter);
        }

        // ── Left panel: Layers / Tokens tabs ─────────────────────────────────────────────────
        // Tokens used to hang off the foot of the layer tree (cluttered, out of place). They now
        // live behind their own tab so the left panel shows one clear thing at a time.
        void AppendLayers(StringBuilder sb, DesignDocument doc) {
            sb.Append("<div class='wd-panel wd-left' style='flex:0 0 ").Append(model.LeftPanelW)
              .Append("px;width:").Append(model.LeftPanelW).Append("px'>");
            string tab = model.LeftTab;
            sb.Append("<div class='wd-tabs'>");
            AppendLeftTab(sb, "layers", "Layers", tab);
            AppendLeftTab(sb, "tokens", "Tokens", tab);
            AppendLeftTab(sb, "library", "Library", tab);
            sb.Append("</div>");
            if (tab == "tokens") {
                AppendTokens(sb, doc.Tokens);
            } else if (tab == "library") {
                AppendLibrary(sb, doc);
            } else {
                sb.Append("<div class='wd-layers'>");
                int counter = 0;
                if (doc.Root != null) AppendLayerRow(sb, doc.Root, 0, ref counter);
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        static void AppendLeftTab(StringBuilder sb, string id, string label, string current) {
            sb.Append("<span class='wd-tab").Append(current == id ? " wd-tab-on" : "")
              .Append("' on-click='OnSetLeftTab' data-val='").Append(id).Append("'>").Append(label).Append("</span>");
        }

        // ── Library (component kit) ──────────────────────────────────────────────────────────
        // Lists the document's component definitions; clicking one drops an instance into the
        // selected container. Empty docs get a one-click "Install component kit" (Button/Card/…).
        void AppendLibrary(StringBuilder sb, DesignDocument doc) {
            sb.Append("<div class='wd-lib'>");
            if (doc.Components.Count == 0) {
                sb.Append("<div class='wd-empty'>No components in this document yet.</div>");
                sb.Append("<div class='wd-libbtn' on-click='OnInstallKit'>Install component kit</div>");
            } else {
                sb.Append("<div class='wd-libhint'>Click to add an instance to the selection.</div>");
                foreach (var kv in doc.Components) {
                    sb.Append("<div class='wd-libitem' on-click='OnAddInstance' data-comp='").Append(Esc(kv.Key)).Append("'>")
                      .Append("<span class='wd-libitem-ico'>◇</span><span class='wd-libitem-name'>").Append(Esc(kv.Key)).Append("</span></div>");
                }
            }
            sb.Append("</div>");
        }

        // ── Tokens (design-system colors) ──────────────────────────────────────────────────────
        // Lists the document's color tokens at the foot of the layers panel. Clicking one opens
        // it for editing in the inspector (the color picker recolors the token); "+ Add" creates a
        // new token and opens it. Token edits flow through DocumentEditor (undoable).
        void AppendTokens(StringBuilder sb, DesignTokens tokens) {
            sb.Append("<div class='wd-panel-title'>Tokens</div>");
            sb.Append("<div class='wd-tokens'>");
            foreach (var kv in tokens.Colors) {
                bool on = kv.Key == model.EditingToken;
                sb.Append("<div class='wd-token").Append(on ? " wd-token-on" : "")
                  .Append("' on-click='OnEditToken' data-tok='").Append(Esc(kv.Key)).Append("'>")
                  .Append("<span class='wd-token-sw' style='background:").Append(Esc(kv.Value)).Append("'></span>")
                  .Append("<span class='wd-token-name'>").Append(Esc(kv.Key)).Append("</span></div>");
            }
            sb.Append("<div class='wd-token wd-token-add' on-click='OnAddToken'>+ Add color token</div>");
            sb.Append("</div>");
        }

        void AppendLayerRow(StringBuilder sb, DesignNode n, int depth, ref int counter) {
            int idx = counter++;
            string nid = "n" + idx;
            bool sel = ReferenceEquals(n, model.Selected);

            // Drag-and-drop affordances: dim the row being dragged, and draw an insertion
            // line / inside-highlight on the current drop target (see Model drag handlers).
            string extra = sel ? " wd-layer-sel" : "";
            if (model.DragActive && nid == model.DragLayerNid) extra += " wd-layer-dragging";
            if (model.DragActive && nid == model.DropNid)
                extra += model.DropInside ? " wd-drop-inside" : (model.DropAfter ? " wd-drop-after" : " wd-drop-before");

            sb.Append("<div class='wd-layer").Append(extra)
              .Append("' on-click='OnLayer' on-pointerdown='OnLayerDown' data-nid='").Append(nid).Append("'")
              .Append(" style='padding-left:").Append(8 + depth * 14).Append("px'>");
            sb.Append("<span class='wd-layer-ico'>").Append(NodeGlyph(n)).Append("</span>");
            sb.Append("<span class='wd-layer-name'>").Append(Esc(NodeLabel(n))).Append("</span>");
            sb.Append("</div>");

            for (int i = 0; i < n.Children.Count; i++)
                AppendLayerRow(sb, n.Children[i], depth + 1, ref counter);
        }

        // ── Canvas (center) ──────────────────────────────────────────────────────────────────
        // The canvas-surface is sized to the design's artboard (the root's fixed dimensions, or
        // a sensible default for Fill/Hug roots) so the design lays out at a known size and
        // Fill children resolve correctly. A hugging surface in a flex-centered parent otherwise
        // constrained the design (clipped buttons). The canvas scrolls if the artboard overflows.
        static void AppendCanvas(StringBuilder sb, DesignNode root, string designHtml) {
            int aw = (root != null && root.WidthMode == SizeMode.Fixed && root.Width > 0) ? (int)root.Width : 960;
            int ah = (root != null && root.HeightMode == SizeMode.Fixed && root.Height > 0) ? (int)root.Height : 600;
            sb.Append("<div class='wd-canvas' on-pointerdown='OnCanvasDown'>");
            sb.Append("<div class='wd-canvas-surface' style='width:").Append(aw)
              .Append("px;height:").Append(ah).Append("px'>").Append(designHtml).Append("</div>");
            sb.Append("</div>");
        }

        // Make the live canvas directly selectable. DesignCompiler emits one class `wN` per node
        // in pre-order — the same order as the layer tree's `nN` ids — so for each node we splice
        // `data-nid="nN" on-click="OnLayer"` onto its element (reusing the layer-click handler;
        // clicking the canvas selects the clicked element), plus a `wd-canvas-sel` outline class
        // on the selected node. The `class="wN"` match includes the closing quote, so `w1` never
        // collides with `w10`.
        static string InjectCanvasHooks(string html, int count, int selectedIndex, string sizeBadge) {
            if (string.IsNullOrEmpty(html)) return html;
            for (int i = 0; i < count; i++) {
                string find = "class=\"w" + i + "\"";
                if (html.IndexOf(find, System.StringComparison.Ordinal) < 0) continue;
                string outline = (i == selectedIndex) ? " wd-canvas-sel" : "";
                string repl = "data-nid=\"n" + i + "\" on-click=\"OnLayer\" class=\"w" + i + outline + "\"";
                html = html.Replace(find, repl);
            }
            if (selectedIndex >= 0) html = InjectResizeHandles(html, selectedIndex, sizeBadge);
            return html;
        }

        // 8 resize handles (corners + edge midpoints), each a pointer-down target carrying its
        // direction in data-h. Injected as the selected node's first children (CSS positions them
        // at its box edges via the node's position:relative/absolute containing block).
        const string ResizeHandlesHtml =
            "<div class='wd-rh wd-rh-nw' on-pointerdown='OnResizeDown' data-h='nw'></div>" +
            "<div class='wd-rh wd-rh-n'  on-pointerdown='OnResizeDown' data-h='n'></div>" +
            "<div class='wd-rh wd-rh-ne' on-pointerdown='OnResizeDown' data-h='ne'></div>" +
            "<div class='wd-rh wd-rh-e'  on-pointerdown='OnResizeDown' data-h='e'></div>" +
            "<div class='wd-rh wd-rh-se' on-pointerdown='OnResizeDown' data-h='se'></div>" +
            "<div class='wd-rh wd-rh-s'  on-pointerdown='OnResizeDown' data-h='s'></div>" +
            "<div class='wd-rh wd-rh-sw' on-pointerdown='OnResizeDown' data-h='sw'></div>" +
            "<div class='wd-rh wd-rh-w'  on-pointerdown='OnResizeDown' data-h='w'></div>";

        // Insert the handles right after the selected element's opening tag (its class now reads
        // `w{idx} wd-canvas-sel`, so we find that, then the next '>' that closes the tag).
        static string InjectResizeHandles(string html, int selectedIndex, string sizeBadge) {
            string marker = "class=\"w" + selectedIndex + " wd-canvas-sel\"";
            int at = html.IndexOf(marker, System.StringComparison.Ordinal);
            if (at < 0) return html;
            int gt = html.IndexOf('>', at + marker.Length);
            if (gt < 0) return html;
            string inject = ResizeHandlesHtml;
            if (!string.IsNullOrEmpty(sizeBadge))
                inject += "<div class='wd-size-badge'>" + Esc(sizeBadge) + "</div>";
            return html.Insert(gt + 1, inject);
        }

        // ── Inspector (right) — EDITABLE (M3) ──────────────────────────────────────────────────
        void AppendInspector(StringBuilder sb) {
            sb.Append("<div class='wd-panel wd-right' style='flex:0 0 ").Append(model.RightPanelW)
              .Append("px;width:").Append(model.RightPanelW).Append("px'>");
            sb.Append("<div class='wd-panel-title'>Inspector</div>");

            // Token-edit mode takes over the inspector: the color picker recolors the token.
            if (model.EditingToken != null) {
                sb.Append("<div class='wd-insp'>");
                Field(sb, "Token", model.EditingToken);
                ColorPicker(sb);   // edits the token's colour (see Model.ApplyPick / SyncPickFromFill)
                sb.Append("<div class='wd-field'><span class='wd-flabel'></span><span class='wd-chips'>")
                  .Append("<span class='wd-chip' on-click='OnDeleteToken'>Delete</span>")
                  .Append("<span class='wd-chip' on-click='OnCloseToken'>Done</span></span></div>");
                sb.Append("</div></div>");
                return;
            }

            var n = model.Selected;
            if (n == null) {
                sb.Append("<div class='wd-empty'>Select a layer.</div></div>");
                return;
            }
            var tokens = Editor.Document.Tokens;
            bool isRoot = ReferenceEquals(n, Editor.Document.Root);
            sb.Append("<div class='wd-insp'>");
            Field(sb, "Name", string.IsNullOrEmpty(n.Name) ? "(unnamed)" : n.Name);
            Field(sb, "Kind", NodeKind(n));
            if (n.IsText) Field(sb, "Text", Trunc(n.Text, 26));
            if (!isRoot) {
                sb.Append("<div class='wd-field'><span class='wd-flabel'>Order</span><span class='wd-chips'>")
                  .Append("<span class='wd-chip' on-click='OnMoveUp'>↑ Up</span>")
                  .Append("<span class='wd-chip' on-click='OnMoveDown'>↓ Down</span></span></div>");
            }

            // State row: edit the base look or its hover / pressed overrides. A • marks a
            // state that already carries overrides.
            StateChips(sb, n);

            if (model.EditState != null) {
                // State-editing mode: only the props a state can override (fill / text / opacity).
                // Structural props (layout, sizing, padding, font) are not per-state.
                sb.Append("<div class='wd-statehint'>Overrides applied on ")
                  .Append(Esc(model.EditState.Value.ToString().ToLowerInvariant()))
                  .Append("; inherits the base style otherwise.</div>");
                AppendStyleControls(sb, n, tokens);
                var st = n.GetState(model.EditState.Value);
                if (st != null && !st.IsEmpty)
                    sb.Append("<div class='wd-field'><span class='wd-flabel'></span><span class='wd-chips'>")
                      .Append("<span class='wd-chip' on-click='OnClearState'>Clear ")
                      .Append(Esc(model.EditState.Value.ToString().ToLowerInvariant())).Append("</span></span></div>");
            } else {
                // Grouped, collapsible sections — ordered most-used first; heavier / rarer ones
                // (Corners, Stroke, Effects) default closed to keep the panel short.
                if (n.IsText && Section(sb, "text", "Text", true)) {
                    // Content + a button that focuses the IMGUI text field (the editable field
                    // lives in the chrome bar; double-clicking the text on the canvas does the same).
                    Field(sb, "Content", string.IsNullOrEmpty(n.Text) ? "(empty)" : Trunc(n.Text, 22));
                    sb.Append("<div class='wd-field'><span class='wd-flabel'></span><span class='wd-chips'>")
                      .Append("<span class='wd-chip' on-click='OnEditText'>✎ Edit text</span></span></div>");
                    Scrub(sb, "Font", "OnStepFont", "font", (int)(n.FontSize.Px > 0 ? n.FontSize.Px : 16), n.FontSize.HasToken ? "" : "px", 1, 1);
                    Chips(sb, "Weight", "OnSetFontWeight", n.FontWeight.ToString(), "Normal", "Medium", "SemiBold", "Bold");
                    Chips(sb, "Align", "OnSetTextAlign", n.TextAlign.ToString(), "Start", "Center", "End");
                    Chips(sb, "Case", "OnSetTransform", n.TextTransform.ToString(), "None", "Uppercase", "Capitalize");
                    Chips(sb, "Decoration", "OnSetDecoration", n.TextDecoration.ToString(), "None", "Underline", "LineThrough");
                    ToggleChip(sb, "Italic", "OnToggleItalic", n.Italic);
                    Scrub(sb, "Spacing", "OnStepLetterSpacing", "ls", (int)n.LetterSpacing, "px", 1, -20);
                    TextShadowPresets(sb, n);
                }
                if (!n.IsText && Section(sb, "layout", "Layout", true)) {
                    Chips(sb, "Layout", "OnSetLayout", n.Layout.ToString(),
                        "None", "Row", "Column", "Grid");
                    if (n.Layout != LayoutMode.None) {
                        Scrub(sb, "Gap", "OnStepGap", "gap", (int)n.Gap.Px, n.Gap.HasToken ? "" : "px", 1, 0);
                        if (n.Layout == LayoutMode.Grid)
                            Scrub(sb, "Columns", "OnStepCols", "cols", n.GridColumns >= 1 ? n.GridColumns : 1, "", 1, 1);
                        // Alignment pad + Main/Cross chips + Wrap apply to flex (Row/Column) only —
                        // Grid arranges via columns + gap, so justify/align/wrap don't apply there.
                        if (n.Layout == LayoutMode.Row || n.Layout == LayoutMode.Column) {
                            AlignPad(sb, n);
                            Chips(sb, "Main", "OnSetMain", n.MainAlign.ToString(), "Start", "Center", "End", "SpaceBetween");
                            Chips(sb, "Cross", "OnSetCross", n.CrossAlign.ToString(), "Start", "Center", "End", "Stretch");
                            ToggleChip(sb, "Wrap", "OnToggleWrap", n.Wrap);
                        }
                    }
                    PaddingControl(sb, n);
                }

                if (Section(sb, "size", "Size", true)) {
                    Chips(sb, "Width", "OnSetWidth", n.WidthMode.ToString(), "Hug", "Fill", "Fixed");
                    Chips(sb, "Height", "OnSetHeight", n.HeightMode.ToString(), "Hug", "Fill", "Fixed");
                    if (n.WidthMode == SizeMode.Fixed) Scrub(sb, "W", "OnStepW", "w", (int)n.Width, "px", 1, 0);
                    if (n.HeightMode == SizeMode.Fixed) Scrub(sb, "H", "OnStepH", "h", (int)n.Height, "px", 1, 0);
                }

                if (Section(sb, "constraints", "Constraints", false)) {
                    // Min/max bounds (0 = unset). Use the generic ± stepper (OnScrubStep).
                    Scrub(sb, "Min W", "minw", (int)n.MinWidth, "px", 1, 0);
                    Scrub(sb, "Max W", "maxw", (int)n.MaxWidth, "px", 1, 0);
                    Scrub(sb, "Min H", "minh", (int)n.MinHeight, "px", 1, 0);
                    Scrub(sb, "Max H", "maxh", (int)n.MaxHeight, "px", 1, 0);
                }

                if (!isRoot && Section(sb, "position", "Position", false)) {
                    Chips(sb, "Mode", "OnSetPosition", n.Position.ToString(), "InFlow", "Absolute");
                    if (n.Position == Position.Absolute) {
                        // Edge offsets from the positioned parent (px). Scrubbing pins that edge.
                        Scrub(sb, "Top", "offtop", (int)(n.OffTop ?? default(Dim)).Px, "px", 1, 0);
                        Scrub(sb, "Right", "offright", (int)(n.OffRight ?? default(Dim)).Px, "px", 1, 0);
                        Scrub(sb, "Bottom", "offbottom", (int)(n.OffBottom ?? default(Dim)).Px, "px", 1, 0);
                        Scrub(sb, "Left", "offleft", (int)(n.OffLeft ?? default(Dim)).Px, "px", 1, 0);
                    }
                }

                if (Section(sb, "fill", "Fill", true)) {
                    AppendStyleControls(sb, n, tokens);   // fill (solid/gradient) + text colour + opacity
                }

                if (!n.IsText && Section(sb, "corners", "Corners", false)) {
                    RadiusControl(sb, n);
                }

                if (Section(sb, "stroke", "Stroke", false)) {
                    StrokeControls(sb, n, tokens);
                }

                if (Section(sb, "effects", "Effects", false)) {
                    ShadowPresets(sb, n);
                    // Transform — paint-time rotate / scale (no layout effect). Scale shown as %.
                    Scrub(sb, "Rotate", "OnStepRotation", "rot", (int)n.Rotation, "°", 1, -180, 180);
                    Scrub(sb, "Scale", "OnStepScale", "scale", (int)System.Math.Round(n.Scale * 100), "%", 5, 10, 400);
                    // Interactivity: clickable cursor + transition duration for smooth state changes.
                    ToggleChip(sb, "Pointer", "OnToggleCursor", n.Cursor == Cursor.Pointer);
                    Scrub(sb, "Transition", "trans", (int)n.TransitionMs, "ms", 50, 0, 2000);
                }

                if (Section(sb, "bind", "Data", false)) {
                    // Bind text/events/list to a controller — values typed via the IMGUI bar.
                    BindRow(sb, "Text", "text", n.Binding?.Text);
                    BindRow(sb, "On click", "click", EventVal(n, "click"));
                    BindRow(sb, "Repeat", "each", n.Binding?.RepeatEach);
                    if (n.HasBinding)
                        sb.Append("<div class='wd-field'><span class='wd-flabel'></span><span class='wd-chips'>")
                          .Append("<span class='wd-chip' on-click='OnClearBinding'>Clear binding</span></span></div>");
                }
            }
            sb.Append("</div>");
            sb.Append("</div>");
        }

        // Fill (swatches + picker), text colour (text nodes) and opacity — the properties an
        // interactive state can override. Current values come from model.Active* so the controls
        // reflect the base style or the state override being edited.
        void AppendStyleControls(StringBuilder sb, DesignNode n, DesignTokens tokens) {
            bool gradAllowed = model.EditState == null;   // gradients on the base fill only (v1)
            if (gradAllowed) {
                bool grad = model.GradMode;
                // Fill type: Solid | Gradient
                sb.Append("<div class='wd-field'><span class='wd-flabel'>Fill</span><span class='wd-chips'>")
                  .Append("<span class='wd-chip").Append(!grad ? " wd-chip-on" : "").Append("' on-click='OnSetFillType' data-val='Solid'>Solid</span>")
                  .Append("<span class='wd-chip").Append(grad ? " wd-chip-on" : "").Append("' on-click='OnSetFillType' data-val='Gradient'>Gradient</span>")
                  .Append("</span></div>");
                if (grad) {
                    // Which stop the picker edits + the angle.
                    sb.Append("<div class='wd-field'><span class='wd-flabel'>Stop</span><span class='wd-chips'>")
                      .Append("<span class='wd-chip").Append(model.GradStop != "end" ? " wd-chip-on" : "").Append("' on-click='OnSetGradStop' data-val='start'>Start</span>")
                      .Append("<span class='wd-chip").Append(model.GradStop == "end" ? " wd-chip-on" : "").Append("' on-click='OnSetGradStop' data-val='end'>End</span>")
                      .Append("</span></div>");
                    Stepper(sb, "Angle", "OnStepGradAngle", model.GradAngle, "°");
                    ColorPicker(sb);   // edits the selected stop
                    if (!string.IsNullOrEmpty(n.Fill))
                        sb.Append("<div class='wd-gradprev' style='background:").Append(n.Fill).Append("'></div>");
                } else {
                    Swatches(sb, "", "OnSetFill", tokens, model.ActiveFill());
                    ColorPicker(sb);
                }
            } else {
                Swatches(sb, "Fill", "OnSetFill", tokens, model.ActiveFill());
                ColorPicker(sb);
            }
            if (n.IsText) Swatches(sb, "Text color", "OnSetTextColor", tokens, model.ActiveTextColor());
            Scrub(sb, "Opacity", "OnStepOpacity", "opacity", (int)System.Math.Round(model.ActiveOpacity() * 100), "%", 1, 0, 100);
        }

        // Stroke (border) colour + width — its own section in the base inspector. (Per-state
        // strokes / focus rings are a later pass, hence base-style only.)
        void StrokeControls(StringBuilder sb, DesignNode n, DesignTokens tokens) {
            Swatches(sb, "Stroke", "OnSetStroke", tokens, n.Stroke);
            if (!string.IsNullOrEmpty(n.Stroke))
                Scrub(sb, "Border", "OnStepStrokeW", "strokew", (int)(n.StrokeWidth > 0 ? n.StrokeWidth : 1), "px", 1, 0);
        }

        // The Normal | Hover | Pressed selector. Normal = the base style (model.EditState null).
        void StateChips(StringBuilder sb, DesignNode n) {
            sb.Append("<div class='wd-field'><span class='wd-flabel'>State</span><span class='wd-chips'>");
            AppendStateChip(sb, n, null, "Normal");
            AppendStateChip(sb, n, InteractionState.Hover, "Hover");
            AppendStateChip(sb, n, InteractionState.Pressed, "Pressed");
            sb.Append("</span></div>");
        }

        void AppendStateChip(StringBuilder sb, DesignNode n, InteractionState? s, string label) {
            bool on = model.EditState == s;
            var st = s != null ? n.GetState(s.Value) : null;
            bool hasOverride = st != null && !st.IsEmpty;
            sb.Append("<span class='wd-chip").Append(on ? " wd-chip-on" : "")
              .Append("' on-click='OnSetState' data-val='").Append(s == null ? "Normal" : label).Append("'>")
              .Append(label).Append(hasOverride ? " •" : "").Append("</span>");
        }

        // A collapsible section header. Returns whether the body should render (open). The header
        // carries the current open state so OnToggleSection can flip it without knowing defaults.
        bool Section(StringBuilder sb, string key, string title, bool defaultOpen) {
            bool open = model.IsSectionOpen(key, defaultOpen);
            sb.Append("<div class='wd-sec-hdr' on-click='OnToggleSection' data-sec='").Append(key)
              .Append("' data-open='").Append(open ? "1" : "0").Append("'>")
              .Append("<span class='wd-sec-chev'>").Append(open ? "▾" : "▸").Append("</span>")
              .Append("<span class='wd-sec-title'>").Append(Esc(title)).Append("</span></div>");
            return open;
        }

        // A data-binding row: label + the current bound value + an Edit chip that opens the
        // IMGUI text editor (OnEditBind) for typing the path / method / list expression.
        static void BindRow(StringBuilder sb, string label, string key, string current) {
            sb.Append("<div class='wd-field'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-chips'><span class='wd-bindval'>")
              .Append(Esc(string.IsNullOrEmpty(current) ? "(none)" : current))
              .Append("</span><span class='wd-chip' on-click='OnEditBind' data-bind='").Append(key).Append("'>Edit</span></span></div>");
        }

        static string EventVal(DesignNode n, string ev) =>
            n.Binding?.Events != null && n.Binding.Events.TryGetValue(ev, out string m) ? m : null;

        static void Field(StringBuilder sb, string label, string value) {
            sb.Append("<div class='wd-field'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-fval'>").Append(Esc(value)).Append("</span></div>");
        }

        // A labelled row of clickable enum chips; the active value is highlighted. Each chip
        // carries data-val and an on-click to the given controller method.
        static void Chips(StringBuilder sb, string label, string method, string current, params string[] options) {
            sb.Append("<div class='wd-field'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-chips'>");
            foreach (var opt in options) {
                bool on = string.Equals(opt, current, System.StringComparison.OrdinalIgnoreCase);
                sb.Append("<span class='wd-chip").Append(on ? " wd-chip-on" : "")
                  .Append("' on-click='").Append(method).Append("' data-val='").Append(opt).Append("'>")
                  .Append(opt).Append("</span>");
            }
            sb.Append("</span></div>");
        }

        // Corner radius: the quick presets, then a uniform drag-scrub or four per-corner scrubs
        // behind a link toggle (parallels PaddingControl). Linked/per-corner is editor UI state.
        void RadiusControl(StringBuilder sb, DesignNode n) {
            RadiusPresets(sb, n);
            bool linked = model.RadiusLinked;
            sb.Append("<div class='wd-field'><span class='wd-flabel'></span><span class='wd-chips'>")
              .Append("<span class='wd-chip").Append(linked ? " wd-chip-on" : "").Append("' on-click='OnToggleRadiusLink'>")
              .Append(linked ? "🔗 Linked" : "Per-corner").Append("</span></span></div>");
            if (linked) {
                Scrub(sb, "Radius", "OnStepRadius", "radius", (int)n.Radius.Px, n.Radius.HasToken ? "" : "px", 1, 0);
            } else {
                Scrub(sb, "Top L", "OnStepRadTL", "radtl", (int)(n.RadiusTopLeft ?? n.Radius).Px, "px", 1, 0);
                Scrub(sb, "Top R", "OnStepRadTR", "radtr", (int)(n.RadiusTopRight ?? n.Radius).Px, "px", 1, 0);
                Scrub(sb, "Bot R", "OnStepRadBR", "radbr", (int)(n.RadiusBottomRight ?? n.Radius).Px, "px", 1, 0);
                Scrub(sb, "Bot L", "OnStepRadBL", "radbl", (int)(n.RadiusBottomLeft ?? n.Radius).Px, "px", 1, 0);
            }
        }

        // The Figma alignment pad: a 3×3 grid of dots. Clicking a cell sets both the main- and
        // cross-axis alignment at once (direction-aware — horizontal maps to main for a Row but
        // to cross for a Column). The cell matching the current alignment is lit. SpaceBetween /
        // Stretch don't map to a single cell, so the Main/Cross chips below still cover those.
        static void AlignPad(StringBuilder sb, DesignNode n) {
            AlignIndices(n, out int hOn, out int vOn);
            sb.Append("<div class='wd-field'><span class='wd-flabel'>Align</span><div class='wd-align-pad'>");
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++) {
                    bool on = col == hOn && row == vOn;
                    sb.Append("<span class='wd-align-cell").Append(on ? " wd-align-on" : "")
                      .Append("' on-click='OnSetAlignPad' data-val='").Append(col).Append(',').Append(row)
                      .Append("'><span class='wd-align-dot'></span></span>");
                }
            sb.Append("</div></div>");
        }

        // Current alignment → (horizontal, vertical) cell indices (0/1/2), or -1 when the axis
        // is on a non-positional mode (SpaceBetween / Stretch). Mapping flips with layout dir.
        static void AlignIndices(DesignNode n, out int h, out int v) {
            int main = n.MainAlign == MainAlign.Start ? 0 : n.MainAlign == MainAlign.Center ? 1 : n.MainAlign == MainAlign.End ? 2 : -1;
            int cross = n.CrossAlign == CrossAlign.Start ? 0 : n.CrossAlign == CrossAlign.Center ? 1 : n.CrossAlign == CrossAlign.End ? 2 : -1;
            if (n.Layout == LayoutMode.Column) { v = main; h = cross; }
            else { h = main; v = cross; }   // Row (the default mapping)
        }

        // A single on/off toggle rendered as one chip (highlighted when on). For booleans like
        // Italic where a full chip row would be overkill.
        static void ToggleChip(StringBuilder sb, string label, string method, bool on) {
            sb.Append("<div class='wd-field'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-chips'><span class='wd-chip").Append(on ? " wd-chip-on" : "")
              .Append("' on-click='").Append(method).Append("'>").Append(on ? "On" : "Off")
              .Append("</span></span></div>");
        }

        // Token color palette: one swatch per color token + a "none" clear chip. Clicking sets
        // the property to {tokenName} (or empty). The current token is ringed.
        static void Swatches(StringBuilder sb, string label, string method, DesignTokens tokens, string current) {
            sb.Append("<div class='wd-field wd-field-col'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-swatches'>");
            sb.Append("<span class='wd-sw wd-sw-none").Append(string.IsNullOrEmpty(current) ? " wd-sw-on" : "")
              .Append("' on-click='").Append(method).Append("' data-val='' title='none'>∅</span>");
            foreach (var kv in tokens.Colors) {
                string tok = "{" + kv.Key + "}";
                bool on = string.Equals(tok, current, System.StringComparison.Ordinal);
                sb.Append("<span class='wd-sw").Append(on ? " wd-sw-on" : "")
                  .Append("' on-click='").Append(method).Append("' data-val='").Append(Esc(kv.Key))
                  .Append("' style='background:").Append(Esc(kv.Value)).Append("' title='").Append(Esc(kv.Key))
                  .Append("'></span>");
            }
            sb.Append("</span></div>");
        }

        // A real draggable color picker: a saturation/value square over the current hue, a hue
        // strip, and a live hex preview. Dragging a thumb (on-pointerdown/move) recomputes the
        // color from the pointer's fraction within the element and applies it to the fill.
        void ColorPicker(StringBuilder sb) {
            model.SyncPickFromFill();
            double h = model.PickH, s = model.PickS, v = model.PickV;
            string hex = Model.HsvToHex(h, s, v);
            sb.Append("<div class='wd-field wd-field-col'><span class='wd-flabel'>Color</span>");
            // Saturation/Value square: white→hue (right) over black→up, atop the pure hue.
            sb.Append("<div class='wd-sv' data-pick='sv' on-pointerdown='OnSVDown' on-pointermove='OnSVMove' style='")
              .Append("background:linear-gradient(to top,#000,rgba(0,0,0,0)),linear-gradient(to right,#fff,rgba(255,255,255,0)),hsl(")
              .Append(((int)h)).Append(",100%,50%)'>")
              .Append("<span class='wd-pthumb' style='left:").Append((int)(s * 100)).Append("%;top:")
              .Append((int)((1 - v) * 100)).Append("%'></span></div>");
            // Hue strip.
            sb.Append("<div class='wd-hue' data-pick='hue' on-pointerdown='OnHueDown' on-pointermove='OnHueMove'>")
              .Append("<span class='wd-pthumb wd-hthumb' style='left:").Append((int)(h / 360 * 100)).Append("%'></span></div>");
            sb.Append("<div class='wd-cprev'><span class='wd-cswatch' style='background:").Append(hex)
              .Append("'></span><span class='wd-chex'>").Append(hex).Append("</span></div>");
            sb.Append("</div>");
        }

        // One-click corner-radius presets. Highlights the preset matching the current radius;
        // "Round" uses a large radius the engine clamps to a full pill. The ± stepper below
        // stays for fine control.
        static void RadiusPresets(StringBuilder sb, DesignNode n) {
            int r = (int)n.Radius.Px;
            sb.Append("<div class='wd-field'><span class='wd-flabel'>Corners</span><span class='wd-chips'>");
            AppendRadiusChip(sb, "None", 0, r);
            AppendRadiusChip(sb, "S", 4, r);
            AppendRadiusChip(sb, "M", 8, r);
            AppendRadiusChip(sb, "L", 16, r);
            AppendRadiusChip(sb, "Round", 9999, r);
            sb.Append("</span></div>");
        }

        static void AppendRadiusChip(StringBuilder sb, string label, int px, int current) {
            bool on = current == px || (px == 9999 && current >= 9999);
            sb.Append("<span class='wd-chip").Append(on ? " wd-chip-on" : "")
              .Append("' on-click='OnSetRadiusPreset' data-val='").Append(px).Append("'>")
              .Append(label).Append("</span>");
        }

        // Box-shadow presets. data-val is a size key; OnSetShadow maps it to the CSS via
        // ShadowCss so the highlight and the applied value stay in sync.
        static void ShadowPresets(StringBuilder sb, DesignNode n) {
            string cur = n.Shadow;
            sb.Append("<div class='wd-field'><span class='wd-flabel'>Shadow</span><span class='wd-chips'>");
            AppendShadowChip(sb, "None", "none", cur);
            AppendShadowChip(sb, "S", "sm", cur);
            AppendShadowChip(sb, "M", "md", cur);
            AppendShadowChip(sb, "L", "lg", cur);
            sb.Append("</span></div>");
        }

        static void AppendShadowChip(StringBuilder sb, string label, string key, string current) {
            string css = ShadowCss(key);
            bool on = string.Equals(current ?? "", css ?? "", System.StringComparison.Ordinal);
            sb.Append("<span class='wd-chip").Append(on ? " wd-chip-on" : "")
              .Append("' on-click='OnSetShadow' data-val='").Append(key).Append("'>")
              .Append(label).Append("</span>");
        }

        // Shared key→CSS map (used by the chip highlight and the OnSetShadow handler).
        static string ShadowCss(string key) {
            switch (key) {
                case "sm": return "0 1px 2px rgba(0,0,0,0.18)";
                case "md": return "0 4px 10px rgba(0,0,0,0.25)";
                case "lg": return "0 10px 30px rgba(0,0,0,0.35)";
                default:   return null; // none
            }
        }

        // Text-shadow presets (glyph drop-shadow for legibility over busy backgrounds) — same
        // shape as ShadowPresets, but the values are text-shadow strings via TextShadowCss.
        static void TextShadowPresets(StringBuilder sb, DesignNode n) {
            string cur = n.TextShadow;
            sb.Append("<div class='wd-field'><span class='wd-flabel'>Text shadow</span><span class='wd-chips'>");
            AppendTextShadowChip(sb, "None", "none", cur);
            AppendTextShadowChip(sb, "S", "sm", cur);
            AppendTextShadowChip(sb, "M", "md", cur);
            AppendTextShadowChip(sb, "L", "lg", cur);
            sb.Append("</span></div>");
        }

        static void AppendTextShadowChip(StringBuilder sb, string label, string key, string current) {
            string css = TextShadowCss(key);
            bool on = string.Equals(current ?? "", css ?? "", System.StringComparison.Ordinal);
            sb.Append("<span class='wd-chip").Append(on ? " wd-chip-on" : "")
              .Append("' on-click='OnSetTextShadow' data-val='").Append(key).Append("'>")
              .Append(label).Append("</span>");
        }

        static string TextShadowCss(string key) {
            switch (key) {
                case "sm": return "0 1px 1px rgba(0,0,0,0.5)";
                case "md": return "0 2px 3px rgba(0,0,0,0.55)";
                case "lg": return "0 3px 6px rgba(0,0,0,0.6)";
                default:   return null; // none
            }
        }

        // A label + value with − / + steppers calling the controller (data-val = -1 / +1).
        static void Stepper(StringBuilder sb, string label, string method, int value, string unit) {
            sb.Append("<div class='wd-field'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-step'>")
              .Append("<span class='wd-chip' on-click='").Append(method).Append("' data-val='-1'>−</span>")
              .Append("<span class='wd-step-val'>").Append(value).Append(Esc(unit)).Append("</span>")
              .Append("<span class='wd-chip' on-click='").Append(method).Append("' data-val='1'>+</span>")
              .Append("</span></div>");
        }

        // Like Stepper, but the value itself is drag-to-scrub (the Figma signature): press and
        // drag left/right to change the number, ±1 step per ~4px. The − / + chips still call the
        // same step method for one-click fine control; the value carries data-scrub/step/min/max
        // read by the Model's scrub handlers (OnScrubDown + the wd-root move/up tracking).
        void Scrub(StringBuilder sb, string label, string stepMethod, string scrubKey, int value, string unit,
                   double step, double min, double max = double.MaxValue) {
            model.RegisterScrubLabel(scrubKey, label);   // so the click-to-type editor can label itself
            bool active = model.ScrubKey == scrubKey;
            // Common scrub attrs, put on the value (for drag) AND both ± chips (so the generic
            // OnScrubStep can read the key/step/bounds; control-specific stepMethods ignore them).
            string attrs = " data-scrub='" + scrubKey + "' data-step='" + Num(step) + "' data-min='" + Num(min) + "'"
                         + (max != double.MaxValue ? " data-max='" + Num(max) + "'" : "");
            sb.Append("<div class='wd-field'><span class='wd-flabel'>").Append(Esc(label))
              .Append("</span><span class='wd-step'>")
              .Append("<span class='wd-chip' on-click='").Append(stepMethod).Append("' data-val='-1'").Append(attrs).Append(">−</span>")
              .Append("<span class='wd-scrub").Append(active ? " wd-scrubbing" : "")
              .Append("' on-pointerdown='OnScrubDown'").Append(attrs).Append(" title='Drag to change'>")
              .Append(value).Append(Esc(unit)).Append("</span>")
              .Append("<span class='wd-chip' on-click='").Append(stepMethod).Append("' data-val='1'").Append(attrs).Append(">+</span>")
              .Append("</span></div>");
        }

        // Convenience overload for new scrubs that don't need a bespoke ± handler — routes the
        // ± buttons through the generic OnScrubStep (reads data-scrub/step/min/max + data-val).
        void Scrub(StringBuilder sb, string label, string scrubKey, int value, string unit,
                   double step, double min, double max = double.MaxValue)
            => Scrub(sb, label, "OnScrubStep", scrubKey, value, unit, step, min, max);

        static string Num(double d) =>
            d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Figma-style padding: one linked value, or four per-side values behind a link toggle.
        // The toggle's linked/per-side state is editor UI state (model.PadLinked), not on the node.
        void PaddingControl(StringBuilder sb, DesignNode n) {
            bool linked = model.PadLinked;
            sb.Append("<div class='wd-field'><span class='wd-flabel'>Padding</span><span class='wd-chips'>")
              .Append("<span class='wd-chip").Append(linked ? " wd-chip-on" : "").Append("' on-click='OnTogglePadLink'>")
              .Append(linked ? "🔗 Linked" : "Per-side").Append("</span></span></div>");
            if (linked) {
                Scrub(sb, "All", "OnStepPad", "pad", (int)n.PadTop.Px, n.PadTop.HasToken ? "" : "px", 1, 0);
            } else {
                Scrub(sb, "Top", "OnStepPadT", "padt", (int)n.PadTop.Px, n.PadTop.HasToken ? "" : "px", 1, 0);
                Scrub(sb, "Right", "OnStepPadR", "padr", (int)n.PadRight.Px, n.PadRight.HasToken ? "" : "px", 1, 0);
                Scrub(sb, "Bottom", "OnStepPadB", "padb", (int)n.PadBottom.Px, n.PadBottom.HasToken ? "" : "px", 1, 0);
                Scrub(sb, "Left", "OnStepPadL", "padl", (int)n.PadLeft.Px, n.PadLeft.HasToken ? "" : "px", 1, 0);
            }
        }

        // ── Labels ───────────────────────────────────────────────────────────────────────────
        static string NodeLabel(DesignNode n) {
            if (!string.IsNullOrEmpty(n.Name)) return n.Name;
            if (n.IsText) return "\"" + Trunc(n.Text, 22) + "\"";
            if (n.IsInstance) return n.ComponentRef;
            return n.Layout == LayoutMode.Row ? "Row" : n.Layout == LayoutMode.Column ? "Column" : "Frame";
        }

        static string NodeKind(DesignNode n) {
            if (n.IsInstance) return "Instance · " + n.ComponentRef;
            if (n.IsText) return "Text";
            return "Frame";
        }

        static string NodeGlyph(DesignNode n) {
            if (n.IsText) return "T";
            if (n.IsInstance) return "◇";
            if (n.Layout == LayoutMode.Row) return "▭";
            if (n.Layout == LayoutMode.Column) return "▤";
            return "□";
        }

        static string SizeLabel(SizeMode mode, double px) =>
            mode == SizeMode.Fixed ? ((int)px) + "px" : mode.ToString();

        static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

        static string Esc(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("'", "&#39;").Replace("\"", "&quot;");
        }

        // ── Controller (events bind here) ──────────────────────────────────────────────────────
        sealed class Model {
            public DocumentEditor Ed;
            public DesignNode Selected;
            public System.Action Repaint;
            public System.Action<string> LoadTemplate;
            public System.Func<Weva.Dom.Element, Weva.Layout.Boxes.Box> BoxOf;
            public System.Action Save, SaveAs, Open;
            public System.Func<double> Now;   // editor time, for double-click detection
            public readonly Dictionary<string, DesignNode> IdMap = new Dictionary<string, DesignNode>();

            // Color-picker working state (HSV). Synced from the selected node's hex fill on
            // selection; dragging the picker recomputes the fill from it.
            public double PickH = 210, PickS = 0.7, PickV = 0.9;
            string dragging;     // "sv" | "hue" | null — which thumb the pointer grabbed
            string lastApplied;  // last hex the picker wrote; lets Sync keep our exact HSV

            // ── Layer drag-and-drop state (read by AppendLayerRow to draw affordances) ──
            public string DragLayerNid;  // data-nid of the row pressed (candidate drag source)
            public bool DragActive;      // press has moved past the click threshold → real drag
            public string DropNid;       // data-nid of the row the pointer is currently over
            public bool DropAfter;       // insert after DropNid (vs before)
            public bool DropInside;      // drop INTO DropNid (a container) as a child
            double dragStartY;
            const double DragThreshold = 4.0;

            // ── Interactive-state editing ───────────────────────────────────────────────────────
            // Which state the inspector's visual props target. null = the base style; otherwise
            // fill / text-color / opacity edits write that state's override (the look on :hover /
            // :active), via DocumentEditor's state mutations. Reading falls back to the base value
            // when the state has no override yet, so the picker starts from the inherited look.
            public InteractionState? EditState;

            // Token-edit mode: when non-null, the color picker recolors this design-system color
            // token instead of the selected node's fill. Cleared when a layer is selected.
            public string EditingToken;

            public string ActiveFill() =>
                Selected == null ? null
                : EditState != null ? (Selected.GetState(EditState.Value)?.Fill ?? Selected.Fill)
                : Selected.Fill;
            public string ActiveTextColor() =>
                Selected == null ? null
                : EditState != null ? (Selected.GetState(EditState.Value)?.TextColor ?? Selected.TextColor)
                : Selected.TextColor;
            public double ActiveOpacity() =>
                Selected == null ? 1
                : EditState != null ? (Selected.GetState(EditState.Value)?.Opacity ?? Selected.Opacity)
                : Selected.Opacity;

            void ApplyFill(string value) {
                if (Selected == null) return;
                if (EditState != null) Ed.SetStateFill(Selected, EditState.Value, value);
                else Ed.SetFill(Selected, value);
            }
            void ApplyTextColor(string value) {
                if (Selected == null) return;
                if (EditState != null) { Ed.SetStateTextColor(Selected, EditState.Value, value); return; }
                var n = Selected; string old = n.TextColor;
                Ed.Mutate("text color", "textcolor", () => n.TextColor = value, () => n.TextColor = old);
            }
            void ApplyOpacity(double value) {
                if (Selected == null) return;
                if (EditState != null) Ed.SetStateOpacity(Selected, EditState.Value, value);
                else Ed.SetOpacity(Selected, value);
            }

            // ── Gradient fill (linear, 2-stop) ──────────────────────────────────────────────────
            // The fill becomes a CSS linear-gradient string (the compiler passes it straight to
            // `background`). v1: two stops + an angle; the existing HSV picker edits whichever
            // stop is selected. Gradient editing is base-style only (not per interactive state).
            public string GradStop = "start";  // which stop the picker edits ("start" | "end")
            public bool GradMode => EditState == null && Selected != null && IsGradient(Selected.Fill);

            public int GradAngle {
                get { TryParseGradient(Selected?.Fill, out int a, out _, out _); return a; }
            }

            public void OnSetFillType(UIEvent e) {
                if (Selected == null) return;
                bool wantGrad = DataAttr(e, "data-val") == "Gradient";
                if (wantGrad && !IsGradient(Selected.Fill)) {
                    string start = (Selected.Fill != null && Selected.Fill.StartsWith("#")) ? Selected.Fill : "#3b82f6";
                    lastApplied = ComposeGradient(180, start, Darken(start));
                    Ed.SetFill(Selected, lastApplied);
                } else if (!wantGrad && IsGradient(Selected.Fill)) {
                    TryParseGradient(Selected.Fill, out _, out string c0, out _);
                    lastApplied = c0;
                    Ed.SetFill(Selected, c0);   // collapse to the start color
                }
                GradStop = "start";
                Repaint?.Invoke();
            }
            public void OnSetGradStop(UIEvent e) {
                GradStop = DataAttr(e, "data-val") == "end" ? "end" : "start";
                lastApplied = null;   // force the picker to re-sync to the newly selected stop
                Repaint?.Invoke();
            }
            public void OnStepGradAngle(UIEvent e) {
                if (Selected == null || !GradMode) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                TryParseGradient(Selected.Fill, out int ang, out string c0, out string c1);
                ang = ((ang + dir * 15) % 360 + 360) % 360;
                lastApplied = ComposeGradient(ang, c0, c1);
                Ed.SetFill(Selected, lastApplied);
                Repaint?.Invoke();
            }

            static bool IsGradient(string fill) =>
                fill != null && fill.StartsWith("linear-gradient", System.StringComparison.Ordinal);
            static string ComposeGradient(int angle, string c0, string c1) =>
                "linear-gradient(" + angle + "deg, " + c0 + ", " + c1 + ")";
            static string Darken(string hex) =>
                TryHexToHsv(hex, out double h, out double s, out double v) ? HsvToHex(h, s, v * 0.5) : "#1e3a8a";
            // Tolerant parse of the linear-gradient strings we emit: "linear-gradient(Ndeg, #a, #b)".
            static bool TryParseGradient(string fill, out int angle, out string c0, out string c1) {
                angle = 180; c0 = "#3b82f6"; c1 = "#1e3a8a";
                if (!IsGradient(fill)) return false;
                int lp = fill.IndexOf('('), rp = fill.LastIndexOf(')');
                if (lp < 0 || rp <= lp) return false;
                var parts = fill.Substring(lp + 1, rp - lp - 1).Split(',');
                int start = 0;
                if (parts.Length > 0 && parts[0].Trim().EndsWith("deg")) {
                    int.TryParse(parts[0].Trim().Replace("deg", "").Trim(), out angle);
                    start = 1;
                }
                var cols = new List<string>();
                for (int i = start; i < parts.Length; i++) { var p = parts[i].Trim(); if (p.Length > 0) cols.Add(p); }
                if (cols.Count >= 1) c0 = cols[0];
                if (cols.Count >= 2) c1 = cols[cols.Count - 1];
                return true;
            }

            public void OnSetState(UIEvent e) {
                string v = DataAttr(e, "data-val");
                EditState = v == "Hover" ? InteractionState.Hover
                          : v == "Pressed" ? InteractionState.Pressed
                          : (InteractionState?)null;
                Repaint?.Invoke();
            }
            public void OnClearState(UIEvent e) {
                if (Selected == null || EditState == null) return;
                Ed.ClearState(Selected, EditState.Value);
                Repaint?.Invoke();
            }

            // ── Tokens-manager ──────────────────────────────────────────────────────────────────
            public void OnEditToken(UIEvent e) {
                EditingToken = DataAttr(e, "data-tok");
                lastApplied = null;   // force the picker to load this token's colour
                Repaint?.Invoke();
            }
            public void OnAddToken(UIEvent e) {
                if (Ed == null) return;
                // First free "color-N" name.
                var colors = Ed.Document.Tokens.Colors;
                int i = 1; string name; do { name = "color-" + i++; } while (colors.ContainsKey(name));
                Ed.SetColorToken(name, "#888888");
                EditingToken = name;
                lastApplied = null;
                Repaint?.Invoke();
            }
            public void OnDeleteToken(UIEvent e) {
                if (Ed == null || EditingToken == null) return;
                Ed.RemoveColorToken(EditingToken);
                EditingToken = null;
                Repaint?.Invoke();
            }
            public void OnCloseToken(UIEvent e) { EditingToken = null; Repaint?.Invoke(); }

            // ── Collapsible inspector sections ────────────────────────────────────────────────────
            // Stores only sections the user has toggled away from their default; IsSectionOpen
            // falls back to the per-section default otherwise.
            readonly Dictionary<string, bool> sectionOpen = new Dictionary<string, bool>();
            public bool IsSectionOpen(string key, bool def) => sectionOpen.TryGetValue(key, out bool v) ? v : def;
            public void OnToggleSection(UIEvent e) {
                string k = DataAttr(e, "data-sec");
                if (k == null) return;
                bool curOpen = DataAttr(e, "data-open") == "1";
                sectionOpen[k] = !curOpen;
                Repaint?.Invoke();
            }

            // ── Left panel tabs (Layers / Tokens / Library) ───────────────────────────────────────
            public string LeftTab = "layers";
            public void OnSetLeftTab(UIEvent e) {
                string v = DataAttr(e, "data-val");
                if (v != null) { LeftTab = v; if (v != "tokens") EditingToken = null; Repaint?.Invoke(); }
            }
            public void OnInstallKit(UIEvent e) {
                if (Ed == null) return;
                DesignComponentKit.Install(Ed.Document);   // adds Button/Card/Panel/… + theme tokens
                Repaint?.Invoke();
            }
            public void OnAddInstance(UIEvent e) {
                if (Ed == null) return;
                string comp = DataAttr(e, "data-comp");
                if (string.IsNullOrEmpty(comp)) return;
                // Drop into the selected container (or its parent if a leaf), else the root.
                DesignNode parent = (Selected != null && !Selected.IsText) ? Selected
                                  : (Selected != null ? FindParent(Ed.Document.Root, Selected) : null) ?? Ed.Document.Root;
                var inst = Ed.AddInstance(parent, comp);
                if (inst != null) { Selected = inst; LeftTab = "layers"; }
                Repaint?.Invoke();
            }

            // ── Floating menus (New dropdown + right-click context menu) ──────────────────────────
            public bool NewMenuOpen, CtxMenuOpen;
            public double NewMenuX = 8, NewMenuY = 36, CtxX, CtxY;

            public void OnToggleNewMenu(UIEvent e) {
                CtxMenuOpen = false;
                NewMenuOpen = !NewMenuOpen;
                if (NewMenuOpen && e.Target != null) {
                    var box = BoxOf?.Invoke(e.Target);          // anchor the dropdown under the button
                    if (box != null) { AbsXY(box, out double bx, out double by); NewMenuX = bx; NewMenuY = by + box.Height + 2; }
                }
                Repaint?.Invoke();
            }
            public void OnCloseMenus(UIEvent e) {
                if (!NewMenuOpen && !CtxMenuOpen) return;
                NewMenuOpen = false; CtxMenuOpen = false; Repaint?.Invoke();
            }
            void OpenContextMenu(double x, double y) {
                NewMenuOpen = false; CtxMenuOpen = true; CtxX = x; CtxY = y; Repaint?.Invoke();
            }
            // Right-click on the canvas → select the clicked node (so Add targets that container)
            // and open the context menu at the pointer. Left-clicks fall through to node selection.
            public void OnCanvasDown(PointerEvent e) {
                if (DataAttr(e, "data-h") != null) return;  // a resize-handle press → OnResizeDown owns it
                string nid = DataAttr(e, "data-nid");
                if (e.Button == 2) {                       // right-click → select + context menu
                    e.PreventDefault();
                    if (nid != null && IdMap.TryGetValue(nid, out var rn)) Select(rn);
                    OpenContextMenu(e.X, e.Y);
                    return;
                }
                if (e.Button != 0 || nid == null || !IdMap.TryGetValue(nid, out var n)) return;
                // Double-click a text node → edit its text (focus the IMGUI Text field).
                double now = Now?.Invoke() ?? 0;
                bool dbl = nid == lastDownNid && (now - lastDownTime) < 0.4;
                lastDownNid = nid; lastDownTime = now;
                Select(n);                                 // select on press
                if (dbl && n.IsText) { RequestFocusText(); Repaint?.Invoke(); return; }
                canvasMoveStartX = e.X; canvasMoveStartY = e.Y; canvasMoved = false;
                if (n.IsAbsolute) {                         // free move via offsets
                    canvasMoveNid = nid;
                    canvasMoveL = (n.OffLeft ?? default(Dim)).Px;
                    canvasMoveT = (n.OffTop ?? default(Dim)).Px;
                } else if (!ReferenceEquals(n, Ed.Document.Root)) {   // in-flow → drag-to-reorder
                    canvasDragNode = n;                    // by reference: nids shift as it reorders
                }
                Repaint?.Invoke();
            }

            // Centralised select: reset the editing modes that are scoped to the old selection.
            void Select(DesignNode n) {
                if (!ReferenceEquals(n, Selected)) EditState = null;
                EditingToken = null; EditingField = null; EditingBind = null;
                Selected = n;
            }

            // ── Edit text content ─────────────────────────────────────────────────────────────────
            // A request to focus the IMGUI "wd-text" field, raised by the inspector "Edit text"
            // button or by double-clicking a text node; OnPanelChrome consumes it once and calls
            // EditorGUI.FocusTextInControl so the caret lands in the field reliably.
            bool focusTextRequested;
            public void RequestFocusText() { focusTextRequested = true; }
            public bool ConsumeFocusText() { if (!focusTextRequested) return false; focusTextRequested = false; return true; }
            public void OnEditText(UIEvent e) {
                string nid = DataAttr(e, "data-nid");
                DesignNode target = (nid != null && IdMap.TryGetValue(nid, out var hit)) ? hit : Selected;
                if (target == null || !target.IsText) return;
                Select(target);          // also clears the other editor-bar modes so wd-text shows
                RequestFocusText();
                Repaint?.Invoke();
            }

            // ── Canvas drag-to-move (Absolute nodes) ──────────────────────────────────────────────
            // Dragging an absolute node's body on the canvas updates its top/left offsets live.
            // 1 canvas px = 1 design px (no zoom yet), so the pointer delta maps straight to offsets.
            string canvasMoveNid;
            DesignNode canvasDragNode;   // in-flow node being drag-reordered (tracked by ref — nids shift)
            double canvasMoveStartX, canvasMoveStartY, canvasMoveL, canvasMoveT;
            bool canvasMoved;
            string lastDownNid; double lastDownTime;   // double-click detection on the canvas
            void TrackCanvasMove(PointerEvent e) {
                if (e.Buttons == 0) { canvasMoveNid = null; return; }   // released off-window
                double dx = e.X - canvasMoveStartX, dy = e.Y - canvasMoveStartY;
                if (!canvasMoved && dx * dx + dy * dy < 9) return;       // 3px threshold (vs a click)
                canvasMoved = true;
                if (!IdMap.TryGetValue(canvasMoveNid, out var n) || !n.IsAbsolute) { canvasMoveNid = null; return; }
                // One SetOffsets sets top+left together (preserving right/bottom), coalescing to one undo.
                Ed.SetOffsets(n, Dim.Of(canvasMoveT + dy), n.OffRight, n.OffBottom, Dim.Of(canvasMoveL + dx));
            }

            // Drag an in-flow node over its siblings to reorder it live (Figma auto-layout feel).
            // The hovered node is resolved from the pointer's target in the CURRENT frame; the
            // dragged node is held by reference (its nid changes as the tree reorders). before/after
            // is decided along the parent's main axis. MoveNode rejects no-ops / cycles internally.
            void TrackCanvasReorder(PointerEvent e) {
                if (e.Buttons == 0) { canvasDragNode = null; return; }
                double dx = e.X - canvasMoveStartX, dy = e.Y - canvasMoveStartY;
                if (!canvasMoved && dx * dx + dy * dy < 16) return;      // 4px before it's a drag
                canvasMoved = true;
                var dragged = canvasDragNode;
                if (dragged == null || Ed == null) return;
                var el = e.Target;
                while (el != null && el.GetAttribute("data-nid") == null) el = el.Parent as Weva.Dom.Element;
                if (el == null) return;
                if (!IdMap.TryGetValue(el.GetAttribute("data-nid"), out var hovered) || ReferenceEquals(hovered, dragged)) return;
                var refParent = FindParent(Ed.Document.Root, hovered);
                if (refParent == null) return;              // hovered is the root
                var box = BoxOf?.Invoke(el);
                if (box == null || box.Width <= 0 || box.Height <= 0) return;
                AbsXY(box, out double bx, out double by);
                bool after = refParent.Layout == LayoutMode.Row
                    ? e.X > bx + box.Width * 0.5
                    : e.Y > by + box.Height * 0.5;
                int idx = refParent.Children.IndexOf(hovered) + (after ? 1 : 0);
                Ed.MoveNode(dragged, refParent, idx);       // rearranges live; no-op/illegal handled inside
            }

            // ── Canvas resize handles ─────────────────────────────────────────────────────────────
            // Dragging a handle resizes the selected node (switching it to Fixed W/H from its current
            // rendered size). e/s grow the right/bottom edges; w/n grow left/top and, for an absolute
            // node, also shift its offset so the opposite edge stays put. One coalesced undo per drag.
            string resizeHandle;
            double rzStartX, rzStartY, rzW, rzH, rzL, rzT;
            bool rzAbs;
            public void OnResizeDown(PointerEvent e) {
                if (Selected == null || Ed == null) return;
                resizeHandle = DataAttr(e, "data-h");
                if (resizeHandle == null) return;
                e.PreventDefault();
                var n = Selected;
                double w = (n.WidthMode == SizeMode.Fixed && n.Width > 0) ? n.Width : 0;
                double h = (n.HeightMode == SizeMode.Fixed && n.Height > 0) ? n.Height : 0;
                if (w <= 0 || h <= 0) {
                    var box = NodeBoxFrom(e);   // base off the current rendered size
                    if (box != null) { if (w <= 0) w = box.Width; if (h <= 0) h = box.Height; }
                }
                rzW = w > 0 ? w : 1; rzH = h > 0 ? h : 1;
                rzStartX = e.X; rzStartY = e.Y;
                rzAbs = n.IsAbsolute;
                rzL = (n.OffLeft ?? default(Dim)).Px; rzT = (n.OffTop ?? default(Dim)).Px;
            }
            void TrackResize(PointerEvent e) {
                if (e.Buttons == 0 || Selected == null) { resizeHandle = null; return; }
                double dx = e.X - rzStartX, dy = e.Y - rzStartY;
                double w = rzW, h = rzH, l = rzL, t = rzT;
                bool E = resizeHandle.IndexOf('e') >= 0, W = resizeHandle.IndexOf('w') >= 0;
                bool S = resizeHandle.IndexOf('s') >= 0, N = resizeHandle.IndexOf('n') >= 0;
                if (E) w = rzW + dx;
                if (W) { w = rzW - dx; if (rzAbs) l = rzL + dx; }
                if (S) h = rzH + dy;
                if (N) { h = rzH - dy; if (rzAbs) t = rzT + dy; }
                if (w < 1) w = 1;
                if (h < 1) h = 1;
                ApplyResize(w, h, l, t, W && rzAbs, N && rzAbs);
            }
            void ApplyResize(double w, double h, double l, double t, bool setL, bool setT) {
                var n = Selected;
                if (n == null || Ed == null) return;
                SizeMode oWM = n.WidthMode, oHM = n.HeightMode; double oW = n.Width, oH = n.Height;
                Dim? oOL = n.OffLeft, oOT = n.OffTop;
                string key = "resize:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(n);
                Ed.Mutate("Resize", key,
                    () => {
                        n.WidthMode = SizeMode.Fixed; n.HeightMode = SizeMode.Fixed; n.Width = w; n.Height = h;
                        if (setL) n.OffLeft = Dim.Of(l);
                        if (setT) n.OffTop = Dim.Of(t);
                    },
                    () => {
                        n.WidthMode = oWM; n.HeightMode = oHM; n.Width = oW; n.Height = oH;
                        n.OffLeft = oOL; n.OffTop = oOT;
                    });
            }
            Weva.Layout.Boxes.Box NodeBoxFrom(UIEvent e) {
                var el = e?.Target;
                while (el != null && el.GetAttribute("data-nid") == null) el = el.Parent as Weva.Dom.Element;
                return el != null ? BoxOf?.Invoke(el) : null;
            }

            public void OnLoadTemplate(UIEvent e) {
                NewMenuOpen = false;
                LoadTemplate?.Invoke(DataAttr(e, "data-val"));
            }
            public void OnSave(UIEvent e) => Save?.Invoke();
            public void OnSaveAs(UIEvent e) => SaveAs?.Invoke();
            public void OnOpen(UIEvent e) => Open?.Invoke();

            public void OnLayer(UIEvent e) {
                string nid = DataAttr(e, "data-nid");
                if (nid != null && IdMap.TryGetValue(nid, out var n)) {
                    if (!ReferenceEquals(n, Selected)) EditState = null; // new layer → back to base style
                    EditingToken = null;   // leave token-edit mode when a layer is picked
                    EditingField = null;   // close any open type-in editor
                    EditingBind = null;    // close any open bind editor
                    Selected = n;
                    Repaint?.Invoke();
                }
            }

            public void OnUndo(UIEvent e) { if (Ed != null && Ed.CanUndo) { Ed.Undo(); KeepSelection(); Repaint?.Invoke(); } }
            public void OnRedo(UIEvent e) { if (Ed != null && Ed.CanRedo) { Ed.Redo(); KeepSelection(); Repaint?.Invoke(); } }

            // ── Keyboard shortcuts (called from HandleShortcuts, not the DOM) ─────────────────────
            // Escape backs out of the current mode in priority order, else deselects.
            public void OnEscape() {
                if (EditingField != null) EditingField = null;
                else if (EditingBind != null) EditingBind = null;
                else if (NewMenuOpen || CtxMenuOpen) { NewMenuOpen = false; CtxMenuOpen = false; }
                else if (EditingToken != null) EditingToken = null;
                else if (EditState != null) EditState = null;
                else Selected = null;
                Repaint?.Invoke();
            }
            // Arrow-key nudge: move an Absolute node by its top-left offsets; reorder an in-flow
            // node up/down within its parent. Shift = coarse (10px / no effect on reorder).
            public void NudgeSelected(UnityEngine.KeyCode key, bool shift) {
                if (Selected == null || Ed == null) return;
                double step = shift ? 10 : 1;
                if (Selected.IsAbsolute) {
                    double l = (Selected.OffLeft ?? default(Dim)).Px;
                    double t = (Selected.OffTop ?? default(Dim)).Px;
                    if (key == UnityEngine.KeyCode.LeftArrow) ApplyOffset('l', l - step);
                    else if (key == UnityEngine.KeyCode.RightArrow) ApplyOffset('l', l + step);
                    else if (key == UnityEngine.KeyCode.UpArrow) ApplyOffset('t', t - step);
                    else if (key == UnityEngine.KeyCode.DownArrow) ApplyOffset('t', t + step);
                } else {
                    if (key == UnityEngine.KeyCode.UpArrow) Reorder(-1);
                    else if (key == UnityEngine.KeyCode.DownArrow) Reorder(1);
                }
            }

            // ── Inspector edits (each mutates through DocumentEditor → undoable, live re-render) ──
            public void OnSetLayout(UIEvent e) {
                if (TryEnum<LayoutMode>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetLayout(Selected, v);
            }
            public void OnToggleWrap(UIEvent e) {
                if (Selected != null) Ed.SetWrap(Selected, !Selected.Wrap);
            }
            public void OnSetPosition(UIEvent e) {
                if (TryEnum<Position>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetPosition(Selected, v);
            }
            public void OnToggleCursor(UIEvent e) {
                if (Selected != null)
                    Ed.SetCursor(Selected, Selected.Cursor == Cursor.Pointer ? Cursor.Default : Cursor.Pointer);
            }
            public void OnStepCols(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                int cur = Selected.GridColumns >= 1 ? Selected.GridColumns : 1;
                Ed.SetGridColumns(Selected, System.Math.Max(1, cur + dir));
            }
            public void OnSetMain(UIEvent e) {
                if (TryEnum<MainAlign>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetMainAlign(Selected, v);
            }
            public void OnSetCross(UIEvent e) {
                if (TryEnum<CrossAlign>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetCrossAlign(Selected, v);
            }
            // Alignment pad cell → set both axes at once (one undo). data-val is "col,row" (0..2).
            public void OnSetAlignPad(UIEvent e) {
                if (Selected == null || Ed == null) return;
                string val = DataAttr(e, "data-val");
                if (val == null) return;
                var parts = val.Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int col) || !int.TryParse(parts[1], out int row)) return;
                // Horizontal cell → main for a Row, cross for a Column; vertical is the reverse.
                int mainIdx = Selected.Layout == LayoutMode.Column ? row : col;
                int crossIdx = Selected.Layout == LayoutMode.Column ? col : row;
                MainAlign m = mainIdx == 0 ? MainAlign.Start : mainIdx == 1 ? MainAlign.Center : MainAlign.End;
                CrossAlign c = crossIdx == 0 ? CrossAlign.Start : crossIdx == 1 ? CrossAlign.Center : CrossAlign.End;
                Ed.BeginBatch("Align");
                Ed.SetMainAlign(Selected, m);
                Ed.SetCrossAlign(Selected, c);
                Ed.EndBatch();
                Repaint?.Invoke();
            }
            // ── Typography (text nodes) ───────────────────────────────────────────────────────────
            public void OnSetFontWeight(UIEvent e) {
                if (TryEnum<FontWeight>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetFontWeight(Selected, v);
            }
            public void OnSetTextAlign(UIEvent e) {
                if (TryEnum<TextAlign>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetTextAlign(Selected, v);
            }
            public void OnSetTransform(UIEvent e) {
                if (TryEnum<TextTransform>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetTextTransform(Selected, v);
            }
            public void OnSetDecoration(UIEvent e) {
                if (TryEnum<TextDecoration>(DataAttr(e, "data-val"), out var v) && Selected != null)
                    Ed.SetTextDecoration(Selected, v);
            }
            public void OnToggleItalic(UIEvent e) {
                if (Selected != null) Ed.SetItalic(Selected, !Selected.Italic);
            }
            public void OnStepLetterSpacing(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                Ed.SetLetterSpacing(Selected, Selected.LetterSpacing + dir);   // may go negative (tighten)
            }
            public void OnSetTextShadow(UIEvent e) {
                if (Selected == null) return;
                Ed.SetTextShadow(Selected, TextShadowCss(DataAttr(e, "data-val")));   // null for "none"
            }
            public void OnStepRotation(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                Ed.SetRotation(Selected, Selected.Rotation + dir);
            }
            public void OnStepScale(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                Ed.SetScale(Selected, System.Math.Max(0.1, Selected.Scale + dir * 0.05));
            }
            // ── Stroke (border) ───────────────────────────────────────────────────────────────────
            public void OnSetStroke(UIEvent e) {
                if (Selected == null) return;
                string v = DataAttr(e, "data-val");
                Ed.SetStroke(Selected, string.IsNullOrEmpty(v) ? null : "{" + v + "}");
            }
            public void OnStepStrokeW(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double cur = Selected.StrokeWidth > 0 ? Selected.StrokeWidth : 1;
                Ed.SetStrokeWidth(Selected, System.Math.Max(0, cur + dir));
            }
            public void OnSetWidth(UIEvent e) {
                if (!TryEnum<SizeMode>(DataAttr(e, "data-val"), out var v) || Selected == null) return;
                var n = Selected; var oldMode = n.WidthMode; double old = n.Width; bool needDefault = v == SizeMode.Fixed && n.Width <= 0;
                Ed.Mutate("width mode", "wmode",
                    () => { n.WidthMode = v; if (needDefault) n.Width = 200; },
                    () => { n.WidthMode = oldMode; n.Width = old; });
            }
            public void OnSetHeight(UIEvent e) {
                if (!TryEnum<SizeMode>(DataAttr(e, "data-val"), out var v) || Selected == null) return;
                var n = Selected; var oldMode = n.HeightMode; double old = n.Height; bool needDefault = v == SizeMode.Fixed && n.Height <= 0;
                Ed.Mutate("height mode", "hmode",
                    () => { n.HeightMode = v; if (needDefault) n.Height = 120; },
                    () => { n.HeightMode = oldMode; n.Height = old; });
            }
            public void OnSetFill(UIEvent e) {
                if (Selected == null) return;
                string v = DataAttr(e, "data-val");
                ApplyFill(string.IsNullOrEmpty(v) ? null : "{" + v + "}");  // base or active state
            }
            // Raw color (from the palette) — the value is a literal CSS color (e.g. #3b82f6),
            // not a token name. DesignNode.Fill accepts either; the compiler passes raw colors
            // through and resolves {token} refs, so picking any color "just works".
            public void OnSetFillRaw(UIEvent e) {
                if (Selected == null) return;
                Ed.SetFill(Selected, DataAttr(e, "data-val"));
            }
            public void OnSetTextColorRaw(UIEvent e) {
                if (Selected == null) return;
                var n = Selected; string old = n.TextColor; string val = DataAttr(e, "data-val");
                Ed.Mutate("text color", "textcolor", () => n.TextColor = val, () => n.TextColor = old);
            }

            // ── Color picker drag ──────────────────────────────────────────────────────────────
            // Moves are gated on the held-button mask (PointerEvent.Buttons) as well as the
            // grabbed-thumb flag: a move with no button down means the press ended off-window
            // (the pointerup that would clear `dragging` was never delivered), so we self-heal
            // by clearing it — otherwise a stale drag would hijack the next hover.
            public void OnSVDown(PointerEvent e) { dragging = "sv"; UpdateSV(e); }
            public void OnSVMove(PointerEvent e) {
                if (e.Buttons == 0) { dragging = null; return; }
                if (dragging == "sv") UpdateSV(e);
            }
            public void OnHueDown(PointerEvent e) { dragging = "hue"; UpdateHue(e); }
            public void OnHueMove(PointerEvent e) {
                if (e.Buttons == 0) { dragging = null; return; }
                if (dragging == "hue") UpdateHue(e);
            }
            // wd-root pointermove/up: the single place a press is tracked once the pointer
            // wanders off the element it started on. Routes color-picker release and layer
            // drag/drop through one pair of handlers (an element can bind each event once).
            public void OnRootMove(PointerEvent e) {
                if (resizeHandle != null) { TrackResize(e); return; }
                if (canvasMoveNid != null) { TrackCanvasMove(e); return; }
                if (canvasDragNode != null) { TrackCanvasReorder(e); return; }
                if (ScrubKey != null) { TrackScrub(e); return; }
                if (PanelDrag != null) { TrackPanelDrag(e); return; }
                if (DragLayerNid != null) TrackLayerDrag(e);
            }
            public void OnRootUp(UIEvent e) {
                dragging = null;        // color picker: end any SV/hue drag
                PanelDrag = null;       // panel resize: end any divider drag
                canvasMoveNid = null;   // canvas move: end any drag-to-move
                canvasDragNode = null;  // canvas reorder: end any drag-to-reorder
                resizeHandle = null;    // resize: end any handle drag
                EndScrub();             // numeric scrub: end any drag-to-scrub
                CommitLayerDrag();      // layer tree: drop if a real drag is in progress
            }

            // ── Resizable panels ────────────────────────────────────────────────────────────────
            // Drag the divider between a side panel and the canvas to resize it. Width changes
            // re-bake the HTML each frame (rebuild), so tracking relies on the panel-threaded
            // held-button state (PointerEvent.Buttons), which stays accurate across rebuilds —
            // not the dispatcher's per-build mask. Clamped to sane bounds.
            public int LeftPanelW = 220, RightPanelW = 248;
            public string PanelDrag;   // "left" | "right" | null
            double panelDragStartX, panelDragStartW;

            public void OnDividerDown(PointerEvent e) {
                PanelDrag = DataAttr(e, "data-div");
                panelDragStartX = e.X;
                panelDragStartW = PanelDrag == "right" ? RightPanelW : LeftPanelW;
            }
            void TrackPanelDrag(PointerEvent e) {
                if (e.Buttons == 0) { PanelDrag = null; return; }   // released off-window
                double dx = e.X - panelDragStartX;
                // The right divider sits left of the right panel, so dragging it left (negative
                // dx) widens that panel — invert.
                double w = PanelDrag == "right" ? panelDragStartW - dx : panelDragStartW + dx;
                int clamped = (int)System.Math.Max(140, System.Math.Min(520, w));
                int cur = PanelDrag == "right" ? RightPanelW : LeftPanelW;
                if (clamped == cur) return;
                if (PanelDrag == "right") RightPanelW = clamped; else LeftPanelW = clamped;
                Repaint?.Invoke();
            }

            // ── Drag-to-scrub numeric fields (the Figma signature) ───────────────────────────────
            // Press a value (wd-scrub) and drag horizontally to change it: ±1 `scrubStep` per
            // PxPerStep pixels. Each move applies through the same DocumentEditor mutation the
            // ± buttons use, so the whole drag coalesces (per-node merge key) into one undo step.
            // Like the color picker, tracking lives on wd-root (OnRootMove) and reads the held
            // button mask so a release off-window self-heals.
            const double PxPerStep = 4.0;
            string scrubKey;        // which property is being scrubbed (null = none)
            double scrubStartX, scrubStartVal, scrubStep, scrubMin, scrubMax;
            bool scrubMoved;        // did this press actually drag (vs a plain click → type-in)?
            public string ScrubKey => scrubKey;

            // ── Click-to-type numeric editor ──────────────────────────────────────────────────────
            // A click (no drag) on a scrub value sets EditingField; OnPanelChrome then shows an
            // IMGUI number field for it. Labels are recorded during render so the editor can name
            // itself. ScrubValue/ApplyScrubValue expose the private get/set to the IMGUI bar.
            public string EditingField;
            readonly Dictionary<string, string> scrubLabels = new Dictionary<string, string>();
            public void RegisterScrubLabel(string key, string label) { scrubLabels[key] = label; }
            public string EditingFieldLabel =>
                EditingField != null && scrubLabels.TryGetValue(EditingField, out string l) ? l : "Value";
            public double ScrubValue(string key) => ScrubCurrent(key);
            public void ApplyScrubValue(string key, double v) => ApplyScrub(key, v);

            // ── Data-binding editor (text path / event method / list expression) ──────────────────
            public string EditingBind;   // "text" | "click" | "each" | null
            public void OnEditBind(UIEvent e) {
                EditingBind = DataAttr(e, "data-bind");
                EditingField = null; EditingToken = null;
                Repaint?.Invoke();
            }
            public string EditingBindLabel {
                get {
                    switch (EditingBind) {
                        case "text": return "Text bind";
                        case "click": return "On click";
                        case "each": return "Repeat each";
                        default: return "Bind";
                    }
                }
            }
            public string BindCurrent() {
                var n = Selected;
                if (n == null || EditingBind == null) return "";
                var b = n.Binding;
                switch (EditingBind) {
                    case "text": return b?.Text ?? "";
                    case "click": return b != null && b.Events != null && b.Events.TryGetValue("click", out string m) ? m : "";
                    case "each": return b?.RepeatEach ?? "";
                    default: return "";
                }
            }
            public void ApplyBind(string v) {
                var n = Selected;
                if (n == null || Ed == null || EditingBind == null) return;
                switch (EditingBind) {
                    case "text": Ed.SetTextBind(n, string.IsNullOrEmpty(v) ? null : v); break;
                    case "click": Ed.BindEvent(n, "click", v); break;
                    case "each": Ed.SetRepeat(n, string.IsNullOrEmpty(v) ? null : v); break;
                }
                Repaint?.Invoke();
            }
            public void OnClearBinding(UIEvent e) {
                if (Selected != null && Ed != null) Ed.ClearBinding(Selected);
                EditingBind = null;
                Repaint?.Invoke();
            }

            public void OnScrubDown(PointerEvent e) {
                if (Selected == null) return;
                scrubKey = DataAttr(e, "data-scrub");
                if (scrubKey == null) return;
                scrubStartX = e.X;
                scrubStep = ParseD(DataAttr(e, "data-step"), 1);
                scrubMin = ParseD(DataAttr(e, "data-min"), 0);
                scrubMax = ParseD(DataAttr(e, "data-max"), double.MaxValue);
                scrubStartVal = ScrubCurrent(scrubKey);
                scrubMoved = false;
                Repaint?.Invoke();   // show the wd-scrubbing highlight
            }

            // Generic ± step for scrubs that opt in (stepMethod "OnScrubStep"): the key/step/
            // bounds ride on the chip's data attrs, so no per-control handler is needed.
            public void OnScrubStep(UIEvent e) {
                string key = DataAttr(e, "data-scrub");
                if (key == null) return;
                double step = ParseD(DataAttr(e, "data-step"), 1);
                double min = ParseD(DataAttr(e, "data-min"), 0);
                double max = ParseD(DataAttr(e, "data-max"), double.MaxValue);
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double v = ScrubCurrent(key) + dir * step;
                if (v < min) v = min;
                if (v > max) v = max;
                ApplyScrub(key, v);
            }

            void TrackScrub(PointerEvent e) {
                if (e.Buttons == 0) { EndScrub(); return; }   // released off-window
                double steps = System.Math.Round((e.X - scrubStartX) / PxPerStep);
                double v = scrubStartVal + steps * scrubStep;
                if (v < scrubMin) v = scrubMin;
                if (v > scrubMax) v = scrubMax;
                if (System.Math.Abs(v - ScrubCurrent(scrubKey)) < 1e-9) return;   // no change → no rebuild
                scrubMoved = true;
                ApplyScrub(scrubKey, v);   // mutates (coalesces) + fires Changed → Repaint
            }

            void EndScrub() {
                if (scrubKey == null) return;
                string key = scrubKey;
                scrubKey = null;
                if (!scrubMoved) EditingField = key;   // a click (no drag) opens the type-in editor
                Repaint?.Invoke();
            }

            double ScrubCurrent(string key) {
                var n = Selected;
                if (n == null) return 0;
                switch (key) {
                    case "gap": return n.Gap.Px;
                    case "cols": return n.GridColumns >= 1 ? n.GridColumns : 1;
                    case "pad": return n.PadTop.Px;
                    case "padt": return n.PadTop.Px;
                    case "padr": return n.PadRight.Px;
                    case "padb": return n.PadBottom.Px;
                    case "padl": return n.PadLeft.Px;
                    case "radius": return n.Radius.Px;
                    case "radtl": return CornerPx(0);
                    case "radtr": return CornerPx(1);
                    case "radbr": return CornerPx(2);
                    case "radbl": return CornerPx(3);
                    case "w": return n.Width;
                    case "h": return n.Height;
                    case "font": return n.FontSize.Px > 0 ? n.FontSize.Px : 16;
                    case "ls": return n.LetterSpacing;
                    case "strokew": return n.StrokeWidth > 0 ? n.StrokeWidth : 1;
                    case "rot": return n.Rotation;
                    case "scale": return System.Math.Round(n.Scale * 100);
                    case "opacity": return System.Math.Round(ActiveOpacity() * 100);
                    case "minw": return n.MinWidth;
                    case "maxw": return n.MaxWidth;
                    case "minh": return n.MinHeight;
                    case "maxh": return n.MaxHeight;
                    case "trans": return n.TransitionMs;
                    case "offtop": return (n.OffTop ?? default(Dim)).Px;
                    case "offright": return (n.OffRight ?? default(Dim)).Px;
                    case "offbottom": return (n.OffBottom ?? default(Dim)).Px;
                    case "offleft": return (n.OffLeft ?? default(Dim)).Px;
                    default: return 0;
                }
            }

            void ApplyScrub(string key, double v) {
                var n = Selected;
                if (n == null || Ed == null) return;
                switch (key) {
                    case "gap": Ed.SetGap(n, Dim.Of(v)); break;
                    case "cols": Ed.SetGridColumns(n, (int)System.Math.Max(1, v)); break;
                    case "pad": { var d = Dim.Of(v); Ed.SetPadding(n, d, d, d, d); break; }
                    case "padt": ApplyPadSide('t', v); break;
                    case "padr": ApplyPadSide('r', v); break;
                    case "padb": ApplyPadSide('b', v); break;
                    case "padl": ApplyPadSide('l', v); break;
                    case "radius": Ed.SetRadius(n, Dim.Of(v)); break;
                    case "radtl": ApplyCorner(0, v); break;
                    case "radtr": ApplyCorner(1, v); break;
                    case "radbr": ApplyCorner(2, v); break;
                    case "radbl": ApplyCorner(3, v); break;
                    case "w": { double cur = n.Width; Ed.Mutate("width", "w", () => n.Width = v, () => n.Width = cur); break; }
                    case "h": { double cur = n.Height; Ed.Mutate("height", "h", () => n.Height = v, () => n.Height = cur); break; }
                    case "font": Ed.SetFontSize(n, Dim.Of(System.Math.Max(1, v))); break;
                    case "ls": Ed.SetLetterSpacing(n, v); break;
                    case "strokew": Ed.SetStrokeWidth(n, v); break;
                    case "rot": Ed.SetRotation(n, v); break;
                    case "scale": Ed.SetScale(n, v / 100.0); break;
                    case "opacity": ApplyOpacity(v / 100.0); break;
                    case "minw": Ed.SetMinWidth(n, v); break;
                    case "maxw": Ed.SetMaxWidth(n, v); break;
                    case "minh": Ed.SetMinHeight(n, v); break;
                    case "maxh": Ed.SetMaxHeight(n, v); break;
                    case "trans": Ed.SetTransition(n, v); break;
                    case "offtop": ApplyOffset('t', v); break;
                    case "offright": ApplyOffset('r', v); break;
                    case "offbottom": ApplyOffset('b', v); break;
                    case "offleft": ApplyOffset('l', v); break;
                }
            }

            // Set one Absolute edge offset, preserving the others (null = unpinned stays unpinned
            // unless it's the edge being set). Used by the Position section's offset scrubs.
            void ApplyOffset(char edge, double v) {
                var n = Selected;
                if (n == null || Ed == null) return;
                Dim? t = n.OffTop, r = n.OffRight, b = n.OffBottom, l = n.OffLeft;
                var d = Dim.Of(v);
                if (edge == 't') t = d; else if (edge == 'r') r = d; else if (edge == 'b') b = d; else l = d;
                Ed.SetOffsets(n, t, r, b, l);
            }

            static double ParseD(string s, double dflt) =>
                double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : dflt;

            void UpdateSV(PointerEvent e) {
                double px = e.X, py = e.Y;                 // snapshot (PointerEvent is pooled)
                var box = BoxFromTarget(e, "sv");
                if (box == null || box.Width <= 0 || box.Height <= 0) return;
                AbsXY(box, out double bx, out double by);  // BoxLookup is parent-local; pointer is absolute
                PickS = Clamp01((px - bx) / box.Width);
                PickV = Clamp01(1.0 - (py - by) / box.Height);
                ApplyPick();
            }
            void UpdateHue(PointerEvent e) {
                double px = e.X;
                var box = BoxFromTarget(e, "hue");
                if (box == null || box.Width <= 0) return;
                AbsXY(box, out double bx, out double _);
                PickH = Clamp01((px - bx) / box.Width) * 360.0;
                ApplyPick();
            }
            void ApplyPick() {
                if (EditingToken != null) {           // recolor a design token
                    lastApplied = HsvToHex(PickH, PickS, PickV);
                    Ed.SetColorToken(EditingToken, lastApplied);
                    Repaint?.Invoke();
                    return;
                }
                if (Selected == null) return;
                string hex = HsvToHex(PickH, PickS, PickV);
                if (GradMode) {
                    TryParseGradient(Selected.Fill, out int ang, out string c0, out string c1);
                    if (GradStop == "end") c1 = hex; else c0 = hex;
                    lastApplied = ComposeGradient(ang, c0, c1);
                    Ed.SetFill(Selected, lastApplied);
                } else {
                    lastApplied = hex;
                    ApplyFill(hex);   // base fill or the active state's override
                }
                Repaint?.Invoke();
            }

            Weva.Layout.Boxes.Box BoxFromTarget(UIEvent e, string kind) {
                var el = e?.Target;
                while (el != null && el.GetAttribute("data-pick") != kind) el = el.Parent as Weva.Dom.Element;
                return el != null ? BoxOf?.Invoke(el) : null;
            }

            // Accumulate a box's absolute viewport position from its parent-local X/Y. Layout
            // boxes store position relative to their containing block (Box.X/Y are parent-local),
            // so we walk Box.Parent summing offsets and subtracting each scroll container's
            // ScrollX/ScrollY — exactly the transform the paint converter and hit tester apply.
            // The result is in the same absolute space as PointerEvent.X/Y, so the picker's
            // fraction math lines up with where the user actually clicked.
            static void AbsXY(Weva.Layout.Boxes.Box box, out double ax, out double ay) {
                ax = 0; ay = 0;
                for (var b = box; b != null; b = b.Parent) {
                    ax += b.X; ay += b.Y;
                    var p = b.Parent;
                    if (p != null) { ax -= p.ScrollX; ay -= p.ScrollY; }
                }
            }

            // ── Layer drag-and-drop ─────────────────────────────────────────────────────────────
            // Pressing a layer row arms a drag; it only becomes a real drag once the pointer
            // moves past a small threshold (so a plain press still selects via on-click, whose
            // target resolves to the row). A real drag tracks the row under the pointer and,
            // on release, reparents/reorders through DocumentEditor.MoveNode (one undo step).
            public void OnLayerDown(PointerEvent e) {
                string nid = DataAttr(e, "data-nid");
                if (e.Button == 2) {            // right-click → select this layer + context menu
                    e.PreventDefault();
                    if (nid != null && IdMap.TryGetValue(nid, out var rn)) { Selected = rn; EditState = null; EditingToken = null; EditingField = null; }
                    OpenContextMenu(e.X, e.Y);
                    return;
                }
                DragLayerNid = nid;
                dragStartY = e.Y;
                DragActive = false;
                DropNid = null; DropAfter = false; DropInside = false;
                // Select on press, not just on the click: a press that turns into a drag (or
                // whose click is swallowed by the panel's mid-gesture rebuild) should still
                // select the layer. This is also the conventional select-on-mousedown feel.
                if (nid != null && IdMap.TryGetValue(nid, out var n) && !ReferenceEquals(n, Selected)) {
                    EditState = null;   // new layer → back to base style
                    EditingToken = null;
                    Selected = n;
                    Repaint?.Invoke();
                }
            }

            void TrackLayerDrag(PointerEvent e) {
                if (e.Buttons == 0) { CancelLayerDrag(); return; }   // press ended off-window
                if (!DragActive) {
                    if (System.Math.Abs(e.Y - dragStartY) < DragThreshold) return; // still a click
                    DragActive = true;
                }
                ResolveDrop(e, out string nid, out bool after, out bool inside);
                if (nid != DropNid || after != DropAfter || inside != DropInside) {
                    DropNid = nid; DropAfter = after; DropInside = inside;
                    Repaint?.Invoke();   // refresh the insertion indicator
                }
            }

            // Figure out where a drop at the pointer would land: which row it's over, whether
            // before/after it, or (for a container row) nested inside it. Hovering the dragged
            // row itself yields no target (MoveNode would reject it anyway).
            void ResolveDrop(PointerEvent e, out string nid, out bool after, out bool inside) {
                nid = null; after = false; inside = false;
                var el = e?.Target;
                while (el != null && el.GetAttribute("data-nid") == null) el = el.Parent as Weva.Dom.Element;
                if (el == null) return;
                string overNid = el.GetAttribute("data-nid");
                if (overNid == DragLayerNid) return;     // don't indicate a drop onto the source
                var box = BoxOf?.Invoke(el);
                if (box == null || box.Height <= 0) return;
                AbsXY(box, out double _, out double by);
                double frac = (e.Y - by) / box.Height;
                nid = overNid;
                bool container = IdMap.TryGetValue(overNid, out var n) && n != null && !n.IsText;
                if (container && frac > 0.30 && frac < 0.70) { inside = true; return; }
                after = frac >= 0.5;
            }

            void CommitLayerDrag() {
                if (DragLayerNid == null) { CancelLayerDrag(); return; }
                bool wasDrag = DragActive && DropNid != null;
                if (wasDrag && Ed != null
                    && IdMap.TryGetValue(DragLayerNid, out var node) && node != null
                    && IdMap.TryGetValue(DropNid, out var refNode) && refNode != null) {
                    bool moved;
                    if (DropInside) {
                        moved = Ed.MoveNode(node, refNode, refNode.Children.Count); // append into container
                    } else {
                        var parent = FindParent(Ed.Document.Root, refNode);
                        if (parent != null) {
                            int index = parent.Children.IndexOf(refNode) + (DropAfter ? 1 : 0);
                            moved = Ed.MoveNode(node, parent, index);
                        } else moved = false;
                    }
                    if (moved) Selected = node;   // keep the moved node selected
                }
                CancelLayerDrag();
            }

            void CancelLayerDrag() {
                bool hadIndicator = DragActive && DropNid != null;
                DragLayerNid = null; DragActive = false;
                DropNid = null; DropAfter = false; DropInside = false;
                if (hadIndicator) Repaint?.Invoke();  // clear any drawn indicator
            }

            // Reflect the selected node's hex fill in the picker (unless mid-drag, when the
            // picker is the source of truth). Token / non-hex fills leave the picker as-is.
            public void SyncPickFromFill() {
                if (dragging != null) return;
                if (EditingToken != null) {           // load the token's current colour
                    if (Ed != null && Ed.Document.Tokens.Colors.TryGetValue(EditingToken, out string tc)
                        && tc != lastApplied && TryHexToHsv(tc, out double th, out double tsx, out double tv)) {
                        PickH = th; PickS = tsx; PickV = tv;
                    }
                    return;
                }
                if (Selected == null) return;
                if (GradMode) {
                    // Load the currently-selected gradient stop into the picker.
                    if (Selected.Fill == lastApplied) return;
                    TryParseGradient(Selected.Fill, out _, out string c0, out string c1);
                    string stopHex = GradStop == "end" ? c1 : c0;
                    if (TryHexToHsv(stopHex, out double gh, out double gs, out double gv)) {
                        PickH = gh; PickS = gs; PickV = gv;
                    }
                    return;
                }
                string fill = ActiveFill();   // base fill, or the active state's override
                // The fill we just wrote round-trips exactly back to our H/S/V except at low
                // saturation (where hue is ambiguous from hex). Keep our working HSV when the
                // fill is still what the picker last applied, so the hue thumb doesn't jump.
                if (fill == lastApplied) return;
                if (TryHexToHsv(fill, out double h, out double s, out double v)) {
                    PickH = h; PickS = s; PickV = v;
                }
            }

            static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

            // ── HSV ↔ hex ──────────────────────────────────────────────────────────────────────
            public static string HsvToHex(double h, double s, double v) {
                h = ((h % 360) + 360) % 360;
                double c = v * s, x = c * (1 - System.Math.Abs((h / 60.0) % 2 - 1)), m = v - c;
                double r = 0, g = 0, b = 0;
                if (h < 60) { r = c; g = x; }
                else if (h < 120) { r = x; g = c; }
                else if (h < 180) { g = c; b = x; }
                else if (h < 240) { g = x; b = c; }
                else if (h < 300) { r = x; b = c; }
                else { r = c; b = x; }
                int ri = (int)System.Math.Round((r + m) * 255), gi = (int)System.Math.Round((g + m) * 255), bi = (int)System.Math.Round((b + m) * 255);
                return "#" + ri.ToString("x2") + gi.ToString("x2") + bi.ToString("x2");
            }
            static bool TryHexToHsv(string hex, out double h, out double s, out double v) {
                h = s = v = 0;
                if (string.IsNullOrEmpty(hex) || hex[0] != '#' || (hex.Length != 7 && hex.Length != 4)) return false;
                try {
                    int r, g, b;
                    if (hex.Length == 7) { r = System.Convert.ToInt32(hex.Substring(1, 2), 16); g = System.Convert.ToInt32(hex.Substring(3, 2), 16); b = System.Convert.ToInt32(hex.Substring(5, 2), 16); }
                    else { r = System.Convert.ToInt32("" + hex[1] + hex[1], 16); g = System.Convert.ToInt32("" + hex[2] + hex[2], 16); b = System.Convert.ToInt32("" + hex[3] + hex[3], 16); }
                    double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
                    double max = System.Math.Max(rd, System.Math.Max(gd, bd)), min = System.Math.Min(rd, System.Math.Min(gd, bd)), d = max - min;
                    v = max; s = max <= 0 ? 0 : d / max;
                    if (d > 0) {
                        if (max == rd) h = 60 * (((gd - bd) / d) % 6);
                        else if (max == gd) h = 60 * ((bd - rd) / d + 2);
                        else h = 60 * ((rd - gd) / d + 4);
                        if (h < 0) h += 360;
                    }
                    return true;
                } catch { return false; }
            }
            public void OnSetTextColor(UIEvent e) {
                if (Selected == null) return;
                string v = DataAttr(e, "data-val");
                ApplyTextColor(string.IsNullOrEmpty(v) ? null : "{" + v + "}");  // base or active state
            }
            public void OnStepW(UIEvent e) => StepFixed(e, true);
            public void OnStepH(UIEvent e) => StepFixed(e, false);
            public void OnStepOpacity(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double next = System.Math.Max(0, System.Math.Min(1, System.Math.Round(ActiveOpacity() * 100 + dir * 10) / 100.0));
                ApplyOpacity(next);   // base or active state
            }

            void StepFixed(UIEvent e, bool width) {
                if (Selected == null) return;
                var n = Selected;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double cur = width ? n.Width : n.Height;
                double next = System.Math.Max(0, cur + dir * 8);
                if (width) Ed.Mutate("width", "w", () => n.Width = next, () => n.Width = cur);
                else Ed.Mutate("height", "h", () => n.Height = next, () => n.Height = cur);
            }

            public void OnStepGap(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double next = System.Math.Max(0, Selected.Gap.Px + dir * 4);
                Ed.SetGap(Selected, Dim.Of(next));
            }
            public void OnStepPad(UIEvent e) {
                if (Selected == null) return;
                var n = Selected;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double next = System.Math.Max(0, n.PadTop.Px + dir * 4);
                Ed.SetPadding(n, Dim.Of(next), Dim.Of(next), Dim.Of(next), Dim.Of(next));
            }

            // ── Per-side padding (Figma link toggle) ──────────────────────────────────────────────
            // Linked vs per-side is editor-only UI state (the node always stores four sides).
            public bool PadLinked = true;
            public void OnTogglePadLink(UIEvent e) { PadLinked = !PadLinked; Repaint?.Invoke(); }

            public void OnStepPadT(UIEvent e) => StepPadSide(e, 't');
            public void OnStepPadR(UIEvent e) => StepPadSide(e, 'r');
            public void OnStepPadB(UIEvent e) => StepPadSide(e, 'b');
            public void OnStepPadL(UIEvent e) => StepPadSide(e, 'l');

            void StepPadSide(UIEvent e, char side) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                ApplyPadSide(side, System.Math.Max(0, PadSidePx(side) + dir * 4));
            }

            double PadSidePx(char side) {
                var n = Selected;
                return side == 't' ? n.PadTop.Px : side == 'r' ? n.PadRight.Px
                     : side == 'b' ? n.PadBottom.Px : n.PadLeft.Px;
            }

            void ApplyPadSide(char side, double v) {
                var n = Selected;
                if (n == null || Ed == null) return;
                Dim t = n.PadTop, r = n.PadRight, b = n.PadBottom, l = n.PadLeft;
                var d = Dim.Of(v);
                if (side == 't') t = d; else if (side == 'r') r = d; else if (side == 'b') b = d; else l = d;
                Ed.SetPadding(n, t, r, b, l);
            }
            public void OnStepRadius(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double next = System.Math.Max(0, Selected.Radius.Px + dir * 2);
                Ed.SetRadius(Selected, Dim.Of(next));
            }
            // One-click corner preset (data-val is the px radius; "Round" sends a large value the
            // engine clamps to a full pill).
            public void OnSetRadiusPreset(UIEvent e) {
                if (Selected == null) return;
                if (int.TryParse(DataAttr(e, "data-val"), out int px))
                    Ed.SetRadius(Selected, Dim.Of(px));
            }

            // ── Per-corner radius (Figma link toggle) ─────────────────────────────────────────────
            // Linked vs per-corner is editor-only UI state. Switching back to linked collapses any
            // per-corner overrides so the uniform Radius takes over (matches Figma's "link" reset).
            public bool RadiusLinked = true;
            public void OnToggleRadiusLink(UIEvent e) {
                if (Selected == null) return;
                RadiusLinked = !RadiusLinked;
                if (RadiusLinked && Selected.HasPerCornerRadius && Ed != null)
                    Ed.SetCornerRadii(Selected, null, null, null, null);
                Repaint?.Invoke();
            }

            public void OnStepRadTL(UIEvent e) => StepCorner(e, 0);
            public void OnStepRadTR(UIEvent e) => StepCorner(e, 1);
            public void OnStepRadBR(UIEvent e) => StepCorner(e, 2);
            public void OnStepRadBL(UIEvent e) => StepCorner(e, 3);

            void StepCorner(UIEvent e, int corner) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                ApplyCorner(corner, System.Math.Max(0, CornerPx(corner) + dir * 2));
            }

            // The corner's effective radius: its override if set, else the uniform Radius.
            double CornerPx(int corner) {
                var n = Selected;
                Dim? c = corner == 0 ? n.RadiusTopLeft : corner == 1 ? n.RadiusTopRight
                       : corner == 2 ? n.RadiusBottomRight : n.RadiusBottomLeft;
                return (c ?? n.Radius).Px;
            }

            void ApplyCorner(int corner, double v) {
                var n = Selected;
                if (n == null || Ed == null) return;
                Dim? tl = n.RadiusTopLeft, tr = n.RadiusTopRight, br = n.RadiusBottomRight, bl = n.RadiusBottomLeft;
                var d = Dim.Of(v);
                if (corner == 0) tl = d; else if (corner == 1) tr = d; else if (corner == 2) br = d; else bl = d;
                Ed.SetCornerRadii(n, tl, tr, br, bl);
            }
            public void OnSetShadow(UIEvent e) {
                if (Selected == null) return;
                Ed.SetShadow(Selected, ShadowCss(DataAttr(e, "data-val")));   // null for "none"
            }
            public void OnStepFont(UIEvent e) {
                if (Selected == null) return;
                int dir = DataAttr(e, "data-val") == "-1" ? -1 : 1;
                double next = System.Math.Max(1, Selected.FontSize.Px + dir * 2);
                Ed.SetFontSize(Selected, Dim.Of(next));
            }
            public void OnMoveUp(UIEvent e) => Reorder(-1);
            public void OnMoveDown(UIEvent e) => Reorder(1);
            void Reorder(int delta) {
                if (Ed == null || Selected == null) return;
                var parent = FindParent(Ed.Document.Root, Selected);
                if (parent == null) return;
                int from = parent.Children.IndexOf(Selected);
                int to = from + delta;
                if (from < 0 || to < 0 || to >= parent.Children.Count) return;
                Ed.MoveChild(parent, from, to);
                Repaint?.Invoke();
            }

            // ── Structural ops (add / duplicate / delete) ──────────────────────────────────────
            public void OnAddFrame(UIEvent e) {
                CtxMenuOpen = false;
                if (Ed == null || Selected == null) return;
                var f = new DesignNode("Frame") { Layout = LayoutMode.Column, Fill = PickToken(Ed.Document, "surface", "panel", "card", "muted") };
                f.SetFixedSize(140, 90);
                Ed.AppendChild(Selected, f);
                Selected = f;
                Repaint?.Invoke();
            }
            public void OnAddText(UIEvent e) {
                CtxMenuOpen = false;
                if (Ed == null || Selected == null) return;
                var t = new DesignNode("Text") { Text = "Text", TextColor = PickToken(Ed.Document, "text", "fg"), FontSize = 16 };
                Ed.AppendChild(Selected, t);
                Selected = t;
                Repaint?.Invoke();
            }
            public void OnDuplicate(UIEvent e) {
                CtxMenuOpen = false;
                if (Ed == null || Selected == null) return;
                var parent = FindParent(Ed.Document.Root, Selected);
                if (parent == null) return; // root can't be duplicated in place
                var dup = Ed.Duplicate(parent, Selected);
                if (dup != null) Selected = dup;
                Repaint?.Invoke();
            }
            public void OnDelete(UIEvent e) {
                CtxMenuOpen = false;
                if (Ed == null || Selected == null) return;
                var parent = FindParent(Ed.Document.Root, Selected);
                if (parent == null) return; // can't delete the root
                Ed.RemoveChild(parent, Selected);
                Selected = parent;
                Repaint?.Invoke();
            }

            static DesignNode FindParent(DesignNode root, DesignNode target) {
                if (root == null) return null;
                for (int i = 0; i < root.Children.Count; i++) {
                    if (ReferenceEquals(root.Children[i], target)) return root;
                    var r = FindParent(root.Children[i], target);
                    if (r != null) return r;
                }
                return null;
            }

            static string PickToken(DesignDocument doc, params string[] preferred) {
                foreach (var k in preferred) if (doc.Tokens.Colors.ContainsKey(k)) return "{" + k + "}";
                foreach (var kv in doc.Tokens.Colors) return "{" + kv.Key + "}";
                return null;
            }

            static bool TryEnum<T>(string s, out T value) where T : struct {
                return System.Enum.TryParse(s, true, out value);
            }

            static string DataAttr(UIEvent e, string name) {
                var el = e?.Target;
                while (el != null && el.GetAttribute(name) == null) el = el.Parent as Weva.Dom.Element;
                return el?.GetAttribute(name);
            }

            // After undo/redo the Selected node may have been removed from the tree; fall back to root.
            void KeepSelection() {
                if (Ed == null) return;
                if (Selected == null || !InTree(Ed.Document.Root, Selected)) Selected = Ed.Document.Root;
            }

            static bool InTree(DesignNode root, DesignNode target) {
                if (root == null) return false;
                if (ReferenceEquals(root, target)) return true;
                for (int i = 0; i < root.Children.Count; i++)
                    if (InTree(root.Children[i], target)) return true;
                return false;
            }
        }

        // ── Chrome stylesheet (the editor's own look; node classes are scoped separately) ──────
        const string ChromeCss = @"
body { margin:0; }
.wd-root { width:100%; height:100%; display:flex; flex-direction:column; background:#1b1b1f; color:#e6e6e6; font-family:sans-serif; box-sizing:border-box; }
.wd-toolbar { display:flex; align-items:center; height:38px; padding:0 12px; background:#232329; border-bottom:1px solid #34343c; }
.wd-brand { font-size:13px; font-weight:600; color:#ffffff; }
.wd-dirty { color:#f59e0b; font-size:14px; margin-left:6px; }
.wd-spacer { flex:1; }
.wd-tgap { width:14px; }
.wd-tlabel { font-size:11px; color:#8a8a93; margin-right:6px; }
.wd-btn { font-size:12px; color:#cdd0d6; background:#2e2e36; border-radius:5px; padding:5px 12px; margin-left:8px; }
.wd-btn:hover { background:#3a3a44; color:#ffffff; }
.wd-btn-off { color:#5a5a63; background:#26262c; }
.wd-caret { font-size:9px; margin-left:5px; color:#8a8a93; }
/* Floating menus (New dropdown + right-click context menu) over a click-catching scrim. */
.wd-scrim { position:absolute; left:0; top:0; right:0; bottom:0; z-index:50; }
.wd-menu { position:absolute; z-index:51; min-width:148px; background:#2a2a32; border:1px solid #3a3a44; border-radius:6px; padding:4px; box-shadow:0 8px 24px rgba(0,0,0,0.5); }
.wd-menu-item { display:block; font-size:12px; color:#d7dade; padding:6px 10px; border-radius:4px; cursor:pointer; }
.wd-menu-item:hover { background:#3b82f6; color:#ffffff; }
.wd-menu-item-off { color:#5a5a63; }
.wd-menu-sep { height:1px; background:#3a3a44; margin:4px 2px; }
.wd-menu-hdr { font-size:10px; letter-spacing:0.5px; text-transform:uppercase; color:#8a8a93; padding:4px 10px 2px 10px; }
.wd-body { flex:1; display:flex; min-height:0; }
.wd-panel { flex:0 0 220px; width:220px; min-width:0; background:#202026; display:flex; flex-direction:column; overflow:auto; }
.wd-left { border-right:1px solid #34343c; }
.wd-right { flex:0 0 248px; width:248px; border-left:1px solid #34343c; }
.wd-panel-title { font-size:11px; letter-spacing:1px; text-transform:uppercase; color:#8a8a93; padding:12px 14px 8px 14px; }
.wd-tabs { display:flex; border-bottom:1px solid #34343c; }
.wd-tab { flex:1; text-align:center; font-size:11px; letter-spacing:0.6px; text-transform:uppercase; color:#8a8a93; padding:9px 0; cursor:pointer; border-bottom:2px solid transparent; }
.wd-tab:hover { color:#cdd0d6; background:#26262e; }
.wd-tab-on { color:#ffffff; border-bottom-color:#3b82f6; }
.wd-layers { flex:1; }
.wd-layer { display:flex; align-items:center; height:26px; font-size:12px; color:#c7cbd1; }
.wd-layer:hover { background:#2a2a32; }
.wd-layer-sel { background:#2f3a52; color:#ffffff; }
.wd-layer-dragging { opacity:0.4; }
.wd-drop-before { box-shadow:inset 0 2px 0 0 #38bdf8; }
.wd-drop-after { box-shadow:inset 0 -2px 0 0 #38bdf8; }
.wd-drop-inside { box-shadow:inset 0 0 0 2px #38bdf8; background:#26323f; }
.wd-layer-ico { width:18px; color:#7b8190; font-size:11px; }
.wd-layer-name { flex:1; }
.wd-canvas { flex:1; background:#16161a; display:flex; align-items:flex-start; justify-content:center; padding:24px; box-sizing:border-box; overflow:auto; }
.wd-canvas-surface { flex:none; background:#0e0e12; border-radius:10px; box-shadow:0 8px 30px rgba(0,0,0,0.45); overflow:hidden; }
.wd-canvas-sel { outline:2px solid #38bdf8; outline-offset:1px; position:relative; }
/* Resize handles — absolutely positioned at the selected node's corners/edges. The compiled
   per-node CSS (emitted later in the cascade) keeps absolute nodes absolute, so adding
   position:relative above only promotes static nodes to a positioning context. */
.wd-rh { position:absolute; width:9px; height:9px; background:#38bdf8; border:1px solid #ffffff; box-sizing:border-box; z-index:60; }
.wd-rh-nw { left:-5px; top:-5px; } .wd-rh-ne { right:-5px; top:-5px; }
.wd-rh-se { right:-5px; bottom:-5px; } .wd-rh-sw { left:-5px; bottom:-5px; }
.wd-rh-n { left:50%; top:-5px; margin-left:-5px; cursor:ns-resize; }
.wd-rh-s { left:50%; bottom:-5px; margin-left:-5px; cursor:ns-resize; }
.wd-rh-w { top:50%; left:-5px; margin-top:-5px; cursor:ew-resize; }
.wd-rh-e { top:50%; right:-5px; margin-top:-5px; cursor:ew-resize; }
.wd-size-badge { position:absolute; left:0; bottom:-20px; background:#38bdf8; color:#08131f; font-size:10px; padding:1px 6px; border-radius:3px; z-index:60; }
.wd-insp { padding:4px 0; }
/* Collapsible inspector sections (click the header to fold/unfold). */
.wd-sec-hdr { display:flex; align-items:center; height:28px; padding:0 12px; margin-top:2px; cursor:pointer; background:#1d1d22; border-top:1px solid #2c2c34; }
.wd-sec-hdr:hover { background:#26262e; }
.wd-sec-chev { width:14px; color:#8a8a93; font-size:9px; }
.wd-sec-title { font-size:11px; letter-spacing:0.6px; text-transform:uppercase; color:#b9bdc4; font-weight:600; }
.wd-field { display:flex; align-items:center; padding:6px 14px; font-size:12px; }
.wd-field-col { flex-direction:column; align-items:stretch; }
.wd-flabel { width:84px; color:#8a8a93; }
.wd-fval { flex:1; color:#e6e6e6; }
.wd-chips { flex:1; display:flex; flex-wrap:wrap; gap:4px; }
.wd-chip { font-size:11px; color:#b9bdc4; background:#2a2a32; border-radius:4px; padding:3px 9px; }
.wd-chip:hover { background:#3a3a44; color:#ffffff; }
.wd-chip-on { background:#3b82f6; color:#ffffff; }
.wd-step { flex:1; display:flex; align-items:center; gap:8px; }
/* Figma-style 3×3 alignment pad: one click sets both axes' alignment. */
.wd-align-pad { display:grid; grid-template-columns:repeat(3,1fr); grid-template-rows:repeat(3,1fr); gap:2px; width:66px; height:66px; background:#26262c; border-radius:6px; padding:4px; box-sizing:border-box; }
.wd-align-cell { display:flex; align-items:center; justify-content:center; border-radius:3px; }
.wd-align-cell:hover { background:#33333c; }
.wd-align-on { background:#3b82f6; }
.wd-align-dot { width:6px; height:6px; border-radius:50%; background:#7b8190; }
.wd-align-cell:hover .wd-align-dot { background:#ffffff; }
.wd-align-on .wd-align-dot { background:#ffffff; }
.wd-step-val { min-width:46px; color:#e6e6e6; }
/* Figma-style drag-to-scrub value: grab and drag left/right to change the number. */
.wd-scrub { min-width:46px; text-align:center; color:#e6e6e6; cursor:ew-resize; padding:2px 8px; border-radius:4px; border-bottom:1px dotted #4a4a55; }
.wd-scrub:hover { background:#2a2a32; color:#ffffff; border-bottom-color:#3b82f6; }
.wd-scrubbing { background:#3b82f6; color:#ffffff; border-bottom-color:#3b82f6; }
.wd-swatches { display:flex; flex-wrap:wrap; gap:6px; margin-top:6px; }
.wd-sw { width:20px; height:20px; border-radius:5px; border:2px solid transparent; box-sizing:border-box; }
.wd-sw:hover { border-color:#5a5a63; }
.wd-sw-on { border-color:#ffffff; }
.wd-sw-none { background:#2a2a32; color:#8a8a93; font-size:12px; }
.wd-sv { position:relative; width:100%; height:120px; border-radius:6px; margin-top:6px; }
.wd-hue { position:relative; width:100%; height:14px; border-radius:7px; margin-top:8px; background:linear-gradient(to right,#ff0000,#ffff00,#00ff00,#00ffff,#0000ff,#ff00ff,#ff0000); }
.wd-pthumb { position:absolute; width:12px; height:12px; border-radius:50%; border:2px solid #ffffff; box-sizing:border-box; margin-left:-6px; margin-top:-6px; }
.wd-hthumb { top:50%; width:6px; height:18px; border-radius:3px; margin-left:-3px; margin-top:-9px; }
.wd-cprev { display:flex; align-items:center; margin-top:8px; }
.wd-cswatch { width:20px; height:20px; border-radius:5px; border:1px solid #34343c; margin-right:8px; }
.wd-chex { font-size:12px; color:#cdd0d6; }
.wd-empty { font-size:12px; color:#6b6b73; padding:14px; }
.wd-bindval { flex:1; font-size:11px; color:#cdd0d6; padding:3px 0; overflow:hidden; }
.wd-statehint { font-size:11px; color:#8a8a93; padding:2px 14px 6px 14px; line-height:1.4; }
.wd-gradprev { height:20px; border-radius:5px; margin:8px 14px 0 14px; border:1px solid #34343c; }
.wd-divider { flex:0 0 5px; width:5px; background:#34343c; cursor:ew-resize; }
.wd-divider:hover { background:#3b82f6; }
.wd-lib { padding:8px; }
.wd-libhint { font-size:11px; color:#8a8a93; padding:2px 4px 8px 4px; line-height:1.4; }
.wd-libitem { display:flex; align-items:center; height:30px; font-size:12px; color:#c7cbd1; padding:0 8px; border-radius:5px; cursor:pointer; }
.wd-libitem:hover { background:#2f3a52; color:#ffffff; }
.wd-libitem-ico { width:20px; color:#7b8190; }
.wd-libitem-name { flex:1; }
.wd-libbtn { font-size:12px; color:#cdd0d6; background:#2e2e36; border-radius:5px; padding:8px 10px; margin:6px 4px; text-align:center; cursor:pointer; }
.wd-libbtn:hover { background:#3b82f6; color:#ffffff; }
.wd-tokens { padding:0 8px 8px 8px; }
.wd-token { display:flex; align-items:center; height:24px; font-size:12px; color:#c7cbd1; padding:0 6px; border-radius:4px; }
.wd-token:hover { background:#2a2a32; }
.wd-token-on { background:#2f3a52; color:#ffffff; }
.wd-token-sw { width:14px; height:14px; border-radius:3px; border:1px solid #34343c; margin-right:8px; }
.wd-token-name { flex:1; }
.wd-token-add { color:#8a8a93; }
.wd-token-add:hover { color:#ffffff; }
";
    }
}
#endif
