using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements; // ObjectField
using UnityEngine;
using UnityEngine.UIElements;
using Weva;
using Weva.Css.Cascade;
using Weva.DevTools;
using Weva.Documents;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Rendering;
// UnityEngine.UIElements also declares a `Box` VisualElement — alias the
// layout Box so unqualified uses below resolve to the engine type.
using Box = Weva.Layout.Boxes.Box;

namespace Weva.EditorTools.DevTools {
    // Chrome DevTools "Elements" panel for Weva documents.
    //
    // Three-pane layout (UIToolkit):
    //   LEFT:  Toolbar (document picker + refresh + search) + TreeView of DOM nodes.
    //   RIGHT: Styles tab (rule blocks ordered winner-first, overridden declarations
    //          rendered dim with a leading '~') and a Computed foldout with box-model
    //          diagram + sorted property list.
    //
    // SelectionHighlightSource is registered on open, unregistered on close.
    // Works in edit mode (editModePreview) and in play mode.
    // Live refresh via EditorApplication.update polling DomTreeModel.IsDirty at ~4 Hz.
    public sealed class ElementsWindow : EditorWindow {
        // --- state serialized for domain-reload survival ---
        [SerializeField] WevaDocument targetDocument;
        // Int-index path from root used to restore selection after domain reload.
        [SerializeField] int[] selectionPath;

        // --- runtime state (not serialized) ---
        DomTreeModel treeModel;
        // RuleBlockBuilder is a static class — no instance needed (calls are static).
        ComputedStyleModel computedModel;
        SelectionHighlightSource highlight;
        bool highlightRegistered;

        // Selected element
        Element selectedElement;
        Box selectedBox;

        // UI elements
        ObjectField documentField;
        Button pickButton;
        TreeView domTreeView;
        VisualElement stylesContainer;
        VisualElement computedContainer;
        ScrollView stylesScrollView;
        ScrollView computedScrollView;
        TextField searchField;
        TextField computedSearchField;
        Label noDocumentLabel;

        // Refresh throttle: ~4 Hz = 250 ms between polls.
        double lastRefreshTime;
        const double RefreshIntervalSeconds = 0.25;

        // "Pick" mode: when true, the next click in the game view selects an element.
        bool pickModeActive;

        // Last known document identity (to detect document changes between updates).
        Document lastKnownDoc;
        int lastTreeVersion = -1;

        [MenuItem("Window/Weva/Elements", priority = 201)]
        public static void Open() {
            var w = GetWindow<ElementsWindow>("Weva Elements");
            w.minSize = new Vector2(600, 400);
            w.Show();
        }

        void OnEnable() {
            treeModel = new DomTreeModel();
            computedModel = new ComputedStyleModel();
            highlight = new SelectionHighlightSource();

            EditorApplication.update += OnEditorUpdate;
            BuildUI();

            // Try auto-pick first document in scene.
            if (targetDocument == null) {
                targetDocument = FindFirstDocument();
            }

            if (targetDocument != null) {
                // BuildUI ran before the auto-pick, so reflect the picked
                // document into the ObjectField (without re-triggering the
                // change callback, which would re-attach).
                documentField?.SetValueWithoutNotify(targetDocument);
                AttachDocument(targetDocument);
            }
        }

        void OnDisable() {
            EditorApplication.update -= OnEditorUpdate;
            DetachDocument();
            UnregisterHighlight();
            treeModel?.UnsubscribeFromCurrent();
        }

        // -- UI construction --

