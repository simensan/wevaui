// The Native Elements window opens a document hosted by the core: the
// window builds, attaches to a WevaDocument in the scene and lists its
// tree. The plan's Phase 3 step 3 gate in its smallest form.
using NUnit.Framework;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Weva.EditorTools.DevTools;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeElementsWindowTests
    {
        private static void Call(NativeElementsWindow window, string method)
        {
            typeof(NativeElementsWindow).GetField("_nextPoll", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, 0.0);
            typeof(NativeElementsWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Window_ReconnectsAfterHostReenable(bool manualRefresh)
        {
            var go = new GameObject("native-doc-reconnect");
            NativeElementsWindow window = null;
            try
            {
                var host = go.AddComponent<WevaDocument>();
                host.AutoInput = false;
                host.InlineHtml = "<div id=old>old</div>";
                host.Reload();
                window = EditorWindow.GetWindow<NativeElementsWindow>();
                window.rootVisualElement.Q<ObjectField>().value = host;
                TreeView tree = window.rootVisualElement.Q<TreeView>();
                Assert.That(tree.GetTreeCount(), Is.EqualTo(4));

                host.enabled = false;
                Call(window, manualRefresh ? "Rebuild" : "Update");
                Assert.That(tree.GetTreeCount(), Is.Zero, "disabled document clears the tree");
                host.InlineHtml = "<section><p>first</p><p>second</p></section>";
                host.enabled = true;
                Call(window, manualRefresh ? "Rebuild" : "Update");
                Assert.That(tree.GetTreeCount(), Is.EqualTo(6), "the recreated native document is inspected");
            }
            finally
            {
                if (window != null) window.Close();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Window_UpdatesTreeAfterMarkupReload()
        {
            var go = new GameObject("native-doc-markup-reload");
            NativeElementsWindow window = null;
            try
            {
                var host = go.AddComponent<WevaDocument>();
                host.AutoInput = false;
                host.InlineHtml = "<div id=old>old</div>";
                host.Reload();
                window = EditorWindow.GetWindow<NativeElementsWindow>();
                window.rootVisualElement.Q<ObjectField>().value = host;
                TreeView tree = window.rootVisualElement.Q<TreeView>();
                uint kept = host.Document.Query("#old");
                tree.SetSelectionById((int)kept);
                host.Document.ReloadHtml("<section></section><div id=old><p>first</p><p>second</p></div>");
                host.Document.Update(0);
                Call(window, "Update");
                Assert.That(tree.GetTreeCount(), Is.EqualTo(7));
                Assert.That((tree.selectedItem as NativeInspectorModel.Node)?.Element, Is.EqualTo(kept), "selection follows the retained element");
            }
            finally
            {
                if (window != null) window.Close();
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Window_OpensADocumentHostedByTheCore()
        {
            var go = new GameObject("native-doc-for-window");
            NativeElementsWindow window = null;
            try
            {
                var host = go.AddComponent<WevaDocument>();
                host.AutoInput = false;
                host.InlineHtml = "<body><div id=\"panel\" class=\"hud\"><p id=\"line\">Hosted by the core</p></div></body>";
                host.InlineCss = "#panel{padding:8px}.hud{color:#fff}";
                host.Reload();
                Assume.That(host.Document, Is.Not.Null, host.LastError);

                window = EditorWindow.GetWindow<NativeElementsWindow>();
                Assert.That(window, Is.Not.Null);
                Assert.That(window.rootVisualElement.childCount, Is.GreaterThan(0), "the window built its UI");
                TreeView tree = window.rootVisualElement.Q<TreeView>();
                Assert.That(tree, Is.Not.Null);
                Assert.That(tree.GetTreeCount(), Is.GreaterThanOrEqualTo(4), "html, body, #panel, #line are listed: " + tree.GetTreeCount());
                Assert.That(window.rootVisualElement.Q<Label>(), Is.Not.Null, "the status label is on the toolbar");
            }
            finally
            {
                if (window != null) window.Close();
                Object.DestroyImmediate(go);
            }
        }
    }
}
