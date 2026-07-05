using System.Collections.Generic;
using System.IO;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Parsing;

namespace Weva.Css {
    // CSS Cascading Level 4 §6 — `@import` URL loader.
    //
    // The CssParser produces `ImportRule` nodes but cannot fetch the referenced
    // stylesheet itself (it is a pure syntactic pass with no I/O surface). This
    // class is the document-level driver that walks a parsed sheet, resolves
    // every `@import url(...)`/`@import "..."` against the importing sheet's
    // path, loads + parses the target sheet from disk, and splices its rules
    // INTO the importing sheet AT the position of the import — preserving the
    // source-order semantics from CSS Cascading L4 §6.4.1 (the imported
    // stylesheet's rules cascade as if they were written in place of the
    // `@import` statement).
    //
    // Design notes:
    //   - The loader is a separate pass run BEFORE OriginatedStylesheet wraps
    //     the result, so origin / specificity tracking sees a single fully
    //     resolved sheet. The cascade engine never sees an `ImportRule`.
    //   - Loading is synchronous: Unity loads CSS from local disk under
    //     `Assets/`. No remote URLs, no async fetching.
    //   - Cycle detection (A→B→A) is by absolute-path set. On cycle we emit
    //     a single UICssDiagnostics warning and skip the re-import.
    //   - Media-query suffix (`@import url(...) screen and (min-width: 600px)`)
    //     is evaluated against the supplied MediaContext at load time. A
    //     non-matching media query drops the entire imported sheet from the
    //     result — matches CSS Cascading L4 §6.1 ("If the media list doesn't
    //     match, the stylesheet's rules are skipped").
    //   - Remote URLs (http:/https:/data:) are deliberately a no-op: this is
    //     v1 scope; we warn and drop the rule rather than crash on an attempt
    //     to File.ReadAllText("http://...").
    public static class AtImportLoader {
        // Resolve all imports in `sheet`, producing a fresh stylesheet whose
        // rule list has every `@import` replaced inline by the imported sheet's
        // rules. `sheetPath` is the absolute path the importing sheet was
        // loaded from (used to resolve relative hrefs); pass null for in-memory
        // sources, in which case relative hrefs cannot be resolved and we
        // warn + drop.
        //
        // `media` gates `@import ... <media-query>` clauses; pass the document's
        // MediaContext. `fileReader` is the I/O hook (default: File.ReadAllText)
        // — tests inject a fake to avoid disk writes in cycle/media tests.
        // `parseOptions` is forwarded to nested `CssParser.Parse` calls.
        public static Stylesheet Resolve(
            Stylesheet sheet,
            string sheetPath,
            MediaContext media,
            System.Func<string, string> fileReader = null,
            ParseOptions parseOptions = null) {
            if (sheet == null) return null;
            fileReader ??= File.ReadAllText;
            parseOptions ??= new ParseOptions();
            var visited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(sheetPath)) {
                visited.Add(NormalizePath(sheetPath));
            }
            var result = new Stylesheet();
            ResolveInto(sheet, sheetPath, media, fileReader, parseOptions, visited, result.Rules);
            return result;
        }

        // Recursive splice. Walks `source.Rules`; for each `ImportRule`, loads
        // the target and recurses; for everything else, copies the rule
        // reference verbatim. We do NOT deep-clone — the cascade compiler is
        // read-only on Rule instances, so sharing references is safe and keeps
        // allocation cost flat with the rule count.
        static void ResolveInto(
            Stylesheet source,
            string sourcePath,
            MediaContext media,
            System.Func<string, string> fileReader,
            ParseOptions parseOptions,
            HashSet<string> visited,
            List<Rule> output) {
            foreach (var rule in source.Rules) {
                if (rule is ImportRule imp) {
                    SpliceImport(imp, sourcePath, media, fileReader, parseOptions, visited, output);
                } else {
                    output.Add(rule);
                }
            }
        }

