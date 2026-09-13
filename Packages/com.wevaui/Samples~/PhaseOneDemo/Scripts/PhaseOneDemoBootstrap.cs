#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Weva;

// Editor-only convenience that spawns the demo scene programmatically. The
// shipped Scenes/PhaseOneDemo.unity file is a minimal hand-authored scene;
// this menu item is the fallback path for users who prefer to build the
// hierarchy directly into their own scene, and a self-test that confirms the
// pipeline wiring.
//
// Why programmatic rather than committing only YAML: scene .unity files
// reference scripts and assets by GUID. The GUIDs in this sample's .meta files
// must match the user's local AssetDatabase entries, which only happens after
// they import the sample. A code path that builds the GameObject hierarchy
// from scratch sidesteps GUID drift entirely.
public static class PhaseOneDemoBootstrap {
    [MenuItem("GameObject/Weva/Phase One Demo", priority = 30)]
    public static void CreateInActiveScene() {
        var go = new GameObject("DemoUI");
        Undo.RegisterCreatedObjectUndo(go, "Create Phase One Demo");

        var doc = go.AddComponent<WevaDocument>();
        var controller = go.AddComponent<PhaseOneDemoController>();

        var html = LoadAssetAtRelative<TextAsset>("UI/menu.html");
        var css = LoadAssetAtRelative<TextAsset>("UI/menu.css");
        if (html != null) doc.DocumentAsset = html;
        if (css != null) doc.StylesheetAssets = new[] { css };

        EnsureCamera();

        Selection.activeGameObject = go;
        EditorSceneManager.MarkSceneDirty(go.scene);
    }

    static void EnsureCamera() {
        if (Camera.main != null) return;
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
        camGo.AddComponent<AudioListener>();
    }

    static T LoadAssetAtRelative<T>(string relativePath) where T : Object {
        // Once the sample is imported via Package Manager UI, asset paths look
        // like Assets/Samples/Weva/<version>/PhaseOneDemo/<relativePath>.
        // We probe the well-known import root and the in-package Samples~
        // directory (rare, but supported when the user opens the package as
        // an embedded package).
        string[] candidates = {
            "Assets/Samples/Weva/" + relativePath,
            "Packages/com.wevaui/Samples~/PhaseOneDemo/" + relativePath
        };
        foreach (var probe in candidates) {
            // Direct probe.
            var asset = AssetDatabase.LoadAssetAtPath<T>(probe);
            if (asset != null) return asset;
            // Search for files that match the leaf name underneath
            // Assets/Samples/Weva to handle versioned subfolders.
        }
        var leaf = Path.GetFileName(relativePath);
        var guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(leaf), new[] { "Assets/Samples" });
        foreach (var g in guids) {
            var p = AssetDatabase.GUIDToAssetPath(g);
            if (p.EndsWith(relativePath, System.StringComparison.OrdinalIgnoreCase)) {
                var asset = AssetDatabase.LoadAssetAtPath<T>(p);
                if (asset != null) return asset;
            }
        }
        return null;
    }
}
#endif
