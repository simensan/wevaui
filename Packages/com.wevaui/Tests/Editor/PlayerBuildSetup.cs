using System;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

[assembly: TestPlayerBuildModifier(typeof(Weva.Tests.EditorTests.PlayerBuildSetup))]
[assembly: PostBuildCleanup(typeof(Weva.Tests.EditorTests.PlayerBuildSetup))]

namespace Weva.Tests.EditorTests {
    // The opt-in smoke runner needs only Unity's generated test bootstrap.
    // Do not pull the user's enabled game scenes into this fixture build.
    public sealed class PlayerBuildSetup : ITestPlayerBuildModifier, IPostBuildCleanup {
        static bool splitBuild;
        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options) {
            if (Environment.GetEnvironmentVariable("WEVA_TEST_PLAYER") != "1") return options;
            options.scenes = options.scenes.Where(path =>
                System.IO.Path.GetFileName(path).StartsWith("InitTestScene", StringComparison.Ordinal)).ToArray();
            if (options.scenes.Length != 1)
                throw new InvalidOperationException("Expected exactly one Unity test bootstrap scene.");
            var target = NamedBuildTarget.FromBuildTargetGroup(options.targetGroup);
            splitBuild = Environment.GetEnvironmentVariable("WEVA_TEST_PLAYER_RELEASE") == "1";
            if (splitBuild) {
                if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP)
                    throw new InvalidOperationException("Release qualification requires IL2CPP.");
                options.options &= ~(BuildOptions.Development | BuildOptions.AllowDebugging |
                                     BuildOptions.WaitForPlayerConnection | BuildOptions.ConnectWithProfiler |
                                     BuildOptions.AutoRunPlayer | BuildOptions.ConnectToHost);
                PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.High);
                PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Release);
            }
            var receipt = Environment.GetEnvironmentVariable("WEVA_TEST_BUILD_RECEIPT");
            if (!string.IsNullOrEmpty(receipt))
                System.IO.File.WriteAllText(receipt, JsonUtility.ToJson(new BuildReceipt {
                    backend = PlayerSettings.GetScriptingBackend(target).ToString(),
                    stripping = PlayerSettings.GetManagedStrippingLevel(target).ToString(),
                    compiler = PlayerSettings.GetIl2CppCompilerConfiguration(target).ToString(),
                    development = (options.options & BuildOptions.Development) != 0,
                    options = options.options.ToString()
                }, true));
            return options;
        }

        public void Cleanup() {
            if (splitBuild && Environment.GetCommandLineArgs().Contains("-runTests"))
                EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        [Serializable]
        class BuildReceipt {
            public string backend, stripping, compiler, options;
            public bool development;
        }
    }
}