        static void SpliceImport(
            ImportRule imp,
            string importingSheetPath,
            MediaContext media,
            System.Func<string, string> fileReader,
            ParseOptions parseOptions,
            HashSet<string> visited,
            List<Rule> output) {
            string href = imp.Href;
            if (string.IsNullOrEmpty(href)) {
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "AtImportLoader", "empty @import href, skipped");
                return;
            }
            // CSS Cascade L5 §3.3 — supports() clause gates the import before
            // we even hit the network/disk. A false supports() drops the
            // entire imported sheet regardless of media.
            //
            // SupportsText holds the raw inner content of supports(…), without
            // the outer parentheses (the CssParser consumed them as the Function
            // token). SupportsEvaluator.Evaluate expects either a parenthesised
            // declaration "(prop: val)" or a nested condition — so we wrap the
            // stored text in parens before evaluating.
            if (imp.HasSupports && !SupportsEvaluator.Evaluate("(" + imp.SupportsText + ")")) {
                return;
            }
            // CSS Cascading L4 §6.1 — media list gates the entire imported sheet.
            // Empty / whitespace media text means "apply unconditionally".
            if (!MatchesMedia(imp.MediaConditionText, media)) {
                return;
            }
            // Remote / data URLs are out of scope for v1: we have no async
            // fetcher and Unity authors load from Assets/. Warn-and-drop.
            if (IsRemoteHref(href)) {
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "AtImportLoader", "remote @import not supported (v1): " + href);
                return;
            }
            string resolved = ResolveHref(href, importingSheetPath);
            if (resolved == null) {
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "AtImportLoader",
                    "cannot resolve relative @import without base path: " + href);
                return;
            }
            string normalized = NormalizePath(resolved);
            if (visited.Contains(normalized)) {
                // CSS Cascading L4 §6 — cycle detection. We log once and drop.
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "AtImportLoader", "cyclic @import detected, skipped: " + href);
                return;
            }
            string content;
            try {
                content = fileReader(resolved);
            } catch (System.Exception ex) {
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "AtImportLoader",
                    "failed to read @import target '" + resolved + "': " + ex.Message);
                return;
            }
            if (content == null) return;
            var nested = CssParser.Parse(content, parseOptions);
            // Track the *normalized* path so any path-style variant of the
            // same absolute path (e.g. "./a.css" vs "a.css" from the same
            // directory) hashes to a single visited entry.
            visited.Add(normalized);
            if (imp.HasLayer) {
                // CSS Cascade L5 §3.3 — `@import url(x) layer(name)` places the
                // imported sheet's rules inside the named (or anonymous) layer.
                // We synthesize a block-form LayerRule so the cascade's existing
                // @layer handling assigns the ordinal and tags every contained
                // rule with the layer.
                var inner = new List<Rule>();
                ResolveInto(nested, resolved, media, fileReader, parseOptions, visited, inner);
                var wrapper = new LayerRule { IsBlock = true };
                wrapper.Names.Add(string.IsNullOrEmpty(imp.Layer) ? null : imp.Layer);
                wrapper.Rules.AddRange(inner);
                output.Add(wrapper);
            } else {
                ResolveInto(nested, resolved, media, fileReader, parseOptions, visited, output);
            }
            // Remove after recursion so siblings can legitimately import the
            // same file (diamond import is NOT a cycle — only a back-edge is).
            visited.Remove(normalized);
        }

        // True when the media-query clause matches the current MediaContext
        // (or is empty/absent). A parse failure is treated as "does not
        // match" — this is the conservative choice and matches Cascade L4
        // §6 ("rules that fail to parse should be ignored").
        static bool MatchesMedia(string mediaText, MediaContext ctx) {
            if (string.IsNullOrWhiteSpace(mediaText)) return true;
            try {
                var list = MediaQueryParser.Parse(mediaText);
                return MediaQueryEvaluator.Evaluate(list, ctx);
            } catch (MediaQueryParseException) {
                return false;
            }
        }

        // Trivial scheme sniff. We accept `://` after an ASCII scheme as the
        // discriminator — anything matching is bypassed so File.ReadAllText
        // never sees a URL. Path-relative `./foo.css` and absolute disk paths
        // (`C:\Users\...` / `/var/...`) fall through to the loader.
        static bool IsRemoteHref(string href) {
            if (string.IsNullOrEmpty(href)) return false;
            if (href.StartsWith("//", System.StringComparison.Ordinal)) return true;
            if (href.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase)) return true;
            int colon = href.IndexOf(':');
            if (colon <= 1) return false; // Windows drive letter `C:` is not a scheme.
            // Anything that looks like `xxx://...`.
            for (int i = 0; i < colon; i++) {
                char c = href[i];
                if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.')) return false;
            }
            return colon + 2 < href.Length && href[colon + 1] == '/' && href[colon + 2] == '/';
        }

        // Resolve `href` against the importing sheet's directory. Absolute
        // hrefs are returned verbatim; relative hrefs need a base path. If
        // we have no base and the href is relative, we return null and the
        // caller drops with a warning.
        static string ResolveHref(string href, string basePath) {
            if (Path.IsPathRooted(href)) return href;
            if (string.IsNullOrEmpty(basePath)) return null;
            string dir = Path.GetDirectoryName(basePath);
            if (string.IsNullOrEmpty(dir)) return href;
            return Path.GetFullPath(Path.Combine(dir, href));
        }

        // Canonical key for the cycle-detection set: full absolute path with
        // directory separators normalized. Path.GetFullPath collapses `..`
        // and `.` segments; we additionally lowercase on Windows (the FS is
        // case-insensitive) to dedupe casing variants.
        static string NormalizePath(string p) {
            string full;
            try { full = Path.GetFullPath(p); }
            catch { full = p; }
            full = full.Replace('\\', '/');
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return full.ToLowerInvariant();
#else
            // Path comparison is case-sensitive on most non-Windows hosts;
            // the OrdinalIgnoreCase HashSet still tolerates case drift if it
            // occurs, which is the safer default for cycle detection.
            return full;
#endif
        }
    }
}
