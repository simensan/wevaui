using System.IO;
using UnityEditor;
using UnityEngine;
using Weva.Figma.Client;
using Weva.Figma.Import;
using Weva.Figma.Linting;
using Weva.Figma.Mapping;

namespace Weva.Figma.EditorTools
{
    /// <summary>
    /// "Window ▸ Weva ▸ Figma Import": paste a Figma URL (or file key), provide
    /// a personal-access token, and import the selected frame as a Weva
    /// HTML+CSS pair (plus tokens and downloaded images) into the project. The
    /// engine's asset watcher hot-reloads any live WevaDocument on the resulting
    /// import. A subset lint report is shown before/after.
    ///
    /// NEEDS UNITY VALIDATION: written without a Unity compile/run available.
    /// </summary>
    public sealed class FigmaImportWindow : EditorWindow
    {
        const string TokenPrefKey = "Weva.Figma.Token";

        string _url = "";
        string _token = "";
        string _outputFolder = "Assets/UI/Figma";
        string _outputName = "figma";
        string _classPrefix = "";
        bool _importTokens = true;
        bool _downloadImages = true;
        Vector2 _scroll;
        FigmaImportResult _result;
        string _status;

        [MenuItem("Window/Weva/Figma Import")]
        public static void Open()
        {
            var w = GetWindow<FigmaImportWindow>("Figma Import");
            w.minSize = new Vector2(420, 360);
        }

        void OnEnable() => _token = EditorPrefs.GetString(TokenPrefKey, "");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            _url = EditorGUILayout.TextField(new GUIContent("Figma URL / key", "Paste a frame URL (…?node-id=1-23) or a bare file key."), _url);

            EditorGUI.BeginChangeCheck();
            _token = EditorGUILayout.PasswordField(new GUIContent("Access token", "Figma personal access token (stored in EditorPrefs)."), _token);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(TokenPrefKey, _token);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = EditorGUILayout.TextField("Folder", _outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Output folder", _outputFolder, "");
                    if (!string.IsNullOrEmpty(picked)) _outputFolder = ToProjectRelative(picked);
                }
            }
            _outputName = EditorGUILayout.TextField("File name", _outputName);
            _classPrefix = EditorGUILayout.TextField(new GUIContent("Class prefix", "Optional prefix on every generated class."), _classPrefix);
            _importTokens = EditorGUILayout.Toggle(new GUIContent("Import variables → tokens.css"), _importTokens);
            _downloadImages = EditorGUILayout.Toggle(new GUIContent("Download images"), _downloadImages);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_url) || string.IsNullOrWhiteSpace(_token)))
            {
                if (GUILayout.Button("Import", GUILayout.Height(28)))
                    RunImport();
            }

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _result != null && _result.Success ? MessageType.Info : MessageType.Error);

            DrawResult();
        }

        void RunImport()
        {
            _result = null;
            _status = null;
            FigmaTarget target = FigmaUrl.Parse(_url);
            if (string.IsNullOrEmpty(target.FileKey))
            {
                _status = "Could not determine a file key from the input.";
                return;
            }

            var req = new FigmaImportRequest
            {
                FileKey = target.FileKey,
                NodeId = target.HasNode ? target.NodeId : null,
                OutputName = string.IsNullOrWhiteSpace(_outputName) ? "figma" : _outputName,
                ImportTokens = _importTokens,
                DownloadImages = _downloadImages,
                ExportOptions = new ExportOptions { ClassPrefix = _classPrefix },
            };

            try
            {
                EditorUtility.DisplayProgressBar("Figma Import", "Fetching from Figma…", 0.3f);
                var http = new EditorFigmaHttp();
                var sink = new AssetFolderSink(_outputFolder);
                _result = FigmaImportService.Import(req, http, sink, _token);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
            _status = _result.Success
                ? $"Imported {_result.WrittenFiles.Count} file(s) into {_outputFolder}."
                : $"Import failed: {_result.Error}";
        }

        void DrawResult()
        {
            if (_result == null) return;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_result.WrittenFiles.Count > 0)
            {
                EditorGUILayout.LabelField("Written", EditorStyles.boldLabel);
                foreach (string f in _result.WrittenFiles)
                    EditorGUILayout.LabelField("• " + f);
            }

            if (_result.Lint != null && !_result.Lint.IsClean)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Lint — {_result.Lint.Summary()}", EditorStyles.boldLabel);
                foreach (LintDiagnostic d in _result.Lint.Diagnostics)
                {
                    var type = d.Severity == LintSeverity.Error ? MessageType.Error
                        : d.Severity == LintSeverity.Warning ? MessageType.Warning : MessageType.Info;
                    EditorGUILayout.HelpBox(d.ToString(), type);
                }
            }

            foreach (string w in _result.Warnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);

            EditorGUILayout.EndScrollView();
        }

        static string ToProjectRelative(string absolute)
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            absolute = absolute.Replace('\\', '/');
            if (absolute.StartsWith(dataPath))
                return "Assets" + absolute.Substring(dataPath.Length);
            return absolute;
        }
    }
}
