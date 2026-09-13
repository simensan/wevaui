using UnityEditor;
using UnityEngine;
using Weva;
using Weva.Rendering;

namespace Weva.EditorTools.Templates {
    public static class MenuTemplate {
        [MenuItem("GameObject/Weva/New WevaLegacyDocument", false, priority = 10)]
        public static void CreateUIDocument(MenuCommand menuCommand) {
            var go = new GameObject("WevaLegacyDocument");
            go.AddComponent<WevaLegacyDocument>();
            go.AddComponent<IMGUIDocumentRenderer>();
            // Devtools overlay opt-in: most users will want it for the first
            // few sessions, and the F12 toggle keeps it out of the way.
            go.AddComponent<Weva.DevTools.DevToolsOverlay>();
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create WevaLegacyDocument");
            Selection.activeObject = go;
        }
    }
}