        void BuildUI() {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Row;
            rootVisualElement.style.flexGrow = 1;

            // LEFT pane
            var left = new VisualElement();
            left.style.flexDirection = FlexDirection.Column;
            left.style.minWidth = 200;
            left.style.flexGrow = 0.4f;
            left.style.flexShrink = 1;
            rootVisualElement.Add(left);

            // Toolbar
            var toolbar = new VisualElement();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.backgroundColor = new StyleColor(new Color(0.22f, 0.22f, 0.22f));
            toolbar.style.paddingLeft = 4;
            toolbar.style.paddingRight = 4;
            toolbar.style.paddingTop = 2;
            toolbar.style.paddingBottom = 2;
            left.Add(toolbar);

            // Document object field
            var docField = new ObjectField();
            documentField = docField;
            docField.objectType = typeof(WevaDocument);
            docField.style.flexGrow = 1;
            docField.value = targetDocument;
            docField.RegisterValueChangedCallback(evt => {
                var newDoc = evt.newValue as WevaDocument;
                if (newDoc == targetDocument) return;
                DetachDocument();
                targetDocument = newDoc;
                if (targetDocument != null) AttachDocument(targetDocument);
                else ClearSelection();
            });
            toolbar.Add(docField);

            // Refresh button
            var refreshBtn = new Button(() => {
                if (targetDocument != null) RebuildTree();
            }) { text = "⟳" };
            refreshBtn.style.width = 24;
            toolbar.Add(refreshBtn);

            // Pick toggle button (Chrome's element picker — edit + play).
            var pickBtn = new Button(() => SetPickMode(!pickModeActive)) { text = "⊕" };
            pickBtn.tooltip = "Pick an element in the Game view: hover previews, click selects, Esc cancels";
            pickBtn.style.width = 24;
            pickButton = pickBtn;
            toolbar.Add(pickBtn);

            // Search field — single line; flexGrow MUST stay 0 in this column
            // container or the field stretches into a giant empty box and
            // pushes the tree to the bottom (first-cut layout bug).
            searchField = new TextField();
            searchField.style.flexGrow = 0;
            searchField.style.flexShrink = 0;
            searchField.style.marginTop = 2;
            searchField.style.marginBottom = 2;
            (searchField as INotifyValueChanged<string>).RegisterValueChangedCallback(
                _ => RefreshTreeFilter());
            left.Add(searchField);

            // DOM TreeView
            domTreeView = new TreeView();
            domTreeView.style.flexGrow = 1;
            domTreeView.makeItem = MakeTreeItem;
            domTreeView.bindItem = BindTreeItem;
            domTreeView.selectionChanged += OnTreeSelectionChanged;
            domTreeView.style.fontSize = 12;
            left.Add(domTreeView);

            // Vertical separator
            var sep = new VisualElement();
            sep.style.width = 1;
            sep.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
            rootVisualElement.Add(sep);

            // RIGHT pane
            var right = new VisualElement();
            right.style.flexDirection = FlexDirection.Column;
            right.style.flexGrow = 0.6f;
            right.style.flexShrink = 1;
            rootVisualElement.Add(right);

            // Styles section header
            var stylesHeader = new Label("Styles");
            stylesHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            stylesHeader.style.paddingLeft = 6;
            stylesHeader.style.paddingTop = 4;
            stylesHeader.style.paddingBottom = 2;
            stylesHeader.style.backgroundColor = new StyleColor(new Color(0.20f, 0.20f, 0.20f));
            right.Add(stylesHeader);

            stylesScrollView = new ScrollView(ScrollViewMode.Vertical);
            // flexBasis 0 + equal grow = true 50/50 split; with the default
            // auto basis the larger Computed content squeezed Styles to a
            // couple of rows.
            stylesScrollView.style.flexGrow = 1f;
            stylesScrollView.style.flexBasis = 0;
            stylesScrollView.style.flexShrink = 1;
            right.Add(stylesScrollView);

            stylesContainer = new VisualElement();
            stylesContainer.style.paddingLeft = 4;
            stylesContainer.style.paddingRight = 4;
            stylesScrollView.Add(stylesContainer);

            // Horizontal sep between Styles and Computed
            var hSep = new VisualElement();
            hSep.style.height = 1;
            hSep.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
            right.Add(hSep);

            // Computed section
            var computedHeader = new Label("Computed");
            computedHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            computedHeader.style.paddingLeft = 6;
            computedHeader.style.paddingTop = 4;
            computedHeader.style.paddingBottom = 2;
            computedHeader.style.backgroundColor = new StyleColor(new Color(0.20f, 0.20f, 0.20f));
            right.Add(computedHeader);

            computedSearchField = new TextField();
            computedSearchField.style.marginLeft = 4;
            computedSearchField.style.marginRight = 4;
            computedSearchField.style.marginTop = 2;
            (computedSearchField as INotifyValueChanged<string>).RegisterValueChangedCallback(
                _ => RefreshComputedFilter());
            right.Add(computedSearchField);

            computedScrollView = new ScrollView(ScrollViewMode.Vertical);
            computedScrollView.style.flexGrow = 1f;
            computedScrollView.style.flexBasis = 0;
            computedScrollView.style.flexShrink = 1;
            right.Add(computedScrollView);

            computedContainer = new VisualElement();
            computedContainer.style.paddingLeft = 4;
            computedContainer.style.paddingRight = 4;
            computedScrollView.Add(computedContainer);

            // No-document placeholder
            noDocumentLabel = new Label("Pick a WevaDocument above.");
            noDocumentLabel.style.paddingLeft = 8;
            noDocumentLabel.style.paddingTop = 8;
            left.Add(noDocumentLabel);
        }

        // -- TreeView item factory --

        VisualElement MakeTreeItem() {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingLeft = 2;
            row.style.paddingTop = 1;
            row.style.paddingBottom = 1;
            var lbl = new Label();
            lbl.name = "label";
            // Labels hold literal markup like `<a href="#">` — UIToolkit rich
            // text would swallow `<a>`/`<b>`/`<i>` as styling tags and render
            // those rows BLANK. Always literal.
            lbl.enableRichText = false;
            lbl.style.flexGrow = 1;
            lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(lbl);
            return row;
        }

        void BindTreeItem(VisualElement element, int index) {
            var lbl = element.Q<Label>("label");
            if (lbl == null) return;

            var item = GetTreeItemAtIndex(index);
            if (item == null) {
                lbl.text = "";
                return;
            }

            lbl.text = item.Label;

            // Color text: element nodes blue-ish, text nodes gray.
            if (item.IsElement) {
                lbl.style.color = new StyleColor(new Color(0.40f, 0.70f, 1.0f));
            } else {
                lbl.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
            }
        }

