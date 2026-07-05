using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Weva.EditorTools.Importers {
    // Unity's default TextAsset importer covers .txt/.html/.json/.xml/.bytes but
    // not .css (and behavior on .htm/.html varies across versions). Register a
    // ScriptedImporter so any file with these extensions is reliably imported
    // as a TextAsset that WevaDocument.SetDocumentAsset / SetStylesheetAssets can
    // reference.
    [ScriptedImporter(version: 1, exts: new[] { "css" })]
    public sealed class TextAssetExtensionImporter : ScriptedImporter {
        public override void OnImportAsset(AssetImportContext ctx) {
            var text = File.ReadAllText(ctx.assetPath);
            var asset = new TextAsset(text);
            ctx.AddObjectToAsset("text", asset);
            ctx.SetMainObject(asset);
        }
    }
}
