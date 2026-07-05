using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Weva.Generators;

namespace Weva.Tests.Generators {
    public class UIBindGeneratorSmokeTests {
        [Test]
        public void Emits_for_partial_type_when_binding_member_is_on_later_declaration() {
            var generated = RunGenerator(
                "A_Controller.cs",
                @"
namespace Demo {
    public partial class Controller {
    }
}",
                "B_Controller.Bindings.cs",
                @"
using Weva.Binding;

namespace Demo {
    public partial class Controller {
        [UIBind] public string Title;
    }
}",
                "Stubs.cs",
                @"
using System;
using System.Collections.Generic;

namespace Weva.Binding {
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class UIBindAttribute : Attribute {}

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class UIElementAttribute : Attribute {
        public UIElementAttribute(string id) { Id = id; }
        public string Id { get; }
    }

    public static class BindingResolver {
        public static void RegisterAccessorFactory(Type controllerType, Func<object, Weva.Binding.Generated.IBindingAccessor> factory) {}
    }
}

namespace Weva.Binding.Generated {
    public readonly struct ElementBindingDescriptor {
        public ElementBindingDescriptor(string id, Type elementType) {
            Id = id;
            ElementType = elementType;
        }
        public string Id { get; }
        public Type ElementType { get; }
    }

    public interface IBindingAccessor {
        IReadOnlyList<string> BoundMemberNames { get; }
        IReadOnlyList<ElementBindingDescriptor> ElementBindings { get; }
        bool TryGet(string memberName, out object value);
        bool TrySet(string memberName, object value);
        bool TrySetElement(string id, object element);
    }
}");

            StringAssert.Contains("partial class Controller", generated);
            StringAssert.Contains("case \"Title\"", generated);
        }

        static string RunGenerator(params string[] pathAndSourcePairs) {
            var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp10);
            var trees = new List<SyntaxTree>();
            for (int i = 0; i < pathAndSourcePairs.Length; i += 2) {
                trees.Add(CSharpSyntaxTree.ParseText(pathAndSourcePairs[i + 1], parseOptions, pathAndSourcePairs[i]));
            }

            var references = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location) || !seen.Add(asm.Location)) continue;
                references.Add(MetadataReference.CreateFromFile(asm.Location));
            }

            var compilation = CSharpCompilation.Create(
                "WevaGeneratorSmoke",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(
                new ISourceGenerator[] { new UIBindGenerator().AsSourceGenerator() },
                parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
            AssertNoErrors(diagnostics);
            AssertNoErrors(outputCompilation.GetDiagnostics());

            var generatedTrees = outputCompilation.SyntaxTrees.Skip(trees.Count).ToArray();
            Assert.That(generatedTrees, Is.Not.Empty);
            return string.Join("\n", generatedTrees.Select(t => t.ToString()));
        }

        static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics) {
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(e => e.ToString())));
        }
    }
}
