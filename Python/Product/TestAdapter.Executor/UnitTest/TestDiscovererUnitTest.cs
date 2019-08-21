﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.
// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.TestAdapter.Config;
using Microsoft.PythonTools.TestAdapter.Services;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Newtonsoft.Json;

namespace Microsoft.PythonTools.TestAdapter.UnitTest {
    internal class TestDiscovererUnitTest : IPythonTestDiscoverer {
        private readonly PythonProjectSettings _settings;
        private IMessageLogger _logger;
        private static readonly string DiscoveryAdapterPath = PythonToolsInstallPath.GetFile("PythonFiles\\testing_tools\\run_adapter.py");

        public TestDiscovererUnitTest(PythonProjectSettings settings) {
            _settings = settings;
        }

        public void DiscoverTests(
            IEnumerable<string> sources,
            IMessageLogger logger,
            ITestCaseDiscoverySink discoverySink
        ) {
            _logger = logger;
            var workspaceText = _settings.IsWorkspace ? Strings.WorkspaceText : Strings.ProjectText;
            LogInfo(Strings.PythonTestDiscovererStartedMessage.FormatUI(PythonConstants.UnitTestText, _settings.ProjectName, workspaceText, _settings.DiscoveryWaitTimeInSeconds));

            var env = InitializeEnvironment(sources, _settings);
            var outputFilePath = Path.GetTempFileName();
            var arguments = GetArguments(sources, outputFilePath);

            LogInfo("cd " + _settings.WorkingDirectory);
            LogInfo("set " + _settings.PathEnv + "=" + env[_settings.PathEnv]);
            LogInfo($"{_settings.InterpreterPath} {string.Join(" ", arguments)}");

            try {
                var stdout = ProcessExecute.RunWithTimeout(
                    _settings.InterpreterPath,
                    env,
                    arguments,
                    _settings.WorkingDirectory,
                    _settings.PathEnv,
                    _settings.DiscoveryWaitTimeInSeconds
                );
                if (!String.IsNullOrEmpty(stdout)) {
                    Error(stdout);
                }
            } catch (TimeoutException) {
                Error(Strings.PythonTestDiscovererTimeoutErrorMessage);
                return;
            }

            if (!File.Exists(outputFilePath)) {
                Error(Strings.PythonDiscoveryResultsNotFound.FormatUI(outputFilePath));
                return;
            }

            string json = File.ReadAllText(outputFilePath);
            if (String.IsNullOrEmpty(json)) {
                return;
            }

            List<UnitTestDiscoveryResults> results = null;
            try {
                results = JsonConvert.DeserializeObject<List<UnitTestDiscoveryResults>>(json);
                CreateVsTests(results, discoverySink);
            } catch (InvalidOperationException ex) {
                Error("Failed to parse: {0}".FormatInvariant(ex.Message));
                Error(json);
            } catch (JsonException ex) {
                Error("Failed to parse: {0}".FormatInvariant(ex.Message));
                Error(json);
            }
        }

        private void CreateVsTests(
            IEnumerable<UnitTestDiscoveryResults> unitTestResults,
            ITestCaseDiscoverySink discoverySink
        ) {
            bool showConfigurationHint = false;
            foreach (var test in unitTestResults?.SelectMany(result => result.Tests.Select(test => test)).MaybeEnumerate()) {
                try {
                    // Note: Test Explorer will show a key not found exception if we use a source path that doesn't match a test container's source.
                    if (_settings.TestContainerSources.TryGetValue(test.Source, out _)) {
                        TestCase tc = test.ToVsTestCase(_settings.ProjectHome);
                        discoverySink?.SendTestCase(tc);
                    } else {
                        Warn(Strings.ErrorTestContainerNotFound.FormatUI(_settings.ProjectHome, test.ToString()));
                        showConfigurationHint = true;
                    }
                } catch (Exception ex) {
                    Error(ex.Message);
                }
            }

            if (showConfigurationHint) {
                LogInfo(Strings.DiscoveryConfigurationMessage);
            }
        }

        public string[] GetArguments(IEnumerable<string> sources, string outputfilename) {
            var arguments = new List<string>();
            arguments.Add(DiscoveryAdapterPath);
            arguments.Add("discover");
            arguments.Add("unittest");
            arguments.Add("--output-file");
            arguments.Add(outputfilename);
            //Note unittest specific options go after this separator
            arguments.Add("--");
            arguments.Add(_settings.UnitTestRootDir);
            arguments.Add(_settings.UnitTestPattern);

            return arguments.ToArray();
        }

        private Dictionary<string, string> InitializeEnvironment(IEnumerable<string> sources, PythonProjectSettings projSettings) {
            var pythonPathVar = projSettings.PathEnv;
            var pythonPath = GetSearchPaths(sources, projSettings);
            var env = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(pythonPathVar)) {
                env[pythonPathVar] = pythonPath;
            }

            foreach (var envVar in projSettings.Environment) {
                env[envVar.Key] = envVar.Value;
            }

            env["PYTHONUNBUFFERED"] = "1";

            return env;
        }

        private string GetSearchPaths(IEnumerable<string> sources, PythonProjectSettings settings) {
            var paths = settings.SearchPath;
            paths.Insert(0, settings.WorkingDirectory);

            string searchPaths = string.Join(
                ";",
                paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            );
            return searchPaths;
        }

        [Conditional("DEBUG")]
        private void DebugInfo(string message) {
            _logger?.SendMessage(TestMessageLevel.Informational, message);
        }

        private void LogInfo(string message) {
            _logger?.SendMessage(TestMessageLevel.Informational, message);
        }

        private void Error(string message) {
            _logger?.SendMessage(TestMessageLevel.Error, message);
        }

        private void Warn(string message) {
            _logger?.SendMessage(TestMessageLevel.Warning, message);
        }
    }
}
