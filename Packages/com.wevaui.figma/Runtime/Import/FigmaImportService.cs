using System.Collections.Generic;
using Weva.Figma.Client;
using Weva.Figma.Linting;
using Weva.Figma.Mapping;
using Weva.Figma.Model;
using Weva.Figma.Tokens;

namespace Weva.Figma.Import
{
    public sealed class FigmaImportRequest
    {
        public string FileKey;
        public string NodeId;             // optional; if null, the first exportable frame of the file is used
        public string OutputName = "figma"; // base name for the .html/.css pair
        public ExportOptions ExportOptions;
        public bool ImportTokens = true;    // also fetch Variables → tokens.css
        public bool DownloadImages = true;
        public double ImageScale = 2;       // for rendered vectors
    }

    public sealed class FigmaImportResult
    {
        public bool Success;
        public string Error;
        public LintReport Lint;
        public ExportedDocument Document;
        public readonly List<string> WrittenFiles = new List<string>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Orchestrates a Figma import end to end — fetch, parse, lint, export, write
    /// files, download assets — over the injected <see cref="IFigmaHttp"/> and
    /// <see cref="IExportSink"/>. No Unity or network types here, so the whole
    /// flow is unit-tested headlessly; the Editor only supplies the two adapters.
    /// </summary>
    public static class FigmaImportService
    {
        public static FigmaImportResult Import(FigmaImportRequest req, IFigmaHttp http, IExportSink sink, string token)
        {
            var result = new FigmaImportResult();

            if (!TryResolveRoot(req, http, token, out FigmaNode root, out string rootError))
            {
                result.Error = rootError;
                return result;
            }

            result.Lint = SubsetLinter.Lint(root);
            ExportedDocument export = FigmaDocumentExporter.Export(root, req.ExportOptions);
            result.Document = export;

            var hrefs = new List<string>();

            if (req.ImportTokens
                && http.TryGetString(FigmaApiRoutes.Variables(req.FileKey), token, out string varsBody, out _))
            {
                string tokenCss = SafeBuildTokens(varsBody);
                if (!string.IsNullOrEmpty(tokenCss))
                {
                    sink.WriteText("tokens.css", tokenCss);
                    result.WrittenFiles.Add("tokens.css");
                    hrefs.Add("tokens.css"); // first so it cascades before component CSS
                }
            }

            string cssName = req.OutputName + ".css";
            hrefs.Add(cssName);

            string html = HtmlDocumentTemplate.Wrap(export.Html, hrefs, root.Name);
            sink.WriteText(req.OutputName + ".html", html);
            sink.WriteText(cssName, export.Css);
            result.WrittenFiles.Add(req.OutputName + ".html");
            result.WrittenFiles.Add(cssName);

            if (req.DownloadImages && export.RasterRequests.Count > 0)
                DownloadAssets(req, export, http, sink, token, result);

            result.Success = true;
            return result;
        }

        /// <summary>
        /// Tokenless import from data the Figma plugin already exported: a parsed
        /// node tree plus optional variables JSON. No network — lints, exports,
        /// and writes the html/css (+ tokens). Images are expected to be dropped
        /// alongside by the plugin, named to match <c>RasterNaming</c>.
        /// </summary>
        public static FigmaImportResult ImportLocal(FigmaNode root, string variablesJson,
            FigmaImportRequest req, IExportSink sink)
        {
            var result = new FigmaImportResult();
            if (root == null) { result.Error = "No node provided."; return result; }

            result.Lint = SubsetLinter.Lint(root);
            ExportedDocument export = FigmaDocumentExporter.Export(root, req.ExportOptions);
            result.Document = export;

            var hrefs = new List<string>();
            if (req.ImportTokens && !string.IsNullOrEmpty(variablesJson))
            {
                string tokenCss = SafeBuildTokens(variablesJson);
                if (!string.IsNullOrEmpty(tokenCss))
                {
                    sink.WriteText("tokens.css", tokenCss);
                    result.WrittenFiles.Add("tokens.css");
                    hrefs.Add("tokens.css");
                }
            }

            string cssName = req.OutputName + ".css";
            hrefs.Add(cssName);

            string html = HtmlDocumentTemplate.Wrap(export.Html, hrefs, root.Name);
            sink.WriteText(req.OutputName + ".html", html);
            sink.WriteText(cssName, export.Css);
            result.WrittenFiles.Add(req.OutputName + ".html");
            result.WrittenFiles.Add(cssName);

            foreach (RasterRequest r in export.RasterRequests)
                result.Warnings.Add($"Drop the exported image alongside as {r.FileName}.");

            result.Success = true;
            return result;
        }

