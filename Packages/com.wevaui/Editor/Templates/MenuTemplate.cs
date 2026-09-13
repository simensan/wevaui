using UnityEditor;
using UnityEngine;
using Weva;

namespace Weva.EditorTools.Templates {
    public static class MenuTemplate {
        [MenuItem("GameObject/Weva/New WevaDocument", false, priority = 10)]
        public static void CreateUIDocument(MenuCommand menuCommand) {
            var go = new GameObject("WevaDocument");
            go.AddComponent<WevaDocument>();
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create WevaDocument");
            Selection.activeObject = go;
        }
    }
}
