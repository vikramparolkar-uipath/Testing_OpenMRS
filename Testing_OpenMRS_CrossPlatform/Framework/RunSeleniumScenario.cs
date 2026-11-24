using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using UiPath.CodedWorkflows;

namespace Testing_OpenMRS_CrossPlatform
{
    /// <summary>
    /// Runs one existing Selenium scenario (any language) through the suite's run-scenario.ps1, unchanged,
    /// and turns its JUnit XML into a table the test case can report on.
    /// The persona password comes from an Orchestrator credential asset and reaches the Selenium process only
    /// as an environment variable: never on a command line, never in a log.
    /// </summary>
    public class RunSeleniumScenario : CodedWorkflow
    {
        private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

        [Workflow]
        public (int out_ExitCode, DataTable out_dt_TestResults, string out_ResultsFolder, string out_OutputJson) Execute(
            string in_Scenario,
            string in_CredentialAsset,
            string in_AssetFolder,
            string in_BaseUrl,
            string in_SeleniumRoot,
            string in_PatientQuery,
            bool in_Headless,
            bool in_SimulateDefect)
        {
            in_SeleniumRoot = ResolveSeleniumRoot(in_SeleniumRoot);
            string script = Path.Combine(in_SeleniumRoot, "run-scenario.ps1");

            string scenarioFolder = Directory.GetDirectories(in_SeleniumRoot, in_Scenario.ToLowerInvariant() + "-*").FirstOrDefault()
                ?? throw new DirectoryNotFoundException($"No folder for scenario {in_Scenario} under {in_SeleniumRoot}");
            string resultsFolder = Path.Combine(scenarioFolder, "results");

            var (userName, password) = system.GetCredential(in_CredentialAsset,
                string.IsNullOrWhiteSpace(in_AssetFolder) ? null : in_AssetFolder, timeoutMS: 30000);   // empty = the job's own folder
            Log($"{in_Scenario}: running {Path.GetFileName(scenarioFolder)} as '{userName}' against {in_BaseUrl}");

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Scenario {in_Scenario}")
            {
                WorkingDirectory = in_SeleniumRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["OMRS_BASE_URL"] = in_BaseUrl;
            psi.Environment["OMRS_USERNAME"] = userName;
            psi.Environment["OMRS_PASSWORD"] = new NetworkCredential(string.Empty, password).Password;
            psi.Environment["OMRS_HEADLESS"] = in_Headless ? "true" : "false";
            // Portable toolchains installed by Bootstrap-SeleniumSuite.ps1 on a fresh robot machine
            if (File.Exists(Path.Combine(ToolsDir, "ready.txt"))) psi.Environment["SELENIUM_TOOLS"] = ToolsDir;
            // Demo switch for suites that support it (S3): imitate an application regression so a checkpoint fails
            psi.Environment["OMRS_SIMULATE_DEFECT"] = in_SimulateDefect ? "true" : "false";
            if (in_SimulateDefect) Log($"{in_Scenario}: SIMULATED DEFECT is on - a checkpoint is expected to fail");
            if (!string.IsNullOrWhiteSpace(in_PatientQuery))
                psi.Environment["OMRS_PATIENT_QUERY"] = in_PatientQuery;

            var console = new StringBuilder();
            int exitCode;
            var watch = Stopwatch.StartNew();
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (console) console.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (console) console.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException($"{in_Scenario} did not finish within {RunTimeout.TotalMinutes} minutes");
                }
                process.WaitForExit();   // flush the async output readers
                exitCode = process.ExitCode;
            }
            Log($"{in_Scenario}: Selenium run finished in {watch.Elapsed.TotalSeconds:F0}s with exit code {exitCode}");

            Directory.CreateDirectory(resultsFolder);
            File.WriteAllText(Path.Combine(resultsFolder, "console.log"), console.ToString());

            // Safety net before anything is attached to Test Manager: no text file may contain the password
            // (driver logs record sendKeys text, for example).
            int redacted = SeleniumResults.RedactSecret(resultsFolder, psi.Environment["OMRS_PASSWORD"]);
            psi.Environment["OMRS_PASSWORD"] = string.Empty;
            if (redacted > 0) Log($"{in_Scenario}: password redacted from {redacted} result file(s)");

            DataTable results = SeleniumResults.Read(resultsFolder);
            Log($"{in_Scenario}: {results.Rows.Count} Selenium assertion(s), " +
                $"{results.AsEnumerable().Count(r => (string)r["Outcome"] == "passed")} passed");
            string report = SeleniumResults.WriteHtmlReport(results, resultsFolder, in_Scenario, in_BaseUrl, exitCode);
            Log($"{in_Scenario}: report written to {report}");

