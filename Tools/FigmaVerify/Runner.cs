using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Weva.Figma.Verify
{
    /// <summary>
    /// Headless NUnit runner for the Figma bridge tests. Mirrors the engine's
    /// Tools/TestVerifyAll approach (reflection over [Test] methods) but needs no
    /// UnityEngine stubs because the bridge core is pure C#.
    ///
    /// Usage: dotnet run --project Tools/FigmaVerify [nameFilter]
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            Assembly asm = typeof(Program).Assembly;
            int passed = 0, failed = 0, skipped = 0;
            var failures = new List<string>();

            foreach (Type t in asm.GetTypes())
            {
                if (t.Namespace == null || !t.Namespace.StartsWith("Weva.Figma.Tests")) continue;
                if (filter != null && !t.Name.Contains(filter)) continue;

                bool typeSkipped = t.GetCustomAttribute<IgnoreAttribute>() != null
                                   || t.GetCustomAttribute<ExplicitAttribute>() != null;
                int tPassed = 0, tFailed = 0, tSkipped = 0;

                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.GetCustomAttribute<TestAttribute>() == null) continue;
                    if (typeSkipped
                        || m.GetCustomAttribute<IgnoreAttribute>() != null
                        || m.GetCustomAttribute<ExplicitAttribute>() != null)
                    {
                        skipped++; tSkipped++; continue;
                    }

                    object instance;
                    try { instance = Activator.CreateInstance(t); }
                    catch (Exception ex)
                    {
                        failed++; tFailed++;
                        failures.Add($"{t.Name}.{m.Name}: ctor: {Unwrap(ex).Message}");
                        continue;
                    }

                    try
                    {
                        RunWithAttribute<SetUpAttribute>(t, instance);
                        m.Invoke(instance, null);
                        passed++; tPassed++;
                    }
                    catch (Exception ex)
                    {
                        failed++; tFailed++;
                        Exception inner = Unwrap(ex);
                        failures.Add($"{t.Name}.{m.Name}: {inner.GetType().Name}: {inner.Message}");
                    }
                    finally
                    {
                        try { RunWithAttribute<TearDownAttribute>(t, instance); } catch { }
                    }
                }

                if (tPassed + tFailed + tSkipped > 0)
                    Console.WriteLine($"{t.Name}: {tPassed} passed, {tFailed} failed, {tSkipped} skipped");
            }

            Console.WriteLine();
            Console.WriteLine($"TOTAL: {passed} passed, {failed} failed, {skipped} skipped");
            foreach (string f in failures) Console.WriteLine("  FAIL: " + f);
            return failed == 0 ? 0 : 1;
        }

        static void RunWithAttribute<TAttr>(Type t, object instance) where TAttr : Attribute
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.GetCustomAttribute<TAttr>() != null)
                    m.Invoke(instance, null);
        }

        static Exception Unwrap(Exception ex)
            => ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
    }
}
