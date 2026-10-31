using System;
using System.IO;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(Weva.Tests.Rendering.PlayerResultWriter))]

namespace Weva.Tests.Rendering {
    // Release players do not rely on the editor's development connection.
    public sealed class PlayerResultWriter : ITestRunCallback {
        public void RunStarted(ITest testsToRun) { }
        public void TestStarted(ITest test) { }
        public void TestFinished(ITestResult result) { }

        public void RunFinished(ITestResult result) {
#if !UNITY_EDITOR
            string path = Environment.GetEnvironmentVariable("WEVA_TEST_LOCAL_RESULTS");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, result.ToXml(true).OuterXml);
            Application.Quit(result.FailCount == 0 && result.PassCount > 0 ? 0 : 1);
#endif
        }
    }
}
