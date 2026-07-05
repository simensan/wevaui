using System.IO;
using Weva.Figma.Client;

namespace Weva.Figma.EditorTools
{
    /// <summary>
    /// Writes exported files under a project-relative folder (e.g.
    /// <c>Assets/UI/Figma</c>). Forward-slashed relative paths are mapped to the
    /// platform separator; subfolders (e.g. <c>images/</c>) are created on demand.
    /// Call <c>AssetDatabase.Refresh()</c> after a batch (the importer does).
    ///
    /// NEEDS UNITY VALIDATION: written without a Unity compile/run available.
    /// </summary>
    public sealed class AssetFolderSink : IExportSink
    {
        readonly string _absRoot;

        /// <param name="projectRelativeFolder">e.g. "Assets/UI/Figma".</param>
        public AssetFolderSink(string projectRelativeFolder)
        {
            _absRoot = Path.Combine(Directory.GetCurrentDirectory(), projectRelativeFolder);
            Directory.CreateDirectory(_absRoot);
        }

        public void WriteText(string relativePath, string text)
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        public void WriteBytes(string relativePath, byte[] data)
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        string Resolve(string relativePath)
            => Path.Combine(_absRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
