using System.Collections.Generic;
using System.Text;
using Weva.Figma.Model;

namespace Weva.Figma.Linting
{
    public enum LintSeverity
    {
        /// <summary>Handled, but needs an out-of-band step (e.g. rasterizing an image/vector).</summary>
        Info,
        /// <summary>Exported with loss — the result approximates the design.</summary>
        Warning,
        /// <summary>Cannot be represented; the node won't export meaningfully.</summary>
        Error,
    }

    public sealed class LintDiagnostic
    {
        public LintSeverity Severity;
        public string Code;       // stable machine-readable id, e.g. "blend-mode-unsupported"
        public string NodeId;
        public string NodeName;
        public string NodeType;
        public string Message;
        public string Suggestion; // optional remediation hint

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(Severity).Append("] ").Append(Code).Append(": ").Append(Message);
            if (!string.IsNullOrEmpty(NodeName)) sb.Append(" — '").Append(NodeName).Append('\'');
            if (!string.IsNullOrEmpty(Suggestion)) sb.Append("  → ").Append(Suggestion);
            return sb.ToString();
        }
    }

    public sealed class LintReport
    {
        public readonly List<LintDiagnostic> Diagnostics = new List<LintDiagnostic>();
        public int InfoCount, WarningCount, ErrorCount;

        public bool HasErrors => ErrorCount > 0;
        public bool HasWarnings => WarningCount > 0;
        public bool IsClean => Diagnostics.Count == 0;

        public void Add(LintSeverity severity, string code, FigmaNode node, string message, string suggestion = null)
        {
            Diagnostics.Add(new LintDiagnostic
            {
                Severity = severity,
                Code = code,
                NodeId = node?.Id,
                NodeName = node?.Name,
                NodeType = node?.Type,
                Message = message,
                Suggestion = suggestion,
            });
            switch (severity)
            {
                case LintSeverity.Info: InfoCount++; break;
                case LintSeverity.Warning: WarningCount++; break;
                case LintSeverity.Error: ErrorCount++; break;
            }
        }

        public IEnumerable<LintDiagnostic> WithCode(string code)
        {
            foreach (LintDiagnostic d in Diagnostics)
                if (d.Code == code) yield return d;
        }

        public string Summary()
            => $"{ErrorCount} error(s), {WarningCount} warning(s), {InfoCount} note(s)";

        public string Format()
        {
            var sb = new StringBuilder();
            sb.Append(Summary()).Append('\n');
            foreach (LintDiagnostic d in Diagnostics)
                sb.Append("  ").Append(d.ToString()).Append('\n');
            return sb.ToString();
        }
    }
}
