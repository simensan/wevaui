using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Weva.Native;

if (args.Length != 1) throw new ArgumentException("Pass the expected native library name.");
int count = 0;
foreach (var method in typeof(WevaNative).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
{
    var import = method.GetCustomAttribute<DllImportAttribute>();
    if (import == null) continue;
    if (import.Value != args[0] || import.CallingConvention != CallingConvention.Cdecl)
        throw new InvalidOperationException($"{method.Name}: unexpected import {import.Value} ({import.CallingConvention})");
    foreach (var parameter in method.GetParameters())
        if (parameter.ParameterType.IsFunctionPointer)
            throw new InvalidOperationException($"{method.Name}: IL2CPP cannot marshal function-pointer parameters.");
    count++;
}
if (count == 0) throw new InvalidOperationException("No native imports were checked.");
Console.WriteLine($"{count} native imports use {args[0]} with Cdecl.");
