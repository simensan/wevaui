// The Native Elements window opens a document hosted by the core: the
// window builds, attaches to a WevaDocument in the scene and lists its
// tree. The plan's Phase 3 step 3 gate in its smallest form.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Weva.EditorTools.DevTools;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeElementsWindowTests
    {
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
