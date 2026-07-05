using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Layout.Text;
using Weva.Paint;

namespace Weva.Tests.Documents {
    // Audit TX3: the caret/selection hook measured with the base
    // IFontMetrics overload while the value text renders weight/style-aware
    // (bold routes to a variant face with DIFFERENT advances). On an
    // `input { font-weight: 700 }` the painted caret drifted from the glyph
    // boundary by px-per-char — cumulatively visible by mid-string.
    public class InputCaretStyledMeasureTests {
        [Test]
        public void Caret_measures_with_the_styled_face_TX3() {
            var state = new UIDocumentBuilder {
                DocumentSource = "<input id=\"i\" value=\"MMMM\">",
                StylesheetSources = new List<string> { "input { font-weight: 700; }" },
                MediaContext = MediaContext.Default(400, 300),
                FontMetricsOverride = new InterFontMetrics(),
            }.Build();
            var input = state.Doc.GetElementById("i");
            state.Events.Focus(input);
            Assert.That(state.FormControls.TryGet(input, out var ic), Is.True, "registry wired the input");
            ic.Model.SetCaret(4);

            var geom = state.Painter.InputCaretOf(input);
            Assert.That(geom.HasValue, Is.True, "focused input must produce caret geometry");

            var m = new InterFontMetrics();
            const double fs = 16; // UA default font-size
            double boldX = m.Measure("MMMM", 0, 4, fs, "x", FontStyle.Normal, 700);
            double regularX = m.Measure("MMMM", 0, 4, fs);
            Assert.That(boldX, Is.Not.EqualTo(regularX).Within(0.001),
                "fixture sanity: Inter bold advances must differ from regular for 'M'");
            Assert.That(geom.Value.CaretX, Is.EqualTo(boldX).Within(0.01),
                "caret X must be measured with the face the value RENDERS with — the " +
                "weight-blind measure put it at the regular-face position (audit TX3)");
        }

        [Test]
        public void Caret_on_regular_weight_input_is_unchanged_TX3() {
            var state = new UIDocumentBuilder {
                DocumentSource = "<input id=\"i\" value=\"MMMM\">",
                StylesheetSources = new List<string>(),
                MediaContext = MediaContext.Default(400, 300),
                FontMetricsOverride = new InterFontMetrics(),
            }.Build();
            var input = state.Doc.GetElementById("i");
            state.Events.Focus(input);
            state.FormControls.TryGet(input, out var ic);
            ic.Model.SetCaret(4);
            var geom = state.Painter.InputCaretOf(input);
            var m = new InterFontMetrics();
            Assert.That(geom.Value.CaretX, Is.EqualTo(m.Measure("MMMM", 0, 4, 16)).Within(0.01));
        }
    }
}
