using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Parsing {
    // HTML5 §13.2 fragment normalisation: HtmlParser synthesises
    // `<html>`/`<head>`/`<body>` wrappers around any fragment that doesn't
    // already include an `<html>` start tag. The fix exists because `:root`
    // (defined as the first Element child of Document) and custom-property
    // inheritance on `:root { ... }` were both broken for fragments where
    // the first element was a `<link>` / `<style>` rather than the content
    // root — those rules then lived on a `display: none` `<link>` and never
    // cascaded down to the actual UI tree.
    public class HtmlFragmentWrapperTests {
        // 1. Bare body fragment → Document > html > body > main.
        [Test]
        public void Bare_main_fragment_wrapped_in_html_and_body() {
            var doc = HtmlParser.Parse("<main>content</main>");

            Assert.That(doc.Children, Has.Count.EqualTo(1));
            var html = (Element)doc.Children[0];
            Assert.That(html.TagName, Is.EqualTo("html"));
            Assert.That(html.Parent, Is.SameAs(doc));

            // <html> has at least one child <body> (head is empty so we
            // allow either [body] or [head, body]).
            Element body = null;
            foreach (var c in html.Children) {
                if (c is Element e && e.TagName == "body") body = e;
            }
            Assert.That(body, Is.Not.Null);

            Assert.That(body.Children, Has.Count.EqualTo(1));
            var main = (Element)body.Children[0];
            Assert.That(main.TagName, Is.EqualTo("main"));
            // Chain: main.Parent == body, body.Parent == html, html.Parent == doc.
            Assert.That(main.Parent.Parent.Parent, Is.SameAs(doc));
        }

        // 2. Full document (explicit <html><body>) is NOT re-wrapped.
        [Test]
        public void Full_document_with_explicit_html_and_body_is_not_rewrapped() {
            var doc = HtmlParser.Parse("<html><body><main>x</main></body></html>");

            Assert.That(doc.Children, Has.Count.EqualTo(1));
            var html = (Element)doc.Children[0];
            Assert.That(html.TagName, Is.EqualTo("html"));
            // Exactly one body, exactly one main, in the right places.
            int bodyCount = 0;
            Element body = null;
            foreach (var c in html.Children) {
                if (c is Element e && e.TagName == "body") { bodyCount++; body = e; }
            }
            Assert.That(bodyCount, Is.EqualTo(1), "no synthetic <body> on top of the author's explicit one");
            Assert.That(body, Is.Not.Null);
            Assert.That(body.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)body.Children[0]).TagName, Is.EqualTo("main"));
        }

        // 3. Mixed head + body content — <link> goes into synthetic <head>,
        //    <main> into synthetic <body>.
        [Test]
        public void Link_before_body_content_lands_in_synthetic_head() {
            var doc = HtmlParser.Parse("<link rel=\"stylesheet\" href=\"x.css\" /><main>x</main>");

            var html = (Element)doc.Children[0];
            Assert.That(html.TagName, Is.EqualTo("html"));

            Element head = null, body = null;
            foreach (var c in html.Children) {
                if (c is Element e) {
                    if (e.TagName == "head") head = e;
                    else if (e.TagName == "body") body = e;
                }
            }
            Assert.That(head, Is.Not.Null, "expected a synthetic <head>");
            Assert.That(body, Is.Not.Null, "expected a synthetic <body>");

            // <link> in <head>.
            Element link = null;
            foreach (var c in head.Children) {
                if (c is Element e && e.TagName == "link") link = e;
            }
            Assert.That(link, Is.Not.Null);
            Assert.That(link.GetAttribute("href"), Is.EqualTo("x.css"));

            // <main> in <body>.
            Assert.That(body.Children, Has.Count.EqualTo(1));
            var main = (Element)body.Children[0];
            Assert.That(main.TagName, Is.EqualTo("main"));
        }

        static (Element head, Element body) HeadBody(Element html) {
            Element head = null, body = null;
            foreach (var c in html.Children) {
                if (c is Element e) {
                    if (e.TagName == "head") head = e;
                    else if (e.TagName == "body") body = e;
                }
            }
            return (head, body);
        }

        // 3b. REGRESSION (editor-panel gray-render bug): a fragment that STARTS
        // with `<style>…css…</style>` followed by flow content must route the
        // content into <body>, NOT orphan it. The `<style>` element's TEXT
        // (the CSS) is its own content; it must not trigger the head→body
        // transition mid-element (which closed <head> while <style> was still
        // open, nesting <body> inside <style> and leaving the real content
        // outside any rendered body → blank document).
        [Test]
        public void Style_first_with_css_text_routes_following_content_to_body() {
            var doc = HtmlParser.Parse("<style>.panel{background:#1e1e22}</style><div class=\"panel\">x</div>");
            var html = (Element)doc.Children[0];
            var (head, body) = HeadBody(html);
            Assert.That(head, Is.Not.Null, "expected a synthetic <head>");
            Assert.That(body, Is.Not.Null, "expected a synthetic <body>");

            // <style> lives in <head> and is NOT a container for <body>.
            Element style = null;
            foreach (var c in head.Children) if (c is Element e && e.TagName == "style") style = e;
            Assert.That(style, Is.Not.Null, "<style> must be in <head>");
            foreach (var c in style.Children) {
                Assert.That(!(c is Element ce && ce.TagName == "body"),
                    "<body> must NOT be nested inside <style>");
            }

            // The .panel content is a direct child of <body>.
            Element panel = null;
            foreach (var c in body.Children) if (c is Element e && e.GetAttribute("class") == "panel") panel = e;
            Assert.That(panel, Is.Not.Null, ".panel must land in <body>, not be orphaned outside it");
            Assert.That(panel.Parent, Is.SameAs(body));
        }

        // 3c. The CSS text inside <style> stays as the style element's own
        // text content (so the stylesheet actually parses), not pushed to body.
        [Test]
        public void Style_text_content_stays_inside_the_style_element() {
            var doc = HtmlParser.Parse("<style>.a{color:red}</style><div class=\"a\">x</div>");
            var html = (Element)doc.Children[0];
            var (head, _) = HeadBody(html);
            Element style = null;
            foreach (var c in head.Children) if (c is Element e && e.TagName == "style") style = e;
            Assert.That(style, Is.Not.Null);
            bool hasCssText = false;
            foreach (var c in style.Children) if (c is TextNode tn && tn.Data.Contains(".a{color:red}")) hasCssText = true;
            Assert.That(hasCssText, Is.True, "the CSS must remain the <style> element's text content");
        }

        // 3d. Same bug class for <title> (RCDATA text-bearing head element).
        [Test]
        public void Title_first_with_text_routes_following_content_to_body() {
            var doc = HtmlParser.Parse("<title>My Panel</title><main>content</main>");
            var html = (Element)doc.Children[0];
            var (head, body) = HeadBody(html);
            Assert.That(body, Is.Not.Null);
            Element title = null;
            foreach (var c in head.Children) if (c is Element e && e.TagName == "title") title = e;
            Assert.That(title, Is.Not.Null, "<title> must be in <head>");
            Element main = null;
            foreach (var c in body.Children) if (c is Element e && e.TagName == "main") main = e;
            Assert.That(main, Is.Not.Null, "<main> must land in <body>");
            Assert.That(main.Parent, Is.SameAs(body));
        }

        // 3e. End-to-end: the .panel background actually cascades (proving the
        // <style> in <head> is live and the content is in the rendered body).
        [Test]
        public void Style_first_panel_background_cascades_to_body_content() {
            var doc = HtmlParser.Parse(
                "<style>.panel{background-color:#1e1e22}</style><div class=\"panel\">x</div>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.Author(CssParser.Parse(
                    ExtractStyleCss(doc)))
            });
            Element panel = null;
            void Walk(Node n) {
                if (n is Element e && e.GetAttribute("class") == "panel") panel = e;
                foreach (var c in n.Children) Walk(c);
            }
            Walk(doc);
            Assert.That(panel, Is.Not.Null, ".panel must exist in the rendered body");
            var cs = engine.Compute(panel);
            Assert.That(cs.Get("background-color"), Is.EqualTo("#1e1e22"));
        }

        static string ExtractStyleCss(Document doc) {
            var sb = new System.Text.StringBuilder();
            void Walk(Node n) {
                if (n is Element e && e.TagName == "style") {
                    foreach (var c in e.Children) if (c is TextNode tn) sb.Append(tn.Data);
                }
                foreach (var c in n.Children) Walk(c);
            }
            Walk(doc);
            return sb.ToString();
        }

        // 4. The smoking-gun integration test: `:root` custom properties
        //    propagate down through the synthetic wrapper into the panel.
        [Test]
        public void Root_custom_properties_propagate_through_synthetic_wrapper() {
            const string css = ":root { --bg-panel: #131826; } .panel { background: var(--bg-panel); }";
            var doc = HtmlParser.Parse("<div class=\"panel\"></div>");
            var sheet = OriginatedStylesheet.Author(CssParser.Parse(css));
            var engine = new CascadeEngine(new[] { sheet });

            Element panel = null;
            foreach (var e in doc.GetElementsByClassName("panel")) { panel = e; break; }
            Assert.That(panel, Is.Not.Null);

            var cs = engine.Compute(panel);
            // The var() reference is resolved to the :root-declared colour.
            // background shorthand sets background-color; assert against it.
            Assert.That(cs.Get("background-color"), Is.EqualTo("#131826"));
        }

        // 5. Regression: a `:root` rule that matches nothing useful in the
        //    synthetic-wrap case (e.g. an empty fragment with only :root
        //    declarations) doesn't crash the cascade.
        [Test]
        public void Empty_fragment_with_root_rule_does_not_crash() {
            // Empty fragments stay empty per the explicit "no synthesis
            // for empty input" contract. The cascade engine should walk
            // the (empty) tree without throwing.
            var doc = HtmlParser.Parse("");
            var sheet = OriginatedStylesheet.Author(CssParser.Parse(":root { --x: 1px; }"));
            var engine = new CascadeEngine(new[] { sheet });
            Assert.DoesNotThrow(() => engine.ComputeAll(doc));
        }

        // The real-world symptom: a production main menu used a bare
        // fragment with a leading <link rel="stylesheet"/>, then defined
        // `--bg-panel` on `:root`. Before the fix `:root` matched the
        // <link> (which is `display: none`), so `--bg-panel` lived on the
        // link and never inherited into `<main>`.
        [Test]
        public void Main_menu_root_var_reaches_panel_background() {
            const string html =
                "<link rel=\"stylesheet\" href=\"main-menu.css\" />" +
                "<main class=\"hud\"><div class=\"panel\"></div></main>";
            const string css =
                ":root { --bg-panel: #131826; }" +
                ".panel { background: var(--bg-panel); }";

            var doc = HtmlParser.Parse(html);
            var sheet = OriginatedStylesheet.Author(CssParser.Parse(css));
            var engine = new CascadeEngine(new[] { sheet });

            Element panel = null;
            foreach (var e in doc.GetElementsByClassName("panel")) { panel = e; break; }
            Assert.That(panel, Is.Not.Null);
            Assert.That(engine.Compute(panel).Get("background-color"), Is.EqualTo("#131826"),
                "the :root custom property must inherit through the synthetic <html>/<body> wrappers into .panel");
        }
    }
}
