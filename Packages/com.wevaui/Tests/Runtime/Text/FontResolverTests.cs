using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    public class FontResolverTests {
        [SetUp]
        public void Reset() {
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/system/sans.ttf",
                ["serif"] = "/system/serif.ttf",
                ["monospace"] = "/system/mono.ttf",
                ["system-ui"] = "/system/ui.ttf"
            });
            FontResolver.DefaultFamily = "sans-serif";
        }

        [Test]
        public void Builtin_sans_serif_resolves() {
            var face = FontResolver.Resolve(new FontHandle("sans-serif", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("sans-serif"));
            Assert.That(face.Path, Is.EqualTo("/system/sans.ttf"));
        }

        [Test]
        public void Builtin_serif_resolves() {
            var face = FontResolver.Resolve(new FontHandle("serif", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/system/serif.ttf"));
        }

        [Test]
        public void Builtin_monospace_resolves() {
            var face = FontResolver.Resolve(new FontHandle("monospace", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/system/mono.ttf"));
        }

        [Test]
        public void Registered_custom_family_resolves() {
            FontResolver.RegisterFont("Inter", "/fonts/Inter.ttf");
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("Inter"));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter.ttf"));
        }

        [Test]
        public void Unknown_family_falls_back_to_default() {
            var face = FontResolver.Resolve(new FontHandle("DoesNotExist", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("sans-serif"));
            Assert.That(face.Path, Is.EqualTo("/system/sans.ttf"));
        }

        [Test]
        public void Lookup_is_case_insensitive() {
            FontResolver.RegisterFont("Inter", "/fonts/Inter.ttf");
            var face = FontResolver.Resolve(new FontHandle("inter", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter.ttf"));
            face = FontResolver.Resolve(new FontHandle("INTER", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter.ttf"));
            face = FontResolver.Resolve(new FontHandle("SANS-SERIF", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/system/sans.ttf"));
        }

        [Test]
        public void Comma_list_picks_first_match() {
            FontResolver.RegisterFont("Inter", "/fonts/Inter.ttf");
            var face = FontResolver.Resolve(new FontHandle("DoesNotExist, Inter, sans-serif", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("Inter"));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter.ttf"));
        }

        [Test]
        public void Quoted_family_names_strip_quotes() {
            FontResolver.RegisterFont("My Font", "/fonts/MyFont.ttf");
            var faceDouble = FontResolver.Resolve(new FontHandle("\"My Font\"", 16, 400, FontStyle.Normal));
            Assert.That(faceDouble.Family, Is.EqualTo("My Font"));
            var faceSingle = FontResolver.Resolve(new FontHandle("'My Font'", 16, 400, FontStyle.Normal));
            Assert.That(faceSingle.Family, Is.EqualTo("My Font"));
        }

        [Test]
        public void Weight_and_style_propagate() {
            var face = FontResolver.Resolve(new FontHandle("sans-serif", 16, 700, FontStyle.Italic));
            Assert.That(face.Weight, Is.EqualTo(700));
            Assert.That(face.StyleFlags, Is.EqualTo(FaceInfo.StyleItalic));
        }

        [Test]
        public void Default_weight_normalizes_to_400() {
            var face = FontResolver.Resolve(new FontHandle("sans-serif", 16, 0, FontStyle.Normal));
            Assert.That(face.Weight, Is.EqualTo(400));
        }

        [Test]
        public void TryResolve_returns_false_for_unknown_with_no_default() {
            FontResolver.SetSystemDefaults(new Dictionary<string, string>());
            FontResolver.ClearRegistered();
            Assert.That(FontResolver.TryResolve("Nope", out _), Is.False);
        }

        [Test]
        public void Empty_family_falls_back_to_default() {
            var face = FontResolver.Resolve(new FontHandle("", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("sans-serif"));
        }

        [Test]
        public void Unregister_removes_family() {
            FontResolver.RegisterFont("Inter", "/fonts/Inter.ttf");
            FontResolver.UnregisterFont("Inter");
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("sans-serif"));
        }
    }
}
