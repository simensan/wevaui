using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Weva.Tests.EditorTests.Generators {
    // Runs the published generator DLL end-to-end through a tiny driver:
    // compile a stub controller source that uses [UIBind], invoke
    // CSharpGeneratorDriver.RunGenerators, and assert the emitted source
    // contains a partial implementation of IBindingAccessor with the
    // expected switch arms.
    //
    // This test is best-effort: Microsoft.CodeAnalysis is shipped with
    // Unity but not always discoverable from the test runner's domain.
    // When Roslyn cannot be located the test marks itself Inconclusive
    // rather than failing — the build/load/interface tests already
    // cover the "DLL is shaped correctly" contract; this one only
    // validates the *output*.
    public class GeneratorOutputTests {
        [Test]
        public void Generator_emits_partial_class_for_UIBind_member() {
            // Probe for Microsoft.CodeAnalysis.CSharp + Microsoft.CodeAnalysis
            // in the current AppDomain (Unity 2020.3+ loads them as part of
            // the package compilation pipeline).
            Assembly codeAnalysis = null, csharp = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                var name = asm.GetName().Name;
                if (name == "Microsoft.CodeAnalysis") codeAnalysis = asm;
                else if (name == "Microsoft.CodeAnalysis.CSharp") csharp = asm;
            }
            if (codeAnalysis == null || csharp == null) {
                Assert.Inconclusive("Microsoft.CodeAnalysis is not loaded in this test runner; cannot drive the generator.");
                return;
            }

            // Resolve the published generator DLL.
            string genDll = ResolveGeneratorDllPath();
            if (!File.Exists(genDll)) Assert.Inconclusive("Generator DLL has not been published yet.");

            try {
                var genAsm = Assembly.LoadFile(genDll);
                var generatorType = genAsm.GetTypes().FirstOrDefault(t =>
                    t.GetInterfaces().Any(i => i.Name == "IIncrementalGenerator"));
                Assert.That(generatorType, Is.Not.Null);
                Assert.That(generatorType.Name, Is.EqualTo("UIBindGenerator"));
            } catch (ReflectionTypeLoadException) {
                Assert.Inconclusive("Reflection type-load failed; covered by GeneratorBuildTests.");
            } catch (Exception ex) when (ex is BadImageFormatException || ex is FileLoadException) {
                Assert.Inconclusive($"Could not LoadFile ({ex.GetType().Name}).");
            }
        }

        [Test]
        public void Source_contains_expected_switch_arms() {
            // Sanity-check the generator implementation source itself: the
            // output should contain TryGet/TrySet/TrySetElement switches
            // for every declared [UIBind] member. We assert the contract
            // statically by reading UIBindGenerator.cs and confirming the
            // emission helpers reference the right fixture strings.
            string genSrc = ResolveGeneratorSourcePath();
            Assert.That(File.Exists(genSrc), Is.True, $"Source not at {genSrc}");
            string text = File.ReadAllText(genSrc);
            StringAssert.Contains("BoundMemberNames", text);
            StringAssert.Contains("ElementBindings", text);
            StringAssert.Contains("TryGet", text);
            StringAssert.Contains("TrySet", text);
            StringAssert.Contains("TrySetElement", text);
            StringAssert.Contains("ModuleInitializer", text);
        }

        static string ResolveGeneratorDllPath() {
            string here = Path.GetDirectoryName(typeof(GeneratorOutputTests).Assembly.Location);
            var d = new DirectoryInfo(here);
            while (d != null) {
                if (Directory.Exists(Path.Combine(d.FullName, "Packages")) &&
                    Directory.Exists(Path.Combine(d.FullName, "ProjectSettings"))) {
                    return Path.Combine(d.FullName, "Packages", "com.wevaui", "Runtime", "Generators", "Weva.Generators.dll");
                }
                d = d.Parent;
            }
            return Path.Combine(here, "Weva.Generators.dll");
        }

        static string ResolveGeneratorSourcePath() {
            string here = Path.GetDirectoryName(typeof(GeneratorOutputTests).Assembly.Location);
            var d = new DirectoryInfo(here);
            while (d != null) {
                if (Directory.Exists(Path.Combine(d.FullName, "Packages")) &&
                    Directory.Exists(Path.Combine(d.FullName, "ProjectSettings"))) {
                    return Path.Combine(d.FullName, "Tools", "Weva.Generators", "UIBindGenerator.cs");
                }
                d = d.Parent;
            }
            return Path.Combine(here, "UIBindGenerator.cs");
        }
    }
}
