using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Weva.Tests.Rendering.Backend {
    // Regression pin for CODE_AUDIT_FINDINGS.md MN5:
    //   The legacy URPRenderBackend caps gradient stops at 8 because the shader uniform
    //   `_WevaGradientStops` is declared `float4[8]` in Weva_Gradient.shader.
    //   The C# side mirrors this with `const int MaxStops = 8;` inside EmitGradient.
    //   The two values MUST move together — if a future edit bumps one without the
    //   other, gradient rendering breaks silently (over-cap stops drop on the shader
    //   side; the throttled K5 warning misfires on the C# side).
    //
    // `MaxStops` is a method-local `const` and is not reflectable, so this test
    // pins the literals via source-text inspection: it reads both source files and
    // asserts each contains the expected `8` dimension token in the load-bearing
    // position. Both call sites carry coupling-comments cross-referencing the other,
    // and this test fails the build the moment those literals drift apart.
    public class URPRenderBackendGradientStopShaderCouplingTests {
        const int ExpectedMaxStops = 8;

        [Test]
        public void Gradient_stop_cap_matches_shader_uniform_dimension_MN5() {
            var csSource = File.ReadAllText(PackagePath("Runtime/Rendering/Backend/URPRenderBackend.cs"));
            var shaderSource = File.ReadAllText(PackagePath("Runtime/Rendering/Shaders/Weva_Gradient.shader"));

            // C# side: `const int MaxStops = 8;` literal inside EmitGradient. Pinning the
            // full declaration (not just `= 8`) so an unrelated `= 8` elsewhere in the
            // file can't satisfy the test.
            var expectedCsDecl = $"const int MaxStops = {ExpectedMaxStops};";
            Assert.That(csSource, Does.Contain(expectedCsDecl),
                "URPRenderBackend.MaxStops declaration drifted from MN5's pinned value. " +
                "If you changed the cap on the C# side, update the shader uniform " +
                "`float4 _WevaGradientStops[N]` in Weva_Gradient.shader AND update " +
                "ExpectedMaxStops in this test to match.");

            // Shader side: `float4 _WevaGradientStops[8];` uniform array dimension.
            var expectedShaderDecl = $"float4 _WevaGradientStops[{ExpectedMaxStops}]";
            Assert.That(shaderSource, Does.Contain(expectedShaderDecl),
                "Weva_Gradient.shader `_WevaGradientStops` array dimension drifted " +
                "from MN5's pinned value. If you changed the cap on the shader side, " +
                "update `const int MaxStops` in URPRenderBackend.cs AND update " +
                "ExpectedMaxStops in this test to match.");

            // Cross-reference comments must also be present so future maintainers find
            // the coupling without grepping for the literal first.
            Assert.That(csSource, Does.Contain("SHADER COUPLING"),
                "URPRenderBackend.cs MN5 cross-reference comment is missing.");
            Assert.That(shaderSource, Does.Contain("C# COUPLING"),
                "Weva_Gradient.shader MN5 cross-reference comment is missing.");
        }

        static string PackagePath(string packageRelativePath) {
            var root = Path.GetDirectoryName(Application.dataPath);
            var path = packageRelativePath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(root, "Packages", "com.wevaui", path);
        }
    }
}