        // -- Document attachment --

        void AttachDocument(WevaDocument doc) {
            if (doc == null) return;
            var docState = doc.CurrentState;
            var domDoc = docState?.Doc;
            treeModel.SubscribeTo(domDoc);
            lastKnownDoc = domDoc;
            RebuildTree();
            RegisterHighlight();
            noDocumentLabel.style.display = DisplayStyle.None;
        }

        void DetachDocument() {
            SetPickMode(false);
            treeModel.UnsubscribeFromCurrent();
            lastKnownDoc = null;
            lastTreeVersion = -1;
            ClearSelection();
            UnregisterHighlight();
        }

        void ClearSelection() {
            selectedElement = null;
            selectedBox = null;
            highlight?.ClearTarget();
            stylesContainer?.Clear();
            computedContainer?.Clear();
        }

        // -- Highlight source registration --

        void RegisterHighlight() {
            if (!highlightRegistered && highlight != null) {
                UIPaintSourceRegistry.Register(highlight);
                highlightRegistered = true;
            }
        }

        void UnregisterHighlight() {
            if (highlightRegistered && highlight != null) {
                UIPaintSourceRegistry.Unregister(highlight);
                highlightRegistered = false;
            }
        }

        // -- Editor update pump (~4 Hz) --

        void OnEditorUpdate() {
            double t = EditorApplication.timeSinceStartup;
            if (t - lastRefreshTime < RefreshIntervalSeconds) return;
            lastRefreshTime = t;

            // Retry the auto-pick while unattached: OnEnable runs during the
            // domain-reload boot (e.g. entering Play mode) BEFORE any
            // WevaDocument has enabled, so a one-shot pick misses and the
            // window stays empty until this poll lands one.
            if (targetDocument == null) {
                targetDocument = FindFirstDocument();
                if (targetDocument == null) return;
                documentField?.SetValueWithoutNotify(targetDocument);
                AttachDocument(targetDocument);
                // Re-arm the picker if the user toggled it while detached.
                if (pickModeActive) { ArmPickListener(); ArmGameViewPick(); }
                return;
            }
            // Listeners drop when the GameView is recreated (dock/maximize/
            // play transition) or the document pipeline rebuilds — re-arm.
            EnsurePickArmed();
            var docState = targetDocument.CurrentState;
            var domDoc = docState?.Doc;

            // Detect document identity change (e.g. after Rebuild()).
            if (domDoc != lastKnownDoc) {
                DetachDocument();
                if (domDoc != null) {
                    AttachDocument(targetDocument);
                }
                return;
            }

            // Rebuild tree when dirtied by DOM mutations.
            if (treeModel.IsDirty) {
                RebuildTree();
            }

            Repaint();
        }

        // -- Pick mode (Chrome's element picker) --
        //
        // Chrome semantics: arming the picker turns mouse movement over the
        // page into a LIVE highlight of the hovered element; a click selects
        // it in the Elements tree and exits pick mode; Esc cancels.
        //
        // PLAY mode: input flows through the engine dispatcher, so hover and
        // click use capture-phase PointerMove/PointerDown listeners on the
        // document root — evt.Target IS the element, no coordinate math. The
        // click is CONSUMED (PreventDefault + StopPropagation) so picking
        // never also presses app buttons, matching Chrome. Moves are NOT
        // consumed (Chrome keeps :hover alive while picking).
        //
        // EDIT mode: no input reaches the engine, so we listen on the GAME
        // VIEW editor window itself (TrickleDown PointerMove/Down on its
        // rootVisualElement), map the window point into game pixels via the
        // GameView's own zoom state, and resolve via ElementPicker.PickBox.
        Weva.Events.EventListener pickListener;      // play: click
        Weva.Events.EventListener pickMoveListener;  // play: hover
        Element pickListenerRoot;
        Weva.Events.EventDispatcher pickListenerDispatcher;
        EditorWindow pickGameView;
        EventCallback<PointerDownEvent> pickGameViewCallback;
        EventCallback<PointerMoveEvent> pickGameViewMoveCallback;
        EventCallback<PointerLeaveEvent> pickGameViewLeaveCallback;
        EventCallback<KeyDownEvent> pickGameViewKeyCallback;

        void SetPickMode(bool on) {
            if (on == pickModeActive) return;
            pickModeActive = on;
            if (pickButton != null) {
                pickButton.style.backgroundColor = on
                    ? new StyleColor(new Color(0.18f, 0.38f, 0.64f))
                    : new StyleColor(StyleKeyword.Null);
            }
            if (on) {
                ArmPickListener();
                ArmGameViewPick();
            } else {
                DisarmPickListener();
                DisarmGameViewPick();
                ClearHoverPreview();
            }
        }

