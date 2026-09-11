// An Elements panel for a document hosted by the core (WevaNativeDocument):
// the tree on the left, the selected element's rules (winners first, losing
// declarations struck through), computed style and box model on the right,
// all read through the C ABI's inspector surface via NativeInspectorModel.
// Kept lean on purpose: it exists to show the editor tooling opening a
// document through the core, which is the plan's Phase 3 step 3 gate.
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Weva.Native;

namespace Weva.EditorTools.DevTools
{
    public sealed class NativeElementsWindow : EditorWindow
    {
        private WevaNativeDocument _target;
        private NativeInspectorModel _model;
        private TreeView _tree;
        private ScrollView _rules;
        private ScrollView _computed;
        private Label _box;
        private Label _status;
        private TextField _filter;
        private double _nextPoll;

        [MenuItem("Window/Weva/Native Elements", priority = 202)]
        public static void Open()
        {
            GetWindow<NativeElementsWindow>("Native Elements");
        }

        private void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            var toolbar = new Toolbar();
            var picker = new ObjectField("Document") { objectType = typeof(WevaNativeDocument), allowSceneObjects = true, value = _target };
            picker.RegisterValueChangedCallback(evt => Attach(evt.newValue as WevaNativeDocument));
            picker.style.minWidth = 260;
            toolbar.Add(picker);
            toolbar.Add(new ToolbarButton(Rebuild) { text = "Refresh" });
            var search = new ToolbarSearchField();
            search.RegisterValueChangedCallback(evt => SearchTree(evt.newValue));
            toolbar.Add(search);
            _status = new Label("no document");
            _status.style.marginLeft = 8;
            toolbar.Add(_status);
            root.Add(toolbar);

            var split = new TwoPaneSplitView(0, 320, TwoPaneSplitViewOrientation.Horizontal);
            _tree = new TreeView { fixedItemHeight = 18 };
            _tree.makeItem = () => new Label();
            _tree.bindItem = (element, index) =>
            {
                NativeInspectorModel.Node node = _tree.GetItemDataForIndex<NativeInspectorModel.Node>(index);
                ((Label)element).text = node.Label;
            };
            _tree.selectionChanged += items =>
            {
                foreach (object item in items)
                {
                    if (item is NativeInspectorModel.Node node) Select(node.Element);
                    break;
                }
            };
            split.Add(_tree);

            var right = new ScrollView();
            right.Add(new Label("Styles") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4 } });
            _rules = new ScrollView { style = { maxHeight = 320 } };
            right.Add(_rules);
            right.Add(new Label("Box model") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } });
            _box = new Label("");
            right.Add(_box);
            right.Add(new Label("Computed") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8 } });
            _filter = new TextField("Filter");
            _filter.RegisterValueChangedCallback(_ => RenderComputed());
            right.Add(_filter);
            _computed = new ScrollView { style = { maxHeight = 400 } };
            right.Add(_computed);
            split.Add(right);
            root.Add(split);

            if (_target == null) _target = FindFirstObjectByType<WevaNativeDocument>();
            picker.SetValueWithoutNotify(_target);
            Attach(_target);
        }

        private void Attach(WevaNativeDocument target)
        {
            _target = target;
            _model = target != null && target.Document != null ? new NativeInspectorModel(target.Document) : null;
            Rebuild();
        }

        private void Rebuild()
        {
            if (_model == null || _target == null || _target.Document == null)
            {
                _tree.SetRootItems(new List<TreeViewItemData<NativeInspectorModel.Node>>());
                _tree.Rebuild();
                _status.text = "no document";
                return;
            }
            _model.Rebuild();
            var items = new List<TreeViewItemData<NativeInspectorModel.Node>>();
            int nextId = 0;
            if (_model.Root != null) items.Add(ToItem(_model.Root, ref nextId));
            _tree.SetRootItems(items);
            _tree.Rebuild();
            _tree.ExpandAll();
            _status.text = _model.NodeCount + " elements, ABI " + NativeDocument.AbiVersion().Major + "." + NativeDocument.AbiVersion().Minor;
            RenderSelection();
        }

        private static TreeViewItemData<NativeInspectorModel.Node> ToItem(NativeInspectorModel.Node node, ref int nextId)
        {
            int id = nextId++;
            var children = new List<TreeViewItemData<NativeInspectorModel.Node>>();
            foreach (NativeInspectorModel.Node child in node.Children) children.Add(ToItem(child, ref nextId));
            return new TreeViewItemData<NativeInspectorModel.Node>(id, node, children);
        }

        private void SearchTree(string text)
        {
            if (_model == null) return;
            List<NativeInspectorModel.Node> hits = _model.Search(text);
            if (hits.Count > 0) Select(hits[0].Element);
        }

        private void Select(uint element)
        {
            if (_model == null) return;
            _model.Select(element);
            RenderSelection();
        }

        private void RenderSelection()
        {
            _rules.Clear();
            if (_model == null || _model.Selected == WevaNative.WEVA_ELEMENT_NONE)
            {
                _box.text = "";
                RenderComputed();
                return;
            }
            foreach (NativeInspectorModel.RuleBlock block in _model.Rules)
            {
                string header = block.Inline ? "element.style" : block.Selector;
                header += "   (" + block.Origin + (block.Layer.Length > 0 ? ", layer " + block.Layer : "") + (block.Specificity.Length > 0 ? ", " + block.Specificity : "") + ")";
                var fold = new Foldout { text = header, value = true };
                foreach (NativeDocument.MatchedRule d in block.Declarations)
                {
                    var line = new Label("  " + d.Property + ": " + d.Value + (d.Important ? " !important" : "") + ";");
                    if (!d.Applied)
                    {
                        line.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
                        line.text = "  ✗" + line.text.Substring(2);
                    }
                    fold.Add(line);
                }
                _rules.Add(fold);
            }
            _box.text = _model.HasBox
                ? $"border box {_model.Bounds.X:0.##},{_model.Bounds.Y:0.##} {_model.Bounds.Width:0.##}×{_model.Bounds.Height:0.##}\n" +
                  $"margin {_model.Box.MarginTop:0.##} {_model.Box.MarginRight:0.##} {_model.Box.MarginBottom:0.##} {_model.Box.MarginLeft:0.##}\n" +
                  $"border {_model.Box.BorderTop:0.##} {_model.Box.BorderRight:0.##} {_model.Box.BorderBottom:0.##} {_model.Box.BorderLeft:0.##}\n" +
                  $"padding {_model.Box.PaddingTop:0.##} {_model.Box.PaddingRight:0.##} {_model.Box.PaddingBottom:0.##} {_model.Box.PaddingLeft:0.##}\n" +
                  $"content {_model.Box.ContentX:0.##},{_model.Box.ContentY:0.##} {_model.Box.ContentWidth:0.##}×{_model.Box.ContentHeight:0.##}"
                : "no box (display: none or not laid out)";
            RenderComputed();
        }

        private void RenderComputed()
        {
            _computed.Clear();
            if (_model == null) return;
            string filter = _filter != null ? _filter.value : "";
            foreach (KeyValuePair<string, string> kv in _model.Computed)
            {
                if (!string.IsNullOrEmpty(filter) && kv.Key.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                _computed.Add(new Label(kv.Key + ": " + kv.Value));
            }
        }

        private void Update()
        {
            // The document moves on its own (animations, input); re-read the
            // selection a few times a second the way the Elements window does.
            if (_model == null || _target == null || _target.Document == null) return;
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 0.25;
            if (_model.Selected != WevaNative.WEVA_ELEMENT_NONE)
            {
                _model.Select(_model.Selected);
                RenderSelection();
            }
        }
    }
}
