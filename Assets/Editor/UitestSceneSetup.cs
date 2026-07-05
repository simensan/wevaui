using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
// Dev-project-only tool (lives in Assets/Editor, not the package): the
// WEVA_URP guards are dropped because the dev project always has URP —
// Assembly-CSharp-Editor never gets the asmdef version-define anyway.
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Weva.Rendering;

namespace Weva.EditorTools.Setup {
    public static class UitestSceneSetup {
        const string ScenePath = "Assets/uitest.unity";
        const string MenuHtmlPath = "Assets/UI/menu.html";
        const string MenuCssPath = "Assets/UI/menu.css";
        const string ControllerTypeName = "UitestController";

        const string SettingsFolder = "Assets/Settings";
        const string PipelineAssetPath = "Assets/Settings/Weva_URPAsset.asset";
        const string RendererAssetPath = "Assets/Settings/Weva_Renderer.asset";

        [MenuItem("Tools/Weva Dev/Set Up uitest Scene", priority = 100)]
        public static void Run() {
            if (!File.Exists(MenuHtmlPath) || !File.Exists(MenuCssPath)) {
                Debug.LogError($"Weva: missing {MenuHtmlPath} or {MenuCssPath}. Cannot set up scene.");
                return;
            }

            ConfigureInput();
            ConfigureUrp();

            var html = AssetDatabase.LoadAssetAtPath<TextAsset>(MenuHtmlPath);
            var css  = AssetDatabase.LoadAssetAtPath<TextAsset>(MenuCssPath);
            if (html == null || css == null) {
                AssetDatabase.Refresh();
                html = AssetDatabase.LoadAssetAtPath<TextAsset>(MenuHtmlPath);
                css  = AssetDatabase.LoadAssetAtPath<TextAsset>(MenuCssPath);
            }
            if (html == null || css == null) {
                Debug.LogError("Weva: TextAsset import failed for menu.html / menu.css. Check the Console for import errors.");
                return;
            }

            var scene = SceneManager.GetActiveScene().path == ScenePath
                ? SceneManager.GetActiveScene()
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            ClearSceneRoots(scene);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.10f, 0.11f, 1f);
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.AddComponent<AudioListener>();

            var uiGo = new GameObject("DemoUI");
            var doc = uiGo.AddComponent<WevaDocument>();
            uiGo.AddComponent<Weva.Rendering.IMGUIDocumentRenderer>();
            // Pointer + keyboard bridge: routes Input System events into the
            // EventDispatcher so :hover / :focus / :active / click fire from
            // real input. WevaDocument.OnEnable also adds this lazily, but
            // adding it at scene-setup time gives the user a visible
            // component to inspect.
            uiGo.AddComponent<Weva.Forms.Bridge.UnityInputController>();

            var controllerType = ResolveType(ControllerTypeName);
            if (controllerType != null) {
                uiGo.AddComponent(controllerType);
            } else {
                Debug.LogWarning("Weva: UitestController script not found. The scene was set up but no controller is attached.");
            }

            var docSO = new SerializedObject(doc);
            docSO.FindProperty("documentAsset").objectReferenceValue = html;
            var arr = docSO.FindProperty("stylesheetAssets");
            arr.arraySize = 1;
            arr.GetArrayElementAtIndex(0).objectReferenceValue = css;
            docSO.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log("Weva: uitest scene set up. Press Play.");
        }

        static void ClearSceneRoots(Scene scene) {
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) {
                Object.DestroyImmediate(roots[i]);
            }
        }

        static System.Type ResolveType(string name) {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies()) {
                var t = asm.GetType(name, throwOnError: false);
                if (t != null) return t;
                foreach (var candidate in asm.GetTypes()) {
                    if (candidate.Name == name && typeof(MonoBehaviour).IsAssignableFrom(candidate)) {
                        return candidate;
                    }
                }
            }
            return null;
        }

        static void ConfigureInput() {
#if UNITY_EDITOR
            const string SettingName = "activeInputHandler";
            var ps = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/ProjectSettings.asset");
            if (ps == null) return;
            var so = new SerializedObject(ps);
            var prop = so.FindProperty(SettingName);
            if (prop != null && prop.intValue != 2) {
                prop.intValue = 2; // Both: Old + New Input
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("Weva: set Player.activeInputHandler = Both. Editor will prompt to restart.");
            }
#endif
        }

        static void ConfigureUrp() {
            if (!AssetDatabase.IsValidFolder(SettingsFolder)) {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            var rendererAsset = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAssetPath);
            if (rendererAsset == null) {
                rendererAsset = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererAsset, RendererAssetPath);
            }

            bool hasFeature = false;
            if (rendererAsset.rendererFeatures != null) {
                for (int i = 0; i < rendererAsset.rendererFeatures.Count; i++) {
                    if (rendererAsset.rendererFeatures[i] is UIRendererFeature) { hasFeature = true; break; }
                }
            }
            if (!hasFeature) {
                var feature = ScriptableObject.CreateInstance<UIRendererFeature>();
                feature.name = nameof(UIRendererFeature);
                AssetDatabase.AddObjectToAsset(feature, rendererAsset);
                rendererAsset.rendererFeatures.Add(feature);
                EditorUtility.SetDirty(rendererAsset);
            }

            var pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
            if (pipelineAsset == null) {
                pipelineAsset = UniversalRenderPipelineAsset.Create(rendererAsset);
                AssetDatabase.CreateAsset(pipelineAsset, PipelineAssetPath);
            }

            AssetDatabase.SaveAssets();

            var graphicsSettings = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettings != null) {
                var so = new SerializedObject(graphicsSettings);
                var rpProp = so.FindProperty("m_CustomRenderPipeline");
                if (rpProp != null && rpProp.objectReferenceValue != pipelineAsset) {
                    rpProp.objectReferenceValue = pipelineAsset;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            for (int i = 0; i < QualitySettings.count; i++) {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipelineAsset;
            }

            Debug.Log($"Weva: URP configured at {PipelineAssetPath} with UIRendererFeature.");
        }
    }
}
