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
            bool in_Headless)
        {
            // A relative path (the default "..\SeleniumSuite", next to the project) is resolved against the project folder
            in_SeleniumRoot = Path.GetFullPath(in_SeleniumRoot);
            string script = Path.Combine(in_SeleniumRoot, "run-scenario.ps1");
            if (!File.Exists(script))
                throw new FileNotFoundException($"Selenium suite runner not found: {script}");

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

            DataTable results = ParseJUnit(Path.Combine(resultsFolder, "junit.xml"));
            Log($"{in_Scenario}: {results.Rows.Count} Selenium test(s), " +
                $"{results.AsEnumerable().Count(r => (string)r["Outcome"] == "passed")} passed");

            string outputPath = Path.Combine(resultsFolder, "output.json");
            string outputJson = File.Exists(outputPath) ? File.ReadAllText(outputPath) : string.Empty;

            return (out_ExitCode: exitCode, out_dt_TestResults: results, out_ResultsFolder: resultsFolder, out_OutputJson: outputJson);
        }

        /// <summary>One row per JUnit testcase: Name, Outcome (passed / failed / skipped), Message, Seconds.</summary>
        private static DataTable ParseJUnit(string junitPath)
        {
            var table = new DataTable("SeleniumResults");
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Outcome", typeof(string));
            table.Columns.Add("Message", typeof(string));
            table.Columns.Add("Seconds", typeof(double));

            if (!File.Exists(junitPath))
            {
                table.Rows.Add("Selenium run produced results", "failed", $"No JUnit file at {junitPath}", 0d);
                return table;
            }

            foreach (XElement testCase in XDocument.Load(junitPath).Descendants("testcase"))
            {
                XElement problem = testCase.Element("failure") ?? testCase.Element("error");
                string outcome = problem != null ? "failed" : testCase.Element("skipped") != null ? "skipped" : "passed";
                string message = problem == null ? string.Empty
                    : ((string)problem.Attribute("message") ?? problem.Value).Trim();
                if (message.Length > 500) message = message.Substring(0, 500) + "…";
                double.TryParse((string)testCase.Attribute("time"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds);
                table.Rows.Add((string)testCase.Attribute("name"), outcome, message, seconds);
            }
            return table;
        }
    }
}