        // Re-arm dropped listeners while pick mode is on: the GameView is
        // recreated on dock/maximize/play transitions, and the engine
        // dispatcher is rebuilt with the document. Called from the ~4 Hz
        // OnEditorUpdate poll — cheap no-op when everything is still wired.
        void EnsurePickArmed() {
            if (!pickModeActive) return;
            var docState = targetDocument?.CurrentState;
            if (docState?.Events != null && docState.Events != pickListenerDispatcher) {
                ArmPickListener();
            }
            if (pickGameView == null || pickGameView.rootVisualElement?.panel == null) {
                ArmGameViewPick();
            }
        }

        void ArmPickListener() {
            DisarmPickListener();
            var docState = targetDocument?.CurrentState;
            var dispatcher = docState?.Events;
            var domDoc = docState?.Doc;
            if (dispatcher == null || domDoc == null) return;
            Element root = null;
            foreach (var child in domDoc.Children) {
                if (child is Element el) { root = el; break; }
            }
            if (root == null) return;
            pickListener = OnPickPointerDown;
            pickMoveListener = OnPickPointerMove;
            pickListenerRoot = root;
            pickListenerDispatcher = dispatcher;
            dispatcher.AddEventListener(root, Weva.Events.EventKind.PointerDown, pickListener, useCapture: true);
            dispatcher.AddEventListener(root, Weva.Events.EventKind.PointerMove, pickMoveListener, useCapture: true);
        }

        void DisarmPickListener() {
            if (pickListenerDispatcher != null && pickListenerRoot != null) {
                if (pickListener != null) {
                    pickListenerDispatcher.RemoveEventListener(pickListenerRoot, Weva.Events.EventKind.PointerDown, pickListener, useCapture: true);
                }
                if (pickMoveListener != null) {
                    pickListenerDispatcher.RemoveEventListener(pickListenerRoot, Weva.Events.EventKind.PointerMove, pickMoveListener, useCapture: true);
                }
            }
            pickListener = null;
            pickMoveListener = null;
            pickListenerRoot = null;
            pickListenerDispatcher = null;
        }

        void OnPickPointerDown(Weva.Events.UIEvent evt) {
            var el = evt?.Target;
            if (el == null) return;
            evt.PreventDefault();
            evt.StopPropagation();
            SetPickMode(false);
            SelectElementInTree(el);
        }

        void OnPickPointerMove(Weva.Events.UIEvent evt) {
            if (!pickModeActive) return;
            var el = evt?.Target;
            if (el == null) { ClearHoverPreview(); return; }
            HoverPreview(el, null);
        }

        // -- Hover preview (live highlight while pick mode is armed) --

        void HoverPreview(Element el, Box box) {
            var docState = targetDocument?.CurrentState;
            if (docState == null) return;
            if (box == null && el != null && docState.ElementToBox != null) {
                box = docState.ElementToBox.Lookup(el);
            }
            if (box == null) { ClearHoverPreview(); return; }
            highlight.SetHover(box, docState);
            KickGameViewRepaint();
        }

        void ClearHoverPreview() {
            if (highlight == null || !highlight.HasHover) return;
            highlight.ClearHover();
            KickGameViewRepaint();
        }

        // In edit mode the player loop (and therefore the Game view render)
        // only ticks on demand — without this kick the highlight wouldn't
        // move until something else dirtied the document. Play mode renders
        // every frame already.
        static void KickGameViewRepaint() {
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
        }

        // -- Edit-mode pick: pointer events on the GameView editor window --

        void ArmGameViewPick() {
            DisarmGameViewPick();
            var gv = FindMainGameView();
            if (gv == null || gv.rootVisualElement == null) return;
            pickGameView = gv;
            pickGameViewCallback = OnGameViewPointerDown;
            pickGameViewMoveCallback = OnGameViewPointerMove;
            pickGameViewLeaveCallback = OnGameViewPointerLeave;
            pickGameViewKeyCallback = OnGameViewKeyDown;
            var root = gv.rootVisualElement;
            root.RegisterCallback(pickGameViewCallback, TrickleDown.TrickleDown);
            root.RegisterCallback(pickGameViewMoveCallback, TrickleDown.TrickleDown);
            root.RegisterCallback(pickGameViewLeaveCallback, TrickleDown.TrickleDown);
            root.RegisterCallback(pickGameViewKeyCallback, TrickleDown.TrickleDown);
        }

        void DisarmGameViewPick() {
            if (pickGameView != null && pickGameView.rootVisualElement != null) {
                var root = pickGameView.rootVisualElement;
                if (pickGameViewCallback != null) root.UnregisterCallback(pickGameViewCallback, TrickleDown.TrickleDown);
                if (pickGameViewMoveCallback != null) root.UnregisterCallback(pickGameViewMoveCallback, TrickleDown.TrickleDown);
                if (pickGameViewLeaveCallback != null) root.UnregisterCallback(pickGameViewLeaveCallback, TrickleDown.TrickleDown);
                if (pickGameViewKeyCallback != null) root.UnregisterCallback(pickGameViewKeyCallback, TrickleDown.TrickleDown);
            }
            pickGameView = null;
            pickGameViewCallback = null;
            pickGameViewMoveCallback = null;
            pickGameViewLeaveCallback = null;
            pickGameViewKeyCallback = null;
        }

