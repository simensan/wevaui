using System;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace TestRunner {
    static class Program {
        static int Main(string[] args) {
            if (args.Length > 0 && args[0] == "--debug-snap") { DebugSnap.Run(); return 0; }
            if (args.Length > 0 && args[0] == "--debug-sticky") { DebugSticky.Run(); return 0; }
            if (args.Length > 0 && args[0] == "--debug-anchor") { DebugAnchor.Run(); return 0; }
            if (args.Length > 0 && args[0] == "--debug-dash") { DebugDash.Run(); return 0; }
            if (args.Length > 0 && args[0] == "--perf1-probe") { Weva.Tests.Perf.CascadeWarmAllocProbe.RunProbe(); return 0; }
            var asm = typeof(Weva.Tests.Layout.IncrementalLayoutGateTests).Assembly;
            int passed = 0, failed = 0;
            int skipped = 0;
            var failures = new System.Collections.Generic.List<string>();

            // `--explicit` opts the [Explicit] alloc/perf benchmarks INTO the run
            // (they're off the default pass because their alloc/timing numbers are
            // environment-sensitive). [Ignore] tests still stay skipped.
            bool runExplicit = System.Array.IndexOf(args, "--explicit") >= 0;
            string filter = null;
            foreach (var a in args) {
                if (a != "--explicit" && !a.StartsWith("--")) { filter = a; break; }
            }

            foreach (var t in asm.GetTypes()) {
                if (t.Namespace == null) continue;
                // AR2: no more namespace ALLOW-list — it was the second half
                // of the 0/0/0 trap (a compiled test type whose namespace
                // nobody remembered to add here ran zero tests silently).
                // Everything the csproj compiles under Weva.Tests.* runs.
                if (!t.Namespace.StartsWith("Weva.Tests")) continue;
                if (filter != null && !t.Name.Contains(filter)) continue;
                bool typeSkipped = t.GetCustomAttribute<IgnoreAttribute>() != null
                    || (!runExplicit && t.GetCustomAttribute<ExplicitAttribute>() != null);
                int tPassed = 0, tFailed = 0;
                int tSkipped = 0;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public)) {
                    // AR2: enumerate ALL invocations for the method — a bare
                    // [Test], [TestCase(...)] (one per attribute), and
                    // [TestCaseSource]. The old runner reflected only [Test],
                    // so parameterised cases ran NOWHERE while their file
                    // "looked covered" in the counts.
                    var cases = CollectCases(t, m);
                    if (cases == null) continue; // not a test method
                    if (typeSkipped
                        || m.GetCustomAttribute<IgnoreAttribute>() != null
                        || (!runExplicit && m.GetCustomAttribute<ExplicitAttribute>() != null)) {
                        skipped += cases.Count == 0 ? 1 : cases.Count;
                        tSkipped += cases.Count == 0 ? 1 : cases.Count;
                        continue;
                    }
                    foreach (var (caseArgs, expected, hasExpected, label) in cases) {
                        object instance;
                        try { instance = Activator.CreateInstance(t); }
                        catch (Exception ex) { failed++; tFailed++; failures.Add($"{t.Name}.{label}: ctor: {ex.Message}"); continue; }
                        ResetNUnitContext();
                        try {
                            foreach (var setup in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                                if (setup.GetCustomAttribute<SetUpAttribute>() != null) setup.Invoke(instance, null);
                            }
                            object result = m.Invoke(instance, caseArgs);
                            if (hasExpected && !Equals(result, expected)) {
                                throw new TargetInvocationException(new Exception(
                                    $"ExpectedResult {expected ?? "null"} but was {result ?? "null"}"));
                            }
                            passed++; tPassed++;
                        } catch (TargetInvocationException tie) {
                            failed++; tFailed++;
                            var ex = tie.InnerException ?? tie;
                            failures.Add($"{t.Name}.{label}: {ex.GetType().Name}: {ex.Message}");
                        } catch (Exception ex) {
                            failed++; tFailed++;
                            failures.Add($"{t.Name}.{label}: {ex.GetType().Name}: {ex.Message}");
                        } finally {
                            foreach (var teardown in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                                if (teardown.GetCustomAttribute<TearDownAttribute>() == null) continue;
                                try { teardown.Invoke(instance, null); } catch { }
                            }
                        }
                    }
                }
                if (tPassed + tFailed + tSkipped > 0) Console.WriteLine($"{t.Name}: {tPassed} passed, {tFailed} failed, {tSkipped} skipped");
            }

            Console.WriteLine();
            Console.WriteLine($"TOTAL: {passed} passed, {failed} failed, {skipped} skipped");
            foreach (var f in failures) Console.WriteLine("  FAIL: " + f);
            return failed == 0 ? 0 : 1;
        }

        // AR2: enumerate the invocations a test method requires. Returns null
        // for non-test methods; an empty list only for [TestCaseSource] whose
        // source yielded nothing. Each entry: (args, expectedResult,
        // hasExpectedResult, display label).
        static System.Collections.Generic.List<(object[] args, object expected, bool hasExpected, string label)>
            CollectCases(Type t, MethodInfo m) {
            var cases = new System.Collections.Generic.List<(object[], object, bool, string)>();
            bool isTest = false;
            foreach (var tc in m.GetCustomAttributes<TestCaseAttribute>()) {
                isTest = true;
                string label = m.Name + "(" + string.Join(", ", Array.ConvertAll(tc.Arguments, a => a?.ToString() ?? "null")) + ")";
                cases.Add((CoerceArgs(m, tc.Arguments), tc.ExpectedResult, tc.HasExpectedResult, label));
            }
            foreach (var tcs in m.GetCustomAttributes<TestCaseSourceAttribute>()) {
                isTest = true;
                var sourceType = tcs.SourceType ?? t;
                var member = (MemberInfo)sourceType.GetProperty(tcs.SourceName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? (MemberInfo)sourceType.GetField(tcs.SourceName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? sourceType.GetMethod(tcs.SourceName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object src = member switch {
                    PropertyInfo pi => pi.GetValue(null),
                    FieldInfo fi => fi.GetValue(null),
                    MethodInfo mi => mi.Invoke(null, null),
                    _ => null,
                };
                if (src is System.Collections.IEnumerable seq) {
                    int i = 0;
                    foreach (var item in seq) {
                        object[] args;
                        object expected = null;
                        bool hasExpected = false;
                        if (item is TestCaseData tcd) {
                            args = tcd.Arguments;
                            hasExpected = tcd.HasExpectedResult;
                            expected = tcd.HasExpectedResult ? tcd.ExpectedResult : null;
                        } else if (item is object[] arr) {
                            args = arr;
                        } else {
                            args = new[] { item };
                        }
                        cases.Add((CoerceArgs(m, args), expected, hasExpected, $"{m.Name}[src {i}]"));
                        i++;
                    }
                }
            }
            if (m.GetCustomAttribute<TestAttribute>() != null && m.GetParameters().Length == 0) {
                isTest = true;
                // A parameterless [Test] (possibly alongside nothing else).
                if (cases.Count == 0) cases.Add((null, null, false, m.Name));
            }
            return isTest ? cases : null;
        }

        // NUnit stores TestCase arguments as the literal attribute values —
        // an int literal for a double parameter arrives as boxed int and
        // MethodInfo.Invoke throws. Coerce primitives to the parameter types.
        static object[] CoerceArgs(MethodInfo m, object[] args) {
            if (args == null) return null;
            var ps = m.GetParameters();
            var result = new object[args.Length];
            for (int i = 0; i < args.Length; i++) {
                var a = args[i];
                if (a != null && i < ps.Length) {
                    var pt = ps[i].ParameterType;
                    if (pt != a.GetType() && a is IConvertible && pt.IsPrimitive) {
                        try { a = Convert.ChangeType(a, pt); } catch { }
                    }
                }
                result[i] = a;
            }
            return result;
        }

        // NUnit's TestExecutionContext.CurrentResult accumulates assertion failures
        // (and stays in MultipleAssertLevel > 0) across reflection-based runs. Reset
        // both per-test so failure messages don't leak across method invocations.
        static void ResetNUnitContext() {
            var ctx = TestExecutionContext.CurrentContext;
            if (ctx == null) return;
            try {
                var resultProp = typeof(TestExecutionContext).GetProperty("CurrentResult",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (resultProp != null) {
                    var fresh = new TestCaseResult(new TestMethod(new MethodWrapper(typeof(Program),
                        typeof(Program).GetMethod(nameof(Main),
                            BindingFlags.Static | BindingFlags.NonPublic))));
                    resultProp.SetValue(ctx, fresh);
                }
            } catch { }
            try {
                var f = typeof(TestExecutionContext).GetField("_multipleAssertLevel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null) f.SetValue(ctx, 0);
            } catch { }
        }
    }
}