            string outputPath = Path.Combine(resultsFolder, "output.json");
            string outputJson = File.Exists(outputPath) ? File.ReadAllText(outputPath) : string.Empty;

            return (out_ExitCode: exitCode, out_dt_TestResults: results, out_ResultsFolder: resultsFolder, out_OutputJson: outputJson);
        }

        private static readonly string ToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiPathSeleniumTools");
        private static readonly string BootstrapRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UiPath", "Testing_OpenMRS");
        private static readonly TimeSpan BootstrapTimeout = TimeSpan.FromMinutes(40);

        /// <summary>
        /// Finds the Selenium suite, preparing the machine first when it has none (a fresh cloud VM, for example):
        /// Bootstrap-SeleniumSuite.ps1 downloads the suites and installs portable toolchains for this user.
        /// </summary>
        private string ResolveSeleniumRoot(string given)
        {
            string found = FindSeleniumRoot(given, out string looked);
            string bootstrapped = Path.Combine(BootstrapRoot, "SeleniumSuite");
            // Resume an unfinished preparation, but only on a machine the bootstrap set up itself (it leaves a marker);
            // a developer's own clone in the same Documents folder uses the machine's toolchains as before.
            bool needsTools = found != null
                && string.Equals(Path.GetFullPath(found), Path.GetFullPath(bootstrapped), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(BootstrapRoot, ".bootstrapped"))
                && !File.Exists(Path.Combine(ToolsDir, "ready.txt"));
            if (found != null && !needsTools) return found;

            Log(found == null
                ? $"No Selenium suite on this machine (looked in: {looked}). Preparing it now; the first run takes several minutes."
                : "Selenium suite found but its toolchains aren't prepared yet. Preparing them now.");
            RunBootstrap();
            if (!File.Exists(Path.Combine(bootstrapped, "run-scenario.ps1")))
                throw new FileNotFoundException("Bootstrap finished but the Selenium suite is still missing at " + bootstrapped);
            return bootstrapped;
        }

        private void RunBootstrap()
        {
            string script = FindBootstrapScript()
                ?? throw new FileNotFoundException("Framework\\Bootstrap-SeleniumSuite.ps1 not found in the package");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Root \"{BootstrapRoot}\" -Tools \"{ToolsDir}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            var output = new StringBuilder();
            var watch = Stopwatch.StartNew();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.AppendLine(e.Data); if (e.Data.StartsWith("[bootstrap")) Log(e.Data); } };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit((int)BootstrapTimeout.TotalMilliseconds))
                {
                    p.Kill(entireProcessTree: true);
                    throw new TimeoutException($"Machine preparation did not finish within {BootstrapTimeout.TotalMinutes} minutes");
                }
                p.WaitForExit();
                Directory.CreateDirectory(ToolsDir);
                File.WriteAllText(Path.Combine(ToolsDir, "bootstrap.log"), output.ToString());
                if (p.ExitCode != 0)
                {
                    string tail = string.Join(Environment.NewLine, output.ToString().Split('\n').Reverse().Take(15).Reverse());
                    throw new InvalidOperationException($"Machine preparation failed (exit {p.ExitCode}). Last lines:{Environment.NewLine}{tail}");
                }
            }
            Log($"Machine prepared in {watch.Elapsed.TotalMinutes:F1} min. Tools: {ToolsDir}");
        }

        /// <summary>The bootstrap script ships in the package (Framework folder); the robot's working directory is the package folder.</summary>
        private static string FindBootstrapScript()
        {
            const string name = "Bootstrap-SeleniumSuite.ps1";
            foreach (string dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                string direct = Path.Combine(dir, "Framework", name);
                if (File.Exists(direct)) return direct;
            }
            string pkg = Directory.GetParent(Environment.CurrentDirectory)?.FullName;
            return pkg == null ? null : Directory.EnumerateFiles(pkg, name, SearchOption.AllDirectories).FirstOrDefault();
        }

        /// <summary>
        /// Looks for an existing Selenium suite. From Studio the default "..\SeleniumSuite" sits next to the project, but a
        /// robot runs the published package from its NuGet cache, where a relative path points nowhere. So, in order:
        /// the argument as given, the SELENIUM_SUITE_ROOT environment variable, then the repo clone in Documents\UiPath.
        /// </summary>
        private static string FindSeleniumRoot(string given, out string looked)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(given)) candidates.Add(Path.GetFullPath(given));
            string fromEnv = Environment.GetEnvironmentVariable("SELENIUM_SUITE_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(Path.GetFullPath(fromEnv));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "UiPath", "Testing_OpenMRS", "SeleniumSuite"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "UiPath", "Testing_OpenMRS", "SeleniumSuite"));

            looked = string.Join("; ", candidates.Distinct());
            return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "run-scenario.ps1")));
        }
    }
}
