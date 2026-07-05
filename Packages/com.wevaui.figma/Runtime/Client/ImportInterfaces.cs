namespace Weva.Figma.Client
{
    /// <summary>
    /// Minimal HTTP surface the import service needs. The Editor implements this
    /// with <c>UnityWebRequest</c> (+ the <c>X-Figma-Token</c> header); tests
    /// implement it with canned responses. Synchronous by design — import is an
    /// editor-time action.
    /// </summary>
    public interface IFigmaHttp
    {
        bool TryGetString(string url, string token, out string body, out string error);
        bool TryGetBytes(string url, string token, out byte[] data, out string error);
    }

    /// <summary>
    /// Where generated files land. The Editor writes to the project's Assets
    /// folder (then refreshes the AssetDatabase); tests capture writes in memory.
    /// Paths are relative to the chosen output folder, forward-slashed.
    /// </summary>
    public interface IExportSink
    {
        void WriteText(string relativePath, string text);
        void WriteBytes(string relativePath, byte[] data);
    }
}
