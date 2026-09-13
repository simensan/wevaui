// The C# 9 [ModuleInitializer] attribute is recognized by the compiler
// based on type name and namespace; it does not need to live in any
// specific assembly. Unity's .NET Standard 2.1 reference assemblies do not
// include this type, so we provide it ourselves. Guarded with a #if so
// that future Unity .NET 5+ runtimes that ship the real attribute do not
// see a duplicate.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices {
    [System.AttributeUsage(System.AttributeTargets.Method, Inherited = false)]
    // internal, not public: this injects a BCL type name into a System.*
    // namespace from an assembly with autoReferenced: true, so a consumer
    // who polyfills the same attribute -- a common pattern -- would get
    // CS0433 ambiguity. The compiler recognises a module initializer
    // through an internal attribute just as well, within this assembly.
    internal sealed class ModuleInitializerAttribute : System.Attribute {
    }
}
#endif
