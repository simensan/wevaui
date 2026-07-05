#if WEVA_ADDRESSABLES
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace Weva.Paint.Images {
    // Production image registry over Unity Addressables. Authors write
    // `<img src="ui/heart">` and the handle matches an Addressable address.
    //
    // Design: poll-based, not async. Earlier revisions used
    // `await op.Task` via reflection — that path deadlocked Unity when
    // multiple keys loaded in parallel (Task continuations not resuming on
    // the main thread reliably through the reflection bridge). This
    // implementation NEVER awaits — TryResolve / Tick drain finished ops
    // synchronously. Robust against the failure modes UniTask's
    // `AsyncOperationHandle` integration normally papers over.
    //
    // Addressables is optional. Compile-time references to
    // Unity.Addressables / Unity.ResourceManager are avoided through
    // reflection because host projects can enable WEVA_ADDRESSABLES
    // before those assemblies are visible to Weva.Runtime. Late binding
    // keeps the package compiling and still uses Addressables when the
    // runtime assembly is present.
    public sealed class AddressablesImageRegistry : IVersionedImageRegistry {
        readonly Dictionary<string, IImageSource> ready = new();
        readonly Dictionary<string, object> handles = new();
        readonly HashSet<string> inFlight = new();
        readonly HashSet<string> failed = new();
        readonly List<PendingLoad> pendingLoads = new();

        struct PendingLoad {
            public string Handle;
            public object Op;
            // Type ladder: Texture2D first (most common — PNGs imported
            // with the default texture type), then Sprite (PNGs imported
            // with Sprite mode), then RenderTexture. NextTypeIndex == 3
            // means we've exhausted the ladder and the key is unresolvable.
            public int NextTypeIndex;
        }

        public int ReadyCount => ready.Count;
        public int InFlightCount => inFlight.Count;
        public int Version { get; private set; }

        public bool TryResolve(string handle, out IImageSource source) {
            // Drain on every resolve so completed ops surface immediately;
            // even when paint is cached and the document isn't repainting,
            // the explicit `Tick()` API below covers the idle case.
            Tick();
            if (string.IsNullOrEmpty(handle)) { source = null; return false; }
            if (ready.TryGetValue(handle, out source)) return source != null;
            if (failed.Contains(handle)) { source = null; return false; }
            if (!inFlight.Contains(handle)) StartLoad(handle);
            source = null;
            return false;
        }

        // Drives the load pipeline. Callers wishing to surface async
        // completions while the rest of the UI is idle (i.e. nothing else
        // is repainting and thus nobody calls TryResolve) should hook this
        // into their per-frame Update. Safe to call multiple times per
        // frame; each call is O(pending-count).
        public void Tick() {
            for (int i = pendingLoads.Count - 1; i >= 0; i--) {
                var p = pendingLoads[i];
                if (!AddressablesBridge.IsDone(p.Op)) continue;
                pendingLoads.RemoveAt(i);
                if (AddressablesBridge.IsSucceeded(p.Op)) {
                    var result = AddressablesBridge.GetResult(p.Op);
                    var src = WrapAsset(result);
                    if (src != null) {
                        ready[p.Handle] = src;
                        handles[p.Handle] = p.Op;
                        inFlight.Remove(p.Handle);
                        Version++;
                        continue;
                    }
                }
                // Either the op failed or the result type wasn't one we
                // can wrap. Try the next type on the ladder if any remain;
                // otherwise mark failed.
                AddressablesBridge.Release(p.Op);
                if (!TryStartNextType(p.Handle, p.NextTypeIndex)) {
                    failed.Add(p.Handle);
                    inFlight.Remove(p.Handle);
                    // Once-per-handle warning. Most common cause in builds:
                    // Addressables groups weren't built before the player
                    // build (Window → Asset Management → Addressables →
                    // Groups → Build → New Build → Default Build Script).
                    // Catalog is empty → every key fails. In Editor this
                    // works because Editor mode loads via AssetDatabase
                    // directly without needing the built catalog.
                    Weva.Diagnostics.UICssDiagnostics.Warn("image-load",
                        "AddressablesImageRegistry: handle '" + p.Handle + "' " +
                        "exhausted the type ladder (Texture2D, Sprite, RenderTexture) " +
                        "with no successful load. Check (1) the Addressables address " +
                        "matches the handle string, (2) Addressables groups were built " +
                        "before the player build, (3) the asset is a Sprite/Texture2D/" +
                        "RenderTexture (not a Material/ScriptableObject).");
                }
            }
        }

        static IImageSource WrapAsset(UnityEngine.Object asset) {
            return asset switch {
                Sprite sp => new SpriteImageSource(sp),
                Texture2D tex => new Texture2DImageSource(tex),
                RenderTexture rt => new RenderTextureImageSource(rt),
                _ => null,
            };
        }

        // Manually register a pre-loaded source under `handle`. Bypasses
        // the Addressables load path entirely — the registry treats this
        // handle as already resolved. Use for sources that don't live in
        // Addressables (sprites from ScriptableObject managers, runtime-
        // generated textures, etc.), so a project can mix Addressable and
        // non-Addressable image sources under one registry instance
        // without needing a composite registry.
        public void Register(string handle, IImageSource source) {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("handle must be non-empty", nameof(handle));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            ready[handle] = source;
            failed.Remove(handle);
            inFlight.Remove(handle);
            Version++;
        }

        // Drops the handle's `ready` entry (and releases the Addressables
        // op handle if one was associated). Returns true if the handle was
        // previously registered or resolved. Useful for forcing a reload
        // — call Unregister, then the next TryResolve kicks a fresh load.
        public bool Unregister(string handle) {
            if (string.IsNullOrEmpty(handle)) return false;
            bool removed = ready.Remove(handle);
            if (handles.TryGetValue(handle, out var op)) {
                AddressablesBridge.Release(op);
                handles.Remove(handle);
                removed = true;
            }
            failed.Remove(handle);
            if (removed) Version++;
            return removed;
        }

        // Kicks off loads for a batch of handles in parallel and returns
        // immediately. Earlier revisions awaited Task.WhenAll on the
        // internal load tasks — that introduced an opportunity for the
        // entire main menu init to deadlock if any one Addressables op
        // failed to surface its Task completion (reflection bridge +
        // SynchronizationContext interactions). Callers that need to
        // block on completion should `while (registry.InFlightCount > 0)
        // await Task.Yield();` instead.
        public Task PreloadAsync(IEnumerable<string> handles) {
            foreach (var h in handles) {
                if (string.IsNullOrEmpty(h)) continue;
                if (ready.ContainsKey(h)) continue;
                if (failed.Contains(h)) continue;
                if (!inFlight.Contains(h)) StartLoad(h);
            }
            return Task.CompletedTask;
        }

        // Type ladder for the speculative load path. Ordered most-likely-
        // first based on observed asset-pipeline patterns: Texture2D wins
        // for default-imported PNGs (Unity UGUI's standard icon shape),
        // Sprite wins for PNGs with `Sprite (2D and UI)` import mode,
        // RenderTexture covers procedural/runtime-generated content.
        static readonly Type[] LoadTypeLadder = {
            typeof(Texture2D),
            typeof(Sprite),
            typeof(RenderTexture),
        };

        void StartLoad(string handle) {
            inFlight.Add(handle);
            if (!AddressablesBridge.IsAvailable) {
                failed.Add(handle);
                inFlight.Remove(handle);
                // Once-per-session warning — the bridge unavailable usually
                // means WEVA_ADDRESSABLES is defined but the Addressables
                // package isn't in the project, OR Addressables itself is
                // misconfigured. Authors see this in Editor + Development
                // builds; release builds emit nothing per UICssDiagnostics gate.
                Weva.Diagnostics.UICssDiagnostics.Warn("image-load",
                    "AddressablesImageRegistry: Addressables bridge unavailable. " +
                    "Either the Addressables package is missing or its runtime " +
                    "assembly isn't reflectable. Handle '" + handle + "' marked failed.");
                return;
            }
            TryStartNextType(handle, 0);
        }

        // Returns true if a load was successfully kicked off (op handle
        // was acquired and added to pendingLoads). Synchronous failure
        // (InvalidKeyException from LoadAssetAsync on a type mismatch)
        // advances `typeIndex` and retries. Returns false when the ladder
        // is exhausted — caller marks the handle as failed.
        bool TryStartNextType(string handle, int typeIndex) {
            while (typeIndex < LoadTypeLadder.Length) {
                var type = LoadTypeLadder[typeIndex];
                object op;
                try {
                    op = AddressablesBridge.LoadAssetAsyncOfType(type, handle);
                } catch (Exception ex) {
                    // Synchronous throw — usually InvalidKeyException for
                    // a type mismatch. Try the next type without surfacing
                    // the error (a load failure is only "real" once every
                    // type has been tried).
                    _ = ex;
                    typeIndex++;
                    continue;
                }
                if (op == null) {
                    typeIndex++;
                    continue;
                }
                pendingLoads.Add(new PendingLoad {
                    Handle = handle,
                    Op = op,
                    NextTypeIndex = typeIndex + 1,
                });
                return true;
            }
            return false;
        }

        public void Clear() {
            foreach (var kv in handles) {
                AddressablesBridge.Release(kv.Value);
            }
            foreach (var p in pendingLoads) {
                AddressablesBridge.Release(p.Op);
            }
            bool hadContent = ready.Count > 0 || handles.Count > 0 || pendingLoads.Count > 0;
            handles.Clear();
            ready.Clear();
            inFlight.Clear();
            failed.Clear();
            pendingLoads.Clear();
            if (hadContent) Version++;
        }

        // Reflection bridge to Unity.Addressables. The package may not be
        // referenced at compile time; resolve everything off the runtime
        // type. All members are static and re-used across registry
        // instances.
        static class AddressablesBridge {
            const BindingFlags StaticPublic = BindingFlags.Public | BindingFlags.Static;
            const BindingFlags InstancePublic = BindingFlags.Public | BindingFlags.Instance;

            static readonly Type addressablesType = Type.GetType(
                "UnityEngine.AddressableAssets.Addressables, Unity.Addressables");
            static readonly MethodInfo loadAssetAsyncDefinition = FindLoadAssetAsync(addressablesType);
            static readonly MethodInfo[] releaseMethods = addressablesType != null
                ? addressablesType.GetMethods(StaticPublic)
                : Array.Empty<MethodInfo>();

            public static bool IsAvailable => addressablesType != null && loadAssetAsyncDefinition != null;

            public static object LoadAssetAsyncOfType(Type assetType, string key) {
                if (!IsAvailable || assetType == null) return null;
                return loadAssetAsyncDefinition.MakeGenericMethod(assetType).Invoke(null, new object[] { key });
            }

            // True when the async operation has finished (succeeded OR
            // failed). Read straight off the AsyncOperationHandle struct's
            // `IsDone` property.
            public static bool IsDone(object handle) {
                if (handle == null) return true;
                var prop = handle.GetType().GetProperty("IsDone", InstancePublic);
                if (prop == null) return true; // unknown shape — treat as done so we don't spin
                return prop.GetValue(handle) is bool b && b;
            }

            public static bool IsSucceeded(object handle) {
                if (handle == null) return false;
                var statusProp = handle.GetType().GetProperty("Status", InstancePublic);
                object status = statusProp != null ? statusProp.GetValue(handle) : null;
                return status != null && status.ToString() == "Succeeded";
            }

            // Returns the loaded asset as a UnityEngine.Object (the common
            // base for Sprite / Texture2D / RenderTexture). Caller does a
            // type-switch to wrap appropriately.
            public static UnityEngine.Object GetResult(object handle) {
                if (handle == null) return null;
                var prop = handle.GetType().GetProperty("Result", InstancePublic);
                if (prop == null) return null;
                return prop.GetValue(handle) as UnityEngine.Object;
            }

            public static void Release(object handle) {
                if (handle == null || addressablesType == null) return;
                if (!IsValid(handle)) return;

                var handleType = handle.GetType();
                foreach (var method in releaseMethods) {
                    if (method.Name != "Release") continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1) continue;

                    try {
                        if (!method.IsGenericMethodDefinition) {
                            var parameterType = parameters[0].ParameterType;
                            if (!IsAsyncOperationHandleType(parameterType)) continue;
                            if (!parameterType.IsAssignableFrom(handleType)) continue;
                            method.Invoke(null, new[] { handle });
                            return;
                        }

                        var resultType = HandleResultType(handleType);
                        if (resultType == null) continue;
                        var closed = method.MakeGenericMethod(resultType);
                        var closedParameterType = closed.GetParameters()[0].ParameterType;
                        if (!closedParameterType.IsAssignableFrom(handleType)) continue;
                        closed.Invoke(null, new[] { handle });
                        return;
                    } catch {
                        // Try the next overload.
                    }
                }
            }

            static bool IsValid(object handle) {
                var isValid = handle.GetType().GetMethod("IsValid", InstancePublic, null, Type.EmptyTypes, null);
                if (isValid == null) return true;
                try {
                    return isValid.Invoke(handle, null) is bool ok && ok;
                } catch {
                    return false;
                }
            }

            static Type HandleResultType(Type handleType) {
                if (handleType == null) return null;
                return handleType.IsGenericType ? handleType.GetGenericArguments()[0] : null;
            }

            static bool IsAsyncOperationHandleType(Type type) {
                return type != null && type.FullName != null && type.FullName.Contains("AsyncOperationHandle");
            }

            static MethodInfo FindLoadAssetAsync(Type type) {
                if (type == null) return null;
                foreach (var method in type.GetMethods(StaticPublic)) {
                    if (method.Name != "LoadAssetAsync") continue;
                    if (!method.IsGenericMethodDefinition) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1) continue;
                    var keyType = parameters[0].ParameterType;
                    if (keyType == typeof(object) || keyType.IsAssignableFrom(typeof(string))) return method;
                }
                return null;
            }
        }
    }
}
#endif
