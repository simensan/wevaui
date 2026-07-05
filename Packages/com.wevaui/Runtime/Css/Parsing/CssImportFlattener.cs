using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Weva.Diagnostics;

namespace Weva.Css {
    // Pre-flattens @import rules in a CSS string by recursively reading
    // imported files and splicing their text in place of the @import statement.
    // Used by the editor bake (LinkedStylesheetBaker) so baked linked sheets
    // are self-contained: players have no on-disk base path so AtImportLoader
    // cannot resolve nested imports at runtime — the bake resolves them at
    // build time instead.
    //
    // Design:
    //   - Pure C# (no UnityEngine refs) so it compiles and tests headlessly.
    //   - I/O is injected via Func<string,string> fileReader (production uses
    //     File.ReadAllText; tests inject a fake).
    //   - Cycle detection: visited set of full paths; on cycle, emit a
    //     UICssDiagnostics.Warn and drop the import (same as AtImportLoader).
    //   - Media query on @import: imported content wrapped in @media { … }
    //     (CSS Cascade 4 §6 semantics).
    //   - Missing file / read error: warn + drop that import only.
    //   - Depth cap: MaxImportDepth (~8) to prevent infinite loops.
    //   - Remote hrefs (http:// / data:): left as-is (they won't resolve in
    //     players anyway; AtImportLoader handles them at runtime).
    //   - AtImportLoader is NOT changed — after flattening there are no
    //     @import statements left in the baked text, so its pass is a no-op.
    public static class CssImportFlattener {
        public const int MaxImportDepth = 8;

        // Regex patterns that match CSS @import rules in their syntactic forms.
        // Captures: Group 1 = href, Group 2 = optional media query text.
        static readonly Regex s_ImportUrlDoubleQuote = new Regex(
            @"@import\s+url\(\s*""([^""]+)""\s*\)((?:\s+[^;]*)?)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        static readonly Regex s_ImportUrlSingleQuote = new Regex(
            @"@import\s+url\(\s*'([^']+)'\s*\)((?:\s+[^;]*)?)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        static readonly Regex s_ImportUrlBare = new Regex(
            @"@import\s+url\(\s*([^\s\)""']+)\s*\)((?:\s+[^;]*)?)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        static readonly Regex s_ImportQuoted = new Regex(
            @"@import\s+""([^""]+)""((?:\s+[^;]*)?)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        static readonly Regex s_ImportSingleQuoted = new Regex(
            @"@import\s+'([^']+)'((?:\s+[^;]*)?)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // Flatten @import rules in `cssText`.
        // `cssFilePath` is the absolute path the CSS file was loaded from
        //   (used to resolve relative hrefs; pass null for in-memory strings).
        // `fileReader` is the I/O hook — inject File.ReadAllText in production;
        //   tests inject a fake to avoid disk writes.
        public static string Flatten(string cssText, string cssFilePath,
            Func<string, string> fileReader) {
            if (string.IsNullOrEmpty(cssText)) return cssText;
            if (fileReader == null) throw new ArgumentNullException(nameof(fileReader));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(cssFilePath)) {
                string norm;
                try { norm = Path.GetFullPath(cssFilePath); } catch { norm = cssFilePath; }
                visited.Add(norm);
            }
            return FlattenCore(cssText, cssFilePath, fileReader, visited, 0);
        }

        // ----------------------------------------------------------------
        // Internal helpers
        // ----------------------------------------------------------------

