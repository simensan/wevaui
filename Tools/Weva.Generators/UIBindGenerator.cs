// UIBindGenerator
// ================
// Roslyn incremental source generator that emits an IBindingAccessor
// implementation for every type that declares one or more
// [UIBind] fields/properties or [UIElement(id)] fields. Decision locked in
// PLAN.md Section 9 row 5 ("Data binding uses C# source generators").
//
// Edge cases handled (and surfaced as Roslyn diagnostics):
//   - Sealed class with [UIBind]: cannot be made `partial` retroactively from
//     the user's perspective if the user wrote `sealed class` without
//     `partial`. We still generate a partial: Roslyn allows `partial sealed`
//     so as long as the user marked the type `partial` we are fine. If the
//     type is NOT marked partial we emit a *separate* accessor class
//     `<TypeName>_BindingAccessor` and register it via [ModuleInitializer]
//     into BindingResolver.RegisterAccessor.
//   - Generic class with [UIBind]: skipped with diagnostic UIB002. v2 work.
//   - Nested class: emitted using the fully-qualified containing-type chain.
//   - Inheritance: the generator only emits for the type that DECLARES the
//     [UIBind] member. The runtime reflection fallback handles members that
//     live on a base type which has no generated accessor of its own.
//   - Static [UIBind]: skipped with diagnostic UIB003.
//
// Output:
//   <ContainingNamespace>.<TypeName>_Bindings.g.cs
// containing either:
//   partial class TypeName : Weva.Binding.Generated.IBindingAccessor { ... }
// or, when the user's declaration is not partial:
//   internal sealed class TypeName_BindingAccessor : Weva.Binding.Generated.IBindingAccessor { ... }
//   internal static class TypeName_BindingAccessor_ModuleInit { [ModuleInitializer] ... }
//
// The runtime side is BindingResolver.GetAccessor / RegisterAccessor.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Weva.Generators {
    [Generator]
    public sealed class UIBindGenerator : IIncrementalGenerator {
        const string UIBindAttributeFullName = "Weva.Binding.UIBindAttribute";
        const string UIElementAttributeFullName = "Weva.Binding.UIElementAttribute";
        const string AccessorInterfaceFullName = "Weva.Binding.Generated.IBindingAccessor";
        const string ElementDescriptorFullName = "Weva.Binding.Generated.ElementBindingDescriptor";
        const string ResolverFullName = "Weva.Binding.BindingResolver";

        static readonly DiagnosticDescriptor SealedNonPartialInfo = new(
            id: "UIB001",
            title: "Type is not partial; using separate accessor class",
            messageFormat: "Type '{0}' is not declared partial. UIBindGenerator emitted a separate '{0}_BindingAccessor' class registered via [ModuleInitializer]. Mark '{0}' as partial to embed the accessor in the type itself.",
            category: "Weva.Binding",
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        static readonly DiagnosticDescriptor GenericTypeSkipped = new(
            id: "UIB002",
            title: "Generic types are not yet supported by UIBindGenerator",
            messageFormat: "Type '{0}' is generic. UIBindGenerator skipped accessor generation; the binding runtime will fall back to reflection.",
            category: "Weva.Binding",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        static readonly DiagnosticDescriptor StaticMemberSkipped = new(
            id: "UIB003",
            title: "[UIBind] on a static member is unsupported",
            messageFormat: "Member '{0}.{1}' is static; UIBindGenerator only supports instance members. Skipping.",
            category: "Weva.Binding",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        static readonly DiagnosticDescriptor PropertyWithoutAccessorSkipped = new(
            id: "UIB004",
            title: "[UIBind] property must have a getter",
            messageFormat: "Property '{0}.{1}' has no accessible getter; UIBindGenerator skipped it.",
            category: "Weva.Binding",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        static readonly DiagnosticDescriptor IndexerSkipped = new(
            id: "UIB005",
            title: "[UIBind] is not allowed on indexers",
            messageFormat: "Indexer on '{0}' is marked [UIBind] but indexers cannot be bound; skipping.",
            category: "Weva.Binding",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context) {
            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
                    transform: static (ctx, _) => Extract(ctx))
                .Where(static x => x is not null)
                .Select(static (x, _) => x!);

            context.RegisterSourceOutput(candidates, static (spc, model) => Emit(spc, model));
        }

        // --- Phase 1: cheap syntactic filter ---
        static bool IsCandidate(SyntaxNode node) {
            if (node is not TypeDeclarationSyntax typeDecl) return false;
            if (typeDecl is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)) return false;
            return HasInterestingMember(typeDecl);
        }

        static bool HasInterestingAttribute(MemberDeclarationSyntax member) {
            if (member is not (FieldDeclarationSyntax or PropertyDeclarationSyntax)) return false;
            foreach (var list in member.AttributeLists) {
                foreach (var attr in list.Attributes) {
                    var name = attr.Name.ToString();
                    var simple = SimpleAttributeName(name);
                    if (simple is "UIBind" or "UIBindAttribute" or "UIElement" or "UIElementAttribute") return true;
                }
            }
            return false;
        }

        static string SimpleAttributeName(string raw) {
            // Accept "Weva.Binding.UIBind", "UIBind", "UIBindAttribute", etc.
            int dot = raw.LastIndexOf('.');
            return dot < 0 ? raw : raw.Substring(dot + 1);
        }

        // --- Phase 2: semantic extraction ---
        static AccessorModel? Extract(GeneratorSyntaxContext ctx) {
            var typeDecl = (TypeDeclarationSyntax)ctx.Node;
            var symbol = ctx.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
            if (symbol == null) return null;

            // Only generate once per type even if it has multiple partial parts.
            // The syntax provider only visits declarations that contain a
            // binding attribute, so the elected declaration must be chosen
            // from that candidate subset rather than from every partial part.
            if (!IsPrimaryCandidateDeclaration(symbol, typeDecl)) return null;

            var bindMembers = new List<BindMember>();
            var elementFields = new List<ElementField>();
            var diagnostics = new List<DiagnosticInfo>();

            bool isGeneric = symbol.IsGenericType || symbol.TypeParameters.Length > 0;

            foreach (var member in symbol.GetMembers()) {
                bool hasUIBind = HasAttribute(member, UIBindAttributeFullName);
                bool hasUIElement = HasAttribute(member, UIElementAttributeFullName);
                if (!hasUIBind && !hasUIElement) continue;

                if (member.IsStatic) {
                    diagnostics.Add(new DiagnosticInfo(StaticMemberSkipped, member.Locations.FirstOrDefault(), symbol.Name, member.Name));
                    continue;
                }

                if (member is IFieldSymbol field) {
                    if (hasUIBind) bindMembers.Add(new BindMember(field.Name, field.Type, isField: true, isWritable: !field.IsReadOnly && !field.IsConst));
                    if (hasUIElement) {
                        var id = ReadElementId(member);
                        if (!string.IsNullOrEmpty(id)) {
                            elementFields.Add(new ElementField(id!, field.Name, field.Type));
                        }
                    }
                } else if (member is IPropertySymbol prop) {
                    if (prop.IsIndexer) {
                        diagnostics.Add(new DiagnosticInfo(IndexerSkipped, member.Locations.FirstOrDefault(), symbol.Name, member.Name));
                        continue;
                    }
                    if (hasUIBind) {
                        if (prop.GetMethod == null) {
                            diagnostics.Add(new DiagnosticInfo(PropertyWithoutAccessorSkipped, member.Locations.FirstOrDefault(), symbol.Name, member.Name));
                            continue;
                        }
                        bindMembers.Add(new BindMember(prop.Name, prop.Type, isField: false, isWritable: prop.SetMethod != null));
                    }
                    // [UIElement] is field-only by attribute targeting; ignore on properties.
                }
            }

            if (bindMembers.Count == 0 && elementFields.Count == 0 && diagnostics.Count == 0) {
                return null;
            }

            if (isGeneric) {
                return new AccessorModel(symbol, typeDecl, bindMembers, elementFields, diagnostics, isGeneric: true);
            }

            return new AccessorModel(symbol, typeDecl, bindMembers, elementFields, diagnostics, isGeneric: false);
        }

        static bool IsPrimaryCandidateDeclaration(INamedTypeSymbol symbol, TypeDeclarationSyntax decl) {
            var refs = symbol.DeclaringSyntaxReferences;
            if (refs.Length <= 1) return true;

            // Pick the first partial declaration that actually contains a
            // [UIBind] or [UIElement] member. A non-attributed declaration
            // may appear first in source order, but it never reaches the
            // syntax-provider transform.
            SyntaxReference? best = null;
            for (int i = 0; i < refs.Length; i++) {
                var r = refs[i];
                if (r.GetSyntax() is not TypeDeclarationSyntax candidate || !HasInterestingMember(candidate)) {
                    continue;
                }
                if (best == null) { best = r; continue; }
                int cmp = string.CompareOrdinal(r.SyntaxTree.FilePath, best.SyntaxTree.FilePath);
                if (cmp < 0 || (cmp == 0 && r.Span.Start < best.Span.Start)) best = r;
            }
            if (best == null) return true;
            return best.SyntaxTree == decl.SyntaxTree && best.Span.Start == decl.Span.Start;
        }

        static bool HasInterestingMember(TypeDeclarationSyntax typeDecl) {
            foreach (var member in typeDecl.Members) {
                if (HasInterestingAttribute(member)) return true;
            }
            return false;
        }

        static bool HasAttribute(ISymbol symbol, string fullName) {
            foreach (var a in symbol.GetAttributes()) {
                var ac = a.AttributeClass;
                if (ac == null) continue;
                if (ac.ToDisplayString() == fullName) return true;
            }
            return false;
        }

        static string? ReadElementId(ISymbol member) {
            foreach (var a in member.GetAttributes()) {
                var ac = a.AttributeClass;
                if (ac == null) continue;
                if (ac.ToDisplayString() != UIElementAttributeFullName) continue;
                if (a.ConstructorArguments.Length >= 1) {
                    var v = a.ConstructorArguments[0].Value;
                    return v as string;
                }
                foreach (var na in a.NamedArguments) {
                    if (na.Key == "Id") return na.Value.Value as string;
                }
            }
            return null;
        }

        // --- Phase 3: emit ---
        static void Emit(SourceProductionContext spc, AccessorModel model) {
            foreach (var d in model.Diagnostics) {
                spc.ReportDiagnostic(d.ToDiagnostic());
            }

            if (model.IsGeneric) {
                spc.ReportDiagnostic(Diagnostic.Create(GenericTypeSkipped, model.TypeSymbol.Locations.FirstOrDefault(), model.TypeSymbol.Name));
                return;
            }

            var symbol = model.TypeSymbol;
            var decl = model.PrimaryDeclaration;

            bool isPartial = decl.Modifiers.Any(SyntaxKind.PartialKeyword);
            bool isNested = symbol.ContainingType != null;
            bool canEmitPartial = isPartial && !isNested; // For nested types, the containing types must all be partial too - we conservatively use the standalone path when nested.
            if (isNested && AllContainingTypesPartial(symbol)) canEmitPartial = isPartial;

            string fileHint = BuildFileHint(symbol);

            if (canEmitPartial) {
                var src = EmitPartialAccessor(model);
                spc.AddSource(fileHint, SourceText(src));
            } else {
                spc.ReportDiagnostic(Diagnostic.Create(SealedNonPartialInfo, symbol.Locations.FirstOrDefault(), symbol.Name));
                var src = EmitStandaloneAccessor(model);
                spc.AddSource(fileHint, SourceText(src));
            }
        }

        static bool AllContainingTypesPartial(INamedTypeSymbol symbol) {
            for (var ct = symbol.ContainingType; ct != null; ct = ct.ContainingType) {
                bool any = false;
                foreach (var r in ct.DeclaringSyntaxReferences) {
                    if (r.GetSyntax() is TypeDeclarationSyntax tds && tds.Modifiers.Any(SyntaxKind.PartialKeyword)) {
                        any = true;
                        break;
                    }
                }
                if (!any) return false;
            }
            return true;
        }

        static string BuildFileHint(INamedTypeSymbol symbol) {
            var sb = new StringBuilder();
            for (var ct = symbol.ContainingType; ct != null; ct = ct.ContainingType) {
                sb.Insert(0, ct.Name + "_");
            }
            sb.Append(symbol.Name);
            sb.Append("_Bindings.g.cs");
            // Prefix with namespace to avoid collisions across the project.
            var ns = symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? "Global"
                : symbol.ContainingNamespace?.ToDisplayString().Replace('.', '_');
            return $"{ns}_{sb}";
        }

        static Microsoft.CodeAnalysis.Text.SourceText SourceText(string src) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(src, Encoding.UTF8);

        // --- Emission: partial path ---
        static string EmitPartialAccessor(AccessorModel model) {
            var w = new Writer();
            EmitFileHeader(w);
            var ns = model.TypeSymbol.ContainingNamespace;
            bool hasNs = ns != null && !ns.IsGlobalNamespace;
            if (hasNs) {
                w.Line($"namespace {ns!.ToDisplayString()} {{");
                w.Indent();
            }

            // Walk containing-type chain so nested types get correctly wrapped.
            var containingTypes = new Stack<INamedTypeSymbol>();
            for (var ct = model.TypeSymbol.ContainingType; ct != null; ct = ct.ContainingType) {
                containingTypes.Push(ct);
            }
            foreach (var ct in containingTypes) {
                var kind = ct.IsRecord ? "record" : (ct.TypeKind == TypeKind.Struct ? "struct" : "class");
                w.Line($"partial {kind} {ct.Name} {{");
                w.Indent();
            }

            EmitClassBody(w, model, isPartial: true);

            foreach (var _ in containingTypes) {
                w.Outdent();
                w.Line("}");
            }
            if (hasNs) {
                w.Outdent();
                w.Line("}");
            }
            return w.ToString();
        }

        // --- Emission: standalone path (sealed/non-partial types) ---
        static string EmitStandaloneAccessor(AccessorModel model) {
            var w = new Writer();
            EmitFileHeader(w);

            var ns = model.TypeSymbol.ContainingNamespace;
            bool hasNs = ns != null && !ns.IsGlobalNamespace;
            if (hasNs) {
                w.Line($"namespace {ns!.ToDisplayString()} {{");
                w.Indent();
            }

            string accessorTypeName = model.TypeSymbol.Name + "_BindingAccessor";
            string controllerType = model.TypeSymbol.ToDisplayString();

            w.Line($"internal sealed class {accessorTypeName} : {AccessorInterfaceFullName} {{");
            w.Indent();
            w.Line($"readonly {controllerType} target;");
            w.Line($"public {accessorTypeName}({controllerType} target) {{ this.target = target; }}");
            EmitAccessorMembers(w, model, instanceExpression: "target");
            w.Outdent();
            w.Line("}");

            // Module initializer registers a *type-level* template: the
            // runtime treats the type as having an accessor and asks the
            // accessor itself to operate on the per-instance target. Since
            // the standalone path requires a target instance, we register a
            // proxy that adapts.
            // The simplest approach: register a "factory" accessor whose
            // TryGet/TrySet/etc. throw if used directly — the runtime is
            // expected to detect non-partial types and call a factory. To
            // keep BindingResolver simple, we instead require that the
            // application explicitly passes the wrapped accessor. For v1 we
            // emit an extension factory method and a [ModuleInitializer]
            // that registers a "marker" instance whose BoundMemberNames is
            // the source of truth — the runtime falls back to reflection
            // for the actual reads on standalone-only types. Document this
            // limitation and prefer partial classes.
            // (See README in this folder.)

            // [ModuleInitializer] is required to register the factory when
            // the user's type cannot host an accessor itself. We polyfill
            // ModuleInitializerAttribute under netstandard2.0 if it isn't
            // already declared.
            w.Line($"internal static class {accessorTypeName}_ModuleInit {{");
            w.Indent();
            w.Line("[System.Runtime.CompilerServices.ModuleInitializer]");
            w.Line($"internal static void Init() {{");
            w.Indent();
            w.Line($"{ResolverFullName}.RegisterAccessorFactory(typeof({controllerType}), static obj => new {accessorTypeName}(({controllerType})obj));");
            w.Outdent();
            w.Line("}");
            w.Outdent();
            w.Line("}");

            if (hasNs) {
                w.Outdent();
                w.Line("}");
            }
            return w.ToString();
        }

        static void EmitFileHeader(Writer w) {
            w.Line("// <auto-generated>");
            w.Line("// Generated by Weva.Generators.UIBindGenerator. Do not edit.");
            w.Line("// </auto-generated>");
            w.Line("#nullable disable");
            w.Line("#pragma warning disable");
            w.Line("using System;");
            w.Line("using System.Collections.Generic;");
            w.Line($"using {NamespaceOf(AccessorInterfaceFullName)};");
            w.Line();
        }

        static string NamespaceOf(string fullTypeName) {
            int idx = fullTypeName.LastIndexOf('.');
            return idx < 0 ? "" : fullTypeName.Substring(0, idx);
        }

        static void EmitClassBody(Writer w, AccessorModel model, bool isPartial) {
            var symbol = model.TypeSymbol;
            string keyword = symbol.IsRecord ? "record" : (symbol.TypeKind == TypeKind.Struct ? "struct" : "class");
            string sealedKw = symbol.IsSealed && !symbol.IsAbstract && symbol.TypeKind != TypeKind.Struct ? "sealed " : "";
            string abstractKw = symbol.IsAbstract && symbol.TypeKind != TypeKind.Struct ? "abstract " : "";

            // Modifier order: 'partial' must come immediately before the type kind.
            w.Line($"{sealedKw}{abstractKw}partial {keyword} {symbol.Name} : {AccessorInterfaceFullName} {{");
            w.Indent();
            EmitAccessorMembers(w, model, instanceExpression: "this");
            w.Outdent();
            w.Line("}");
        }

        static void EmitAccessorMembers(Writer w, AccessorModel model, string instanceExpression) {
            var bindMembers = model.BindMembers;
            var elements = model.ElementFields;

            // Static readonly arrays.
            w.Line($"static readonly string[] __Weva_BoundMemberNames = new string[] {{");
            w.Indent();
            for (int i = 0; i < bindMembers.Count; i++) {
                w.Line($"\"{Escape(bindMembers[i].Name)}\",");
            }
            w.Outdent();
            w.Line("};");
            w.Line($"static readonly {ElementDescriptorFullName}[] __Weva_ElementBindings = new {ElementDescriptorFullName}[] {{");
            w.Indent();
            for (int i = 0; i < elements.Count; i++) {
                var e = elements[i];
                w.Line($"new {ElementDescriptorFullName}(\"{Escape(e.Id)}\", typeof({DisplayType(e.FieldType)})),");
            }
            w.Outdent();
            w.Line("};");
            w.Line();

            // Interface property impls.
            w.Line($"IReadOnlyList<string> {AccessorInterfaceFullName}.BoundMemberNames => __Weva_BoundMemberNames;");
            w.Line($"IReadOnlyList<{ElementDescriptorFullName}> {AccessorInterfaceFullName}.ElementBindings => __Weva_ElementBindings;");
            w.Line();

            // TryGet
            w.Line($"bool {AccessorInterfaceFullName}.TryGet(string memberName, out object value) {{");
            w.Indent();
            w.Line("switch (memberName) {");
            w.Indent();
            for (int i = 0; i < bindMembers.Count; i++) {
                var m = bindMembers[i];
                w.Line($"case \"{Escape(m.Name)}\": value = (object)({instanceExpression}.{m.Name}); return true;");
            }
            w.Line("default: value = null; return false;");
            w.Outdent();
            w.Line("}");
            w.Outdent();
            w.Line("}");
            w.Line();

            // TrySet
            w.Line($"bool {AccessorInterfaceFullName}.TrySet(string memberName, object value) {{");
            w.Indent();
            w.Line("switch (memberName) {");
            w.Indent();
            for (int i = 0; i < bindMembers.Count; i++) {
                var m = bindMembers[i];
                if (!m.IsWritable) {
                    w.Line($"case \"{Escape(m.Name)}\": return false;");
                    continue;
                }
                var typeStr = DisplayType(m.MemberType);
                if (IsValueType(m.MemberType)) {
                    w.Line($"case \"{Escape(m.Name)}\": if (value is {typeStr} __v_{i}) {{ {instanceExpression}.{m.Name} = __v_{i}; return true; }} return false;");
                } else {
                    w.Line($"case \"{Escape(m.Name)}\": if (value is null || value is {typeStr}) {{ {instanceExpression}.{m.Name} = ({typeStr})value; return true; }} return false;");
                }
            }
            w.Line("default: return false;");
            w.Outdent();
            w.Line("}");
            w.Outdent();
            w.Line("}");
            w.Line();

            // TrySetElement
            w.Line($"bool {AccessorInterfaceFullName}.TrySetElement(string id, object element) {{");
            w.Indent();
            w.Line("switch (id) {");
            w.Indent();
            for (int i = 0; i < elements.Count; i++) {
                var e = elements[i];
                var typeStr = DisplayType(e.FieldType);
                w.Line($"case \"{Escape(e.Id)}\": if (element is null || element is {typeStr}) {{ {instanceExpression}.{e.FieldName} = ({typeStr})element; return true; }} return false;");
            }
            w.Line("default: return false;");
            w.Outdent();
            w.Line("}");
            w.Outdent();
            w.Line("}");
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string DisplayType(ITypeSymbol t) =>
            t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        static bool IsValueType(ITypeSymbol t) {
            if (t.IsValueType) return true;
            if (t.TypeKind == TypeKind.TypeParameter) return false;
            return false;
        }

        sealed class Writer {
            readonly StringBuilder sb = new();
            int indent;
            public void Indent() => indent++;
            public void Outdent() {
                if (indent > 0) indent--;
            }
            public void Line() => sb.AppendLine();
            public void Line(string s) {
                for (int i = 0; i < indent; i++) sb.Append("    ");
                sb.AppendLine(s);
            }
            public override string ToString() => sb.ToString();
        }

        readonly struct DiagnosticInfo {
            public readonly DiagnosticDescriptor Descriptor;
            public readonly Location? Location;
            public readonly object?[] Args;
            public DiagnosticInfo(DiagnosticDescriptor d, Location? l, params object?[] args) {
                Descriptor = d;
                Location = l;
                Args = args;
            }
            public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location, Args);
        }

        readonly struct BindMember {
            public readonly string Name;
            public readonly ITypeSymbol MemberType;
            public readonly bool IsField;
            public readonly bool IsWritable;
            public BindMember(string name, ITypeSymbol type, bool isField, bool isWritable) {
                Name = name;
                MemberType = type;
                IsField = isField;
                IsWritable = isWritable;
            }
        }

        readonly struct ElementField {
            public readonly string Id;
            public readonly string FieldName;
            public readonly ITypeSymbol FieldType;
            public ElementField(string id, string fieldName, ITypeSymbol fieldType) {
                Id = id;
                FieldName = fieldName;
                FieldType = fieldType;
            }
        }

        sealed class AccessorModel {
            public readonly INamedTypeSymbol TypeSymbol;
            public readonly TypeDeclarationSyntax PrimaryDeclaration;
            public readonly IReadOnlyList<BindMember> BindMembers;
            public readonly IReadOnlyList<ElementField> ElementFields;
            public readonly IReadOnlyList<DiagnosticInfo> Diagnostics;
            public readonly bool IsGeneric;

            public AccessorModel(
                INamedTypeSymbol typeSymbol,
                TypeDeclarationSyntax primaryDeclaration,
                IReadOnlyList<BindMember> bindMembers,
                IReadOnlyList<ElementField> elementFields,
                IReadOnlyList<DiagnosticInfo> diagnostics,
                bool isGeneric) {
                TypeSymbol = typeSymbol;
                PrimaryDeclaration = primaryDeclaration;
                BindMembers = bindMembers;
                ElementFields = elementFields;
                Diagnostics = diagnostics;
                IsGeneric = isGeneric;
            }
        }
    }
}
