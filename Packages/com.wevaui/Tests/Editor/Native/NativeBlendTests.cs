// ABI minor 33: mix-blend-mode arrives on the draws, and the renderer keeps
// a blended run apart from the normal ones.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeBlendTests
    {
        [Test]
        public void Draws_CarryTheBlendMode()
        {
            using (var doc = new NativeDocument(400, 300))
            {
                doc.LoadHtml("<div id=\"m\"></div><div id=\"n\"></div>");
                doc.SetCss("body{margin:0}div{width:100px;height:20px}#m{mix-blend-mode:multiply;background:rgb(255,0,0)}#n{background:rgb(0,0,255)}");
                doc.Update(0);
                int multiplied = 0, normal = 0;
                foreach (weva_draw d in doc.Draws())
                {
                    if (d.vertex_count < 3) continue;
                    if (d.blend_mode == (int)weva_blend_mode.WEVA_BLEND_MULTIPLY) multiplied++;
                    if (d.blend_mode == (int)weva_blend_mode.WEVA_BLEND_NORMAL) normal++;
                }
                Assert.That(multiplied, Is.GreaterThanOrEqualTo(1));
                Assert.That(normal, Is.GreaterThanOrEqualTo(1));
            }
        }
    }
}