        static bool TryResolveRoot(FigmaImportRequest req, IFigmaHttp http, string token,
            out FigmaNode root, out string error)
        {
            root = null;
            error = null;
            if (!string.IsNullOrEmpty(req.NodeId))
            {
                if (!http.TryGetString(FigmaApiRoutes.FileNodes(req.FileKey, new[] { req.NodeId }), token, out string body, out error))
                    return false;
                Dictionary<string, FigmaNode> nodes = FigmaResponses.ParseNodes(body);
                if (!nodes.TryGetValue(req.NodeId, out root))
                {
                    error = $"Node '{req.NodeId}' was not present in the response.";
                    return false;
                }
                return true;
            }

            if (!http.TryGetString(FigmaApiRoutes.File(req.FileKey), token, out string fileBody, out error))
                return false;
            FigmaNode doc = FigmaResponses.ParseFileDocument(fileBody);
            List<FigmaNode> frames = FigmaNodeQuery.CollectExportableFrames(doc);
            if (frames.Count == 0)
            {
                error = "No exportable frames found in the file.";
                return false;
            }
            root = frames[0];
            return true;
        }

        static string SafeBuildTokens(string varsBody)
        {
            // The variables endpoint is enterprise-only; tolerate any non-variables payload.
            try { return VariablesToCss.Build(varsBody).Css; }
            catch { return null; }
        }

        static void DownloadAssets(FigmaImportRequest req, ExportedDocument export, IFigmaHttp http,
            IExportSink sink, string token, FigmaImportResult result)
        {
            // Image fills: imageRef → url, then fetch each referenced bitmap.
            var imageRefs = new List<string>();
            var vectorIds = new List<string>();
            foreach (RasterRequest r in export.RasterRequests)
            {
                if (r.Kind == RasterKind.ImageFill && r.ImageRef != null) imageRefs.Add(r.ImageRef);
                else if (r.Kind == RasterKind.Vector && r.NodeId != null) vectorIds.Add(r.NodeId);
            }

            Dictionary<string, string> fillUrls = null;
            if (imageRefs.Count > 0
                && http.TryGetString(FigmaApiRoutes.ImageFills(req.FileKey), token, out string fillBody, out _))
                fillUrls = FigmaResponses.ParseImageFillUrls(fillBody);

            Dictionary<string, string> vectorUrls = null;
            if (vectorIds.Count > 0
                && http.TryGetString(FigmaApiRoutes.RenderImages(req.FileKey, vectorIds, "png", req.ImageScale), token, out string renderBody, out _))
                vectorUrls = FigmaResponses.ParseRenderedImageUrls(renderBody);

            foreach (RasterRequest r in export.RasterRequests)
            {
                string url = null;
                if (r.Kind == RasterKind.ImageFill) fillUrls?.TryGetValue(r.ImageRef, out url);
                else vectorUrls?.TryGetValue(r.NodeId, out url);

                if (url == null)
                {
                    result.Warnings.Add($"No image URL for {r.FileName}.");
                    continue;
                }
                if (http.TryGetBytes(url, token, out byte[] data, out string err))
                {
                    sink.WriteBytes(r.FileName, data);
                    result.WrittenFiles.Add(r.FileName);
                }
                else
                {
                    result.Warnings.Add($"Failed to download {r.FileName}: {err}");
                }
            }
        }
    }
}