        internal static string FlattenCore(string cssText, string cssFilePath,
            Func<string, string> fileReader, HashSet<string> visited, int depth) {
            if (depth > MaxImportDepth || string.IsNullOrEmpty(cssText)) return cssText;

            var imports = CollectImports(cssText);
            if (imports.Count == 0) return cssText;

            imports.Sort((a, b) => a.Index.CompareTo(b.Index));

            var sb = new StringBuilder();
            int pos = 0;
            for (int i = 0; i < imports.Count; i++) {
                var imp = imports[i];
                sb.Append(cssText, pos, imp.Index - pos);
                pos = imp.Index + imp.Length;

                string href = imp.Href;
                string media = imp.Media?.Trim() ?? "";

                // Remote hrefs are out of scope — leave them as literal text so
                // AtImportLoader's runtime warning path still fires.
                if (IsRemoteHref(href)) {
                    sb.Append(cssText, imp.Index, imp.Length);
                    continue;
                }

                string resolved = ResolveHref(href, cssFilePath);
                if (resolved == null) {
                    UICssDiagnostics.Warn("CssImportFlattener",
                        "cannot resolve @import '" + href + "' (no base path); dropping import.");
                    continue;
                }

                string normalized;
                try { normalized = Path.GetFullPath(resolved); } catch { normalized = resolved; }

                if (visited.Contains(normalized)) {
                    UICssDiagnostics.Warn("CssImportFlattener",
                        "cyclic @import detected for '" + href + "'; dropping import.");
                    continue;
                }

                // Attempt to read the file; treat any exception as "not found /
                // not readable" and drop that import with a warning. This also
                // allows tests to inject a fake reader that throws
                // FileNotFoundException without needing File.Exists to succeed.
                string importedText;
                try {
                    importedText = fileReader(normalized);
                } catch (FileNotFoundException) {
                    UICssDiagnostics.Warn("CssImportFlattener",
                        "@import target '" + normalized + "' not found; dropping import.");
                    continue;
                } catch (Exception ex) {
                    UICssDiagnostics.Warn("CssImportFlattener",
                        "failed to read @import '" + normalized + "': " + ex.Message + "; dropping import.");
                    continue;
                }

                visited.Add(normalized);
                string flattened = FlattenCore(importedText ?? "", normalized, fileReader, visited, depth + 1);
                visited.Remove(normalized);

                if (!string.IsNullOrEmpty(media)) {
                    sb.Append("@media ");
                    sb.Append(media);
                    sb.Append(" {\n");
                    sb.Append(flattened);
                    sb.Append("\n}");
                } else {
                    sb.Append(flattened);
                }
            }
            sb.Append(cssText, pos, cssText.Length - pos);
            return sb.ToString();
        }

        internal struct ImportMatch {
            public int Index;
            public int Length;
            public string Href;
            public string Media;
        }

        internal static List<ImportMatch> CollectImports(string cssText) {
            var result = new List<ImportMatch>();
            AddMatches(s_ImportUrlDoubleQuote, cssText, result);
            AddMatches(s_ImportUrlSingleQuote, cssText, result);
            AddMatches(s_ImportUrlBare, cssText, result);
            AddMatches(s_ImportQuoted, cssText, result);
            AddMatches(s_ImportSingleQuoted, cssText, result);
            // Deduplicate by index (multiple patterns could match the same
            // syntactic position in corner cases).
            var seen = new HashSet<int>();
            var deduped = new List<ImportMatch>(result.Count);
            for (int i = 0; i < result.Count; i++) {
                if (seen.Add(result[i].Index)) deduped.Add(result[i]);
            }
            return deduped;
        }

        static void AddMatches(Regex regex, string cssText, List<ImportMatch> output) {
            var ms = regex.Matches(cssText);
            for (int i = 0; i < ms.Count; i++) {
                var m = ms[i];
                output.Add(new ImportMatch {
                    Index = m.Index,
                    Length = m.Length,
                    Href = m.Groups[1].Value,
                    Media = m.Groups.Count > 2 ? m.Groups[2].Value : null
                });
            }
        }

        static string ResolveHref(string href, string basePath) {
            if (string.IsNullOrEmpty(href)) return null;
            try {
                if (Path.IsPathRooted(href)) return href;
                if (string.IsNullOrEmpty(basePath)) return null;
                string dir = Path.GetDirectoryName(basePath);
                if (string.IsNullOrEmpty(dir)) return href;
                return Path.Combine(dir, href);
            } catch {
                return null;
            }
        }

        static bool IsRemoteHref(string href) {
            if (string.IsNullOrEmpty(href)) return false;
            if (href.StartsWith("//", StringComparison.Ordinal)) return true;
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
            int colon = href.IndexOf(':');
            if (colon <= 1) return false;
            for (int i = 0; i < colon; i++) {
                char c = href[i];
                if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.')) return false;
            }
            return colon + 2 < href.Length && href[colon + 1] == '/' && href[colon + 2] == '/';
        }
    }
}