        // The docked/main Game view. PlayModeView.GetMainPlayModeView() is the
        // editor's own notion of "the" game view (last focused); fall back to
        // the first live GameView instance when the internal API moves.
        static EditorWindow FindMainGameView() {
            var asm = typeof(EditorWindow).Assembly;
            try {
                var pmvType = asm.GetType("UnityEditor.PlayModeView");
                var mi = pmvType?.GetMethod("GetMainPlayModeView",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (mi?.Invoke(null, null) is EditorWindow main && main != null) return main;
            } catch { /* fall through */ }
            var gvType = asm.GetType("UnityEditor.GameView");
            if (gvType == null) return null;
            var windows = Resources.FindObjectsOfTypeAll(gvType);
            if (windows == null) return null;
            for (int i = 0; i < windows.Length; i++) {
                if (windows[i] is EditorWindow w && w != null) return w;
            }
            return null;
        }

        Box PickBoxAt(Vector2 windowPoint) {
            var docState = targetDocument?.CurrentState;
            if (docState?.RootBox == null) return null;
            var gamePoint = WindowToGamePixels(pickGameView, windowPoint, docState);
            if (!gamePoint.HasValue) return null;
            return Weva.DevTools.ElementPicker.PickBox(
                docState.RootBox, gamePoint.Value.x, gamePoint.Value.y);
        }

        void OnGameViewPointerMove(PointerMoveEvent evt) {
            if (!pickModeActive || Application.isPlaying) return;
            var box = PickBoxAt(evt.position);
            if (box?.Element == null) { ClearHoverPreview(); return; }
            HoverPreview(box.Element, box);
        }

        void OnGameViewPointerLeave(PointerLeaveEvent evt) {
            if (!pickModeActive) return;
            ClearHoverPreview();
        }

        void OnGameViewKeyDown(KeyDownEvent evt) {
            if (!pickModeActive) return;
            if (evt.keyCode != KeyCode.Escape) return;
            evt.StopPropagation();
            SetPickMode(false);
        }

        void OnGameViewPointerDown(PointerDownEvent evt) {
            if (!pickModeActive || Application.isPlaying) return;
            var box = PickBoxAt(evt.position);
            var el = box?.Element;
            if (el == null) return;

            evt.StopPropagation();
            SetPickMode(false);
            SelectElementInTree(el);
            KickGameViewRepaint();
            Focus();
        }

        // Maps a point in GameView panel space (points, window top-left
        // origin — what Pointer*Event.position carries for callbacks on the
        // window's rootVisualElement) to document pixels.
        //
        // Primary path mirrors the GameView's OWN input forwarding
        // (UnityCsReference GameView.cs OnGUI):
        //   gameMousePosition = (editorMousePosition + gameMouseOffset) * gameMouseScale
        //   gameMouseOffset   = -viewInWindow.position - targetInView.position
        //   gameMouseScale    = backingScale / m_ZoomArea.scale.y
        // The result is game-render pixels with a TOP-LEFT origin — exactly
        // CSS coordinate space, no Y flip (an earlier cut reflected a
        // `WindowToGameMousePosition` method that no longer exists and
        // flipped Y; both wrong). The two members are private properties on
        // GameView, so the lookup walks the declared hierarchy. Handles user
        // zoom/pan since m_ZoomArea state is baked into offset+scale.
        //
        // Fallback (reflection target moved): letterbox-fit the document
        // viewport into the content area below the toolbar — correct at the
        // default fit zoom only.
        static Vector2? WindowToGamePixels(EditorWindow gv, Vector2 windowPoint,
                                           Weva.Documents.UIDocumentState docState) {
            if (gv == null) return null;
            var ctx = docState.LayoutContext;
            if (ctx == null || ctx.ViewportWidthPx <= 0 || ctx.ViewportHeightPx <= 0) return null;
            float targetW = (float)ctx.ViewportWidthPx;
            float targetH = (float)ctx.ViewportHeightPx;

            try {
                object offObj = GetHierarchyProperty(gv, "gameMouseOffset");
                object scaleObj = GetHierarchyProperty(gv, "gameMouseScale");
                if (offObj is Vector2 off && scaleObj is float scale &&
                    scale > 0f && !float.IsNaN(scale) && !float.IsInfinity(scale)) {
                    var p = (windowPoint + off) * scale;
                    if (p.x < 0 || p.y < 0 || p.x >= targetW || p.y >= targetH) return null;
                    return p;
                }
            } catch { /* fall through to letterbox mapping */ }

            // Letterbox fallback.
            const float toolbarH = 21f;
            var layout = gv.rootVisualElement.layout;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            float contentWpx = layout.width * ppp;
            float contentHpx = (layout.height - toolbarH) * ppp;
            if (contentWpx <= 0 || contentHpx <= 0) return null;
            float scaleFit = Mathf.Min(contentWpx / targetW, contentHpx / targetH);
            if (scaleFit <= 0) return null;
            float drawnW = targetW * scaleFit;
            float drawnH = targetH * scaleFit;
            float offX = (contentWpx - drawnW) * 0.5f;
            float offY = (contentHpx - drawnH) * 0.5f;
            float px = (windowPoint.x * ppp - offX) / scaleFit;
            float py = ((windowPoint.y - toolbarH) * ppp - offY) / scaleFit;
            if (px < 0 || py < 0 || px > targetW || py > targetH) return null;
            return new Vector2(px, py);
        }

        // Instance property lookup that also finds PRIVATE properties
        // declared on base types (Type.GetProperty alone skips those).
        static object GetHierarchyProperty(object obj, string name) {
            for (var t = obj.GetType(); t != null; t = t.BaseType) {
                var pi = t.GetProperty(name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.DeclaredOnly);
                if (pi != null) return pi.GetValue(obj);
            }
            return null;
        }

        // Selects the element's node in the TreeView (firing the normal
        // selection flow: highlight + styles + computed) and scrolls to it.
        void SelectElementInTree(Element el) {
            var node = treeModel?.FindNode(el);
            if (node == null) { RebuildTree(); node = treeModel?.FindNode(el); }
            if (node == null || domTreeView == null) return;
            domTreeView.SetSelectionById(node.Id);
            domTreeView.ScrollToItemById(node.Id);
        }

        // -- Tree rebuild --

        void RebuildTree() {
            if (targetDocument == null) return;
            var docState = targetDocument.CurrentState;
            var domDoc = docState?.Doc;

            treeModel.Rebuild(domDoc);
            lastTreeVersion = treeModel.Version;

            RefreshTreeFilter();
        }

        // Backing list for the TreeView (Unity 6 TreeView uses int IDs + TreeViewItemData<T>).
        List<TreeViewItemData<DomTreeNode>> treeItems = new List<TreeViewItemData<DomTreeNode>>();
        // Flat list matching the expanded tree view (for index-based bindItem lookup).
        List<DomTreeNode> flatVisible = new List<DomTreeNode>();

        void RefreshTreeFilter() {
            if (domTreeView == null) return;

            string filter = searchField?.value ?? "";
            bool hasFilter = !string.IsNullOrEmpty(filter);

            var nodes = treeModel.Nodes;
            treeItems.Clear();
            flatVisible.Clear();

            // Build hierarchical TreeViewItemData<DomTreeNode> tree.
            // Unity 6 TreeView expects a List<TreeViewItemData<T>> at the root level;
            // each item can have children.
            var roots = BuildTreeViewItems(nodes, 0, 0, filter);
            treeItems.AddRange(roots);

            // Build flat visible list for bindItem index lookup.
            FlattenVisible(roots, flatVisible);

            domTreeView.SetRootItems(treeItems);
            domTreeView.Rebuild();
            // Chrome opens with the document expanded — a collapsed lone
            // <html> row reads as an empty panel.
            domTreeView.ExpandAll();
        }

        List<TreeViewItemData<DomTreeNode>> BuildTreeViewItems(
            IReadOnlyList<DomTreeNode> nodes, int depth, int parentId, string filter) {

            var result = new List<TreeViewItemData<DomTreeNode>>();
            foreach (var node in nodes) {
                if (node.Depth != depth || node.ParentId != parentId) continue;

                bool matchesFilter = string.IsNullOrEmpty(filter) ||
                    node.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                var children = BuildTreeViewItems(nodes, depth + 1, node.Id, filter);

                // Include node if it matches OR has matching descendants.
                if (matchesFilter || children.Count > 0) {
                    var item = new TreeViewItemData<DomTreeNode>(node.Id, node, children);
                    result.Add(item);
                }
            }
            return result;
        }

        void FlattenVisible(IEnumerable<TreeViewItemData<DomTreeNode>> items,
                            List<DomTreeNode> output) {
            foreach (var item in items) {
                output.Add(item.data);
                if (item.hasChildren) {
                    FlattenVisible(item.children, output);
                }
            }
        }

        DomTreeNode GetTreeItemAtIndex(int index) {
            if (index < 0 || index >= flatVisible.Count) return null;
            return flatVisible[index];
        }

        // -- Tree selection --

        void OnTreeSelectionChanged(IEnumerable<object> selectedItems) {
            selectedElement = null;
            selectedBox = null;

            foreach (var obj in selectedItems) {
                if (obj is DomTreeNode node && node.IsElement) {
                    selectedElement = node.Element;
                    break;
                }
            }

            if (selectedElement == null) {
                highlight.ClearTarget();
                KickGameViewRepaint();
                stylesContainer.Clear();
                computedContainer.Clear();
                return;
            }

            // Resolve box.
            var docState = targetDocument?.CurrentState;
            if (docState?.ElementToBox != null) {
                selectedBox = docState.ElementToBox.Lookup(selectedElement);
            }

            // Update highlight. The kick matters in edit mode: the player
            // loop only ticks on demand, so without it the new selection's
            // overlay wouldn't appear until something else repainted.
            highlight.SetTarget(selectedBox, docState);
            KickGameViewRepaint();

            // Update styles panel.
            RefreshStylesPanel();

            // Update computed panel.
            RefreshComputedPanel();

            // Serialize selection path for domain-reload restore.
            SerializeSelectionPath();
        }

        // -- Styles panel --

        void RefreshStylesPanel() {
            stylesContainer.Clear();
            if (selectedElement == null || targetDocument == null) return;

            var docState = targetDocument.CurrentState;
            var cascade = docState?.Cascade;
            var state = docState?.State;

            List<RuleBlock> blocks;
            StyleInspector.CaptureCascadeTrace = true;
            try {
                blocks = RuleBlockBuilder.Build(selectedElement, cascade, state);
            } finally {
                StyleInspector.CaptureCascadeTrace = false;
            }

            foreach (var block in blocks) {
                AddRuleBlockUI(block);
            }
        }

        void AddRuleBlockUI(RuleBlock block) {
            // Selector line
            var selectorRow = new VisualElement();
            selectorRow.style.flexDirection = FlexDirection.Row;
            selectorRow.style.marginTop = 6;

            var selectorLbl = new Label(block.SelectorText) { enableRichText = false };
            selectorLbl.style.color = new StyleColor(block.IsInlineStyle
                ? new Color(0.70f, 0.70f, 0.70f)
                : new Color(0.35f, 0.65f, 1.0f)); // blue-ish for selectors
            selectorLbl.style.flexGrow = 1;
            selectorRow.Add(selectorLbl);

            var originLbl = new Label(block.OriginLabel);
            originLbl.style.color = new StyleColor(new Color(0.50f, 0.50f, 0.50f));
            originLbl.style.unityTextAlign = TextAnchor.MiddleRight;
            selectorRow.Add(originLbl);

            stylesContainer.Add(selectorRow);

            // Declaration rows
            foreach (var decl in block.Declarations) {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.paddingLeft = 16;

                string displayText;
                if (decl.IsOverridden) {
                    // Fallback for UIToolkit which does NOT support text-decoration:line-through
                    // via USS in all Unity versions. Render overridden declarations in dim gray
                    // with a leading '~' sigil (documented in manual test script below).
                    displayText = "~  " + decl.Property + ": " + decl.ValueText
                        + (decl.Important ? " !important" : "") + ";";
                } else {
                    displayText = decl.Property + ": " + decl.ValueText
                        + (decl.Important ? " !important" : "") + ";";
                }

                var declLbl = new Label(displayText) { enableRichText = false };
                declLbl.style.color = new StyleColor(decl.IsOverridden
                    ? new Color(0.45f, 0.45f, 0.45f)
                    : new Color(0.90f, 0.90f, 0.90f));
                declLbl.style.flexGrow = 1;
                declLbl.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.Add(declLbl);

                stylesContainer.Add(row);
            }
        }

        // -- Computed panel --

        void RefreshComputedPanel() {
            if (selectedElement == null || targetDocument == null) {
                computedContainer.Clear();
                return;
            }

            var docState = targetDocument.CurrentState;
            var cascade = docState?.Cascade;
            var state = docState?.State;

            ComputedStyle style = null;
            if (cascade != null) {
                style = cascade.GetComposedStyle(selectedElement, state);
            }

            computedModel.Build(style, selectedBox);
            RefreshComputedFilter();
        }

        void RefreshComputedFilter() {
            computedContainer.Clear();

            // Box-model diagram (nested VisualElements).
            AddBoxModelDiagram();

            // Filtered property list
            string filter = computedSearchField?.value ?? "";
            var entries = computedModel.Filter(filter);
            foreach (var e in entries) {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.paddingTop = 1;
                row.style.paddingBottom = 1;

                var propLbl = new Label(e.Property) { enableRichText = false };
                propLbl.style.color = new StyleColor(new Color(0.50f, 0.75f, 1.0f));
                propLbl.style.width = 160;
                propLbl.style.flexShrink = 0;
                row.Add(propLbl);

                var valLbl = new Label(e.Value) { enableRichText = false };
                valLbl.style.color = new StyleColor(new Color(0.90f, 0.90f, 0.90f));
                valLbl.style.flexGrow = 1;
                row.Add(valLbl);

                computedContainer.Add(row);
            }
        }

        // -- Box-model diagram --

        void AddBoxModelDiagram() {
            var bm = computedModel.BoxModel;

            // Chrome-style nested-box diagram. We build 4 nested VisualElements.
            // Outer = margin (orange), then border (yellow), padding (green), content (blue).
            // Each layer shows its measurement numbers on the four sides.
            //
            // Colors (same palette as SelectionHighlightSource, gamma-space for IMGUI/UIToolkit):
            var marginBg  = new Color(0.96f, 0.70f, 0.42f, 0.40f);
            var borderBg  = new Color(1.00f, 0.90f, 0.60f, 0.40f);
            var paddingBg = new Color(0.58f, 0.77f, 0.49f, 0.40f);
            var contentBg = new Color(0.44f, 0.66f, 0.86f, 0.40f);

            // Outer container
            var diagram = new VisualElement();
            diagram.style.marginTop = 8;
            diagram.style.marginBottom = 8;
            diagram.style.alignSelf = Align.Center;

            // Margin box
            var marginBox = MakeDiagramBox("margin", marginBg,
                (float)bm.MarginW, (float)bm.MarginH,
                (float)(bm.BorderY - bm.MarginY),
                (float)(bm.MarginX + bm.MarginW - bm.BorderX - bm.BorderW),
                (float)(bm.MarginY + bm.MarginH - bm.BorderY - bm.BorderH),
                (float)(bm.BorderX - bm.MarginX));

            // Border box
            var borderBox = MakeDiagramBox("border", borderBg,
                (float)bm.BorderW, (float)bm.BorderH,
                (float)(bm.PaddingY - bm.BorderY),
                (float)(bm.BorderX + bm.BorderW - bm.PaddingX - bm.PaddingW),
                (float)(bm.BorderY + bm.BorderH - bm.PaddingY - bm.PaddingH),
                (float)(bm.PaddingX - bm.BorderX));

            // Padding box
            var paddingBox = MakeDiagramBox("padding", paddingBg,
                (float)bm.PaddingW, (float)bm.PaddingH,
                (float)(bm.ContentY - bm.PaddingY),
                (float)(bm.PaddingX + bm.PaddingW - bm.ContentX - bm.ContentW),
                (float)(bm.PaddingY + bm.PaddingH - bm.ContentY - bm.ContentH),
                (float)(bm.ContentX - bm.PaddingX));

            // Content box (innermost)
            var contentBox = new VisualElement();
            contentBox.style.backgroundColor = new StyleColor(contentBg);
            contentBox.style.minWidth = 80;
            contentBox.style.minHeight = 40;
            contentBox.style.alignItems = Align.Center;
            contentBox.style.justifyContent = Justify.Center;

            var contentSizeLbl = new Label(
                string.Format("{0:F0} × {1:F0}", bm.ContentW, bm.ContentH));
            contentSizeLbl.style.fontSize = 10;
            contentSizeLbl.style.color = new StyleColor(new Color(0.90f, 0.90f, 0.90f));
            contentBox.Add(contentSizeLbl);

            // Nest: content inside padding inside border inside margin
            paddingBox.Add(contentBox);
            borderBox.Add(paddingBox);
            marginBox.Add(borderBox);
            diagram.Add(marginBox);

            computedContainer.Add(diagram);
        }

        // Build a single "band" of the box-model diagram as a padded VisualElement.
        // The label shows the layer name + offsets. The four pad values are
        // the insets from this box's outer edge to the next inner box.
        VisualElement MakeDiagramBox(string layerName, Color bg,
                                     float w, float h,
                                     float topInset, float rightInset,
                                     float bottomInset, float leftInset) {
            var box = new VisualElement();
            box.style.backgroundColor = new StyleColor(bg);
            // Cap diagram width to 260px so it fits in the right pane without scrolling.
            box.style.minWidth = Math.Min(260, Math.Max(80, w * 0.3f));
            box.style.minHeight = Math.Max(24, h * 0.15f);
            box.style.paddingTop    = Math.Max(0, topInset    > 0 ? 14 : 4);
            box.style.paddingRight  = Math.Max(0, rightInset  > 0 ? 14 : 4);
            box.style.paddingBottom = Math.Max(0, bottomInset > 0 ? 14 : 4);
            box.style.paddingLeft   = Math.Max(0, leftInset   > 0 ? 14 : 4);
            box.style.alignItems = Align.Center;
            box.style.position = Position.Relative;

            // Layer label top-left
            var nameLbl = new Label(layerName);
            nameLbl.style.position = Position.Absolute;
            nameLbl.style.top = 2;
            nameLbl.style.left = 4;
            nameLbl.style.fontSize = 9;
            nameLbl.style.color = new StyleColor(new Color(0.80f, 0.80f, 0.80f));
            box.Add(nameLbl);

            // Top inset number
            if (topInset > 0.01f) {
                var lbl = new Label(string.Format("{0:F0}", topInset));
                lbl.style.position = Position.Absolute;
                lbl.style.top = 2;
                lbl.style.alignSelf = Align.Center;
                lbl.style.left = Length.Percent(50);
                lbl.style.fontSize = 9;
                lbl.style.color = new StyleColor(new Color(0.85f, 0.85f, 0.85f));
                box.Add(lbl);
            }

            return box;
        }

        // -- Selection path serialization (domain reload restore) --

        void SerializeSelectionPath() {
            if (selectedElement == null || targetDocument == null) {
                selectionPath = null;
                return;
            }
            // Build index path from the node's flat list index chain.
            var node = treeModel.FindNode(selectedElement);
            if (node == null) { selectionPath = null; return; }
            // Simple: store the node Id as a one-element path.
            selectionPath = new int[] { node.Id };
        }

        // -- Helpers --

        static WevaDocument FindFirstDocument() {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindAnyObjectByType<WevaDocument>();
#else
            return UnityEngine.Object.FindObjectOfType<WevaDocument>();
#endif
        }
    }
}
