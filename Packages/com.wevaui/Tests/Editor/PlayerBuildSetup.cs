using System;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools;

[assembly: TestPlayerBuildModifier(typeof(Weva.Tests.EditorTests.PlayerBuildSetup))]

namespace Weva.Tests.EditorTests {
    // The opt-in smoke runner needs only Unity's generated test bootstrap.
    // Do not pull the user's enabled game scenes into this fixture build.
    public sealed class PlayerBuildSetup : ITestPlayerBuildModifier {
        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options) {
            if (Environment.GetEnvironmentVariable("WEVA_TEST_PLAYER") != "1") return options;
            options.scenes = options.scenes.Where(path =>
                System.IO.Path.GetFileName(path).StartsWith("InitTestScene", StringComparison.Ordinal)).ToArray();
            if (options.scenes.Length != 1)
                throw new InvalidOperationException("Expected exactly one Unity test bootstrap scene.");
            return options;
        }
    }
}
