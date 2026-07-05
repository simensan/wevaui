using System.IO;
using UnityEditor;
using UnityEngine;
using Weva.Figma.Client;
using Weva.Figma.Import;
using Weva.Figma.Mapping;
using Weva.Figma.Model;

namespace Weva.Figma.EditorTools
{
    /// <summary>
    /// Imports the tokenless export produced by the Figma plugin: pick a
    /// <c>*.figma.json</c> node file; a sibling <c>*.variables.json</c> is used
    /// for tokens if present. Output lands next to the json. Images exported by
    /// the plugin should be dropped into an <c>images/</c> subfolder beforehand.
    ///
    /// NEEDS UNITY VALIDATION: written without a Unity compile/run available.
    /// </summary>
    public static class FigmaJsonImportMenu
    {
        [MenuItem("Assets/Weva/Import Figma JSON…", priority = 2200)]
        public static void ImportJson()
        {
            string path = EditorUtility.OpenFilePanel("Import Figma export JSON", "Assets", "json");
            if (string.IsNullOrEmpty(path)) return;

            string nodeJson;
            try { nodeJson = File.ReadAllText(path); }
            catch (System.Exception e) { EditorUtility.DisplayDialog("Figma Import", "Could not read file: " + e.Message, "OK"); return; }

            FigmaNode root;
            try { root = FigmaNode.Parse(nodeJson); }
            catch (System.Exception e) { EditorUtility.DisplayDialog("Figma Import", "Could not parse node JSON: " + e.Message, "OK"); return; }

            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (baseName.EndsWith(".figma")) baseName = baseName.Substring(0, baseName.Length - ".figma".Length);

            string variablesJson = null;
            string varsPath = Path.Combine(dir, baseName + ".variables.json");
            if (File.Exists(varsPath)) variablesJson = File.ReadAllText(varsPath);

            var req = new FigmaImportRequest { OutputName = baseName, ImportTokens = true, ExportOptions = new ExportOptions() };
            var sink = new AssetFolderSink(ToProjectRelative(dir));
            FigmaImportResult result = FigmaImportService.ImportLocal(root, variablesJson, req, sink);

            AssetDatabase.Refresh();
            if (result.Success)
                Debug.Log($"[Weva.Figma] Imported {result.WrittenFiles.Count} file(s) from {Path.GetFileName(path)}. {result.Lint?.Summary()}");
            else
                EditorUtility.DisplayDialog("Figma Import", "Import failed: " + result.Error, "OK");
        }

        static string ToProjectRelative(string absolute)
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            absolute = absolute.Replace('\\', '/');
            return absolute.StartsWith(dataPath) ? "Assets" + absolute.Substring(dataPath.Length) : absolute;
        }
    }
}
