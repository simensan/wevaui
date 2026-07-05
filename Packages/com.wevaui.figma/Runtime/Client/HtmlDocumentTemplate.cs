using System.Collections.Generic;
using System.Text;

namespace Weva.Figma.Client
{
    /// <summary>Wraps an exported body fragment in a complete, Weva-loadable HTML document.</summary>
    public static class HtmlDocumentTemplate
    {
        public static string Wrap(string bodyFragment, IEnumerable<string> stylesheetHrefs, string title)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html>\n<head>\n");
            sb.Append("  <meta charset=\"UTF-8\" />\n");
            sb.Append("  <title>").Append(Mapping.HtmlWriter.EscapeText(title ?? "Figma Export")).Append("</title>\n");
            if (stylesheetHrefs != null)
                foreach (string href in stylesheetHrefs)
                    sb.Append("  <link rel=\"stylesheet\" href=\"").Append(Mapping.HtmlWriter.EscapeAttr(href)).Append("\" />\n");
            sb.Append("</head>\n<body>\n");
            sb.Append(bodyFragment);
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }
    }
}
