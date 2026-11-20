using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Testing_OpenMRS_CrossPlatform
{
    /// <summary>
    /// Reads a Selenium suite's results into one table row per assertion, and writes a self-contained HTML report.
    /// Preferred source: results/checkpoints.json (one entry per named checkpoint, each with its own screenshot).
    /// Fallback: results/junit.xml (one entry per test method), for suites that don't use checkpoints.
    /// </summary>
    public static class SeleniumResults
    {
        public static DataTable Read(string resultsFolder)
        {
            var table = new DataTable("SeleniumResults");
            table.Columns.Add("Test", typeof(string));
            table.Columns.Add("Step", typeof(int));
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Outcome", typeof(string));      // passed | failed | not reached | skipped
            table.Columns.Add("Message", typeof(string));
            table.Columns.Add("Screenshot", typeof(string));   // full path, or empty
            table.Columns.Add("Seconds", typeof(double));

            string checkpoints = Path.Combine(resultsFolder, "checkpoints.json");
            string junit = Path.Combine(resultsFolder, "junit.xml");
            if (File.Exists(checkpoints)) ReadCheckpoints(checkpoints, resultsFolder, table);
            else if (File.Exists(junit)) ReadJUnit(junit, resultsFolder, table);

            if (table.Rows.Count == 0)
                table.Rows.Add("Selenium run", 1, "Selenium run produced results", "failed",
                    $"Neither checkpoints.json nor junit.xml found in {resultsFolder}", string.Empty, 0d);
            return table;
        }

        private static void ReadCheckpoints(string path, string resultsFolder, DataTable table)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                string shot = Str(e, "screenshot");
                string fullShot = string.IsNullOrEmpty(shot) ? string.Empty
                    : Path.GetFullPath(Path.Combine(resultsFolder, shot.Replace('/', Path.DirectorySeparatorChar)));
                table.Rows.Add(Str(e, "test"), e.TryGetProperty("step", out var s) ? s.GetInt32() : 0, Str(e, "name"),
                    Str(e, "outcome"), Trim(Str(e, "message")), File.Exists(fullShot) ? fullShot : string.Empty, 0d);
            }
        }

        private static void ReadJUnit(string path, string resultsFolder, DataTable table)
        {
            foreach (XElement tc in XDocument.Load(path).Descendants("testcase"))
            {
                XElement problem = tc.Element("failure") ?? tc.Element("error");
                string outcome = problem != null ? "failed" : tc.Element("skipped") != null ? "skipped" : "passed";
                string message = problem == null ? string.Empty : Trim(((string)problem.Attribute("message") ?? problem.Value));
                double.TryParse((string)tc.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds);
                string name = (string)tc.Attribute("name") ?? "test";
                table.Rows.Add(name, 1, name, outcome, message, FindScreenshot(resultsFolder, name), seconds);
            }
        }

        /// <summary>
        /// The screenshot a suite saved for one test. Suites name the file after the test, but not always the same way
        /// the JUnit report names it: RSpec and Mocha report "group title + example title" while the file carries only
        /// the example title. So: exact name first, then the longest file name the test name ends with (or contains).
        /// </summary>
        private static string FindScreenshot(string resultsFolder, string testName)
        {
            string folder = Path.Combine(resultsFolder, "screenshots");
            if (!Directory.Exists(folder)) return string.Empty;
            string Norm(string s) => Regex.Replace(s, "[^A-Za-z0-9]+", "_").Trim('_').ToLowerInvariant();
            string wanted = Norm(testName);
            var files = Directory.GetFiles(folder, "*.png")
                .Select(f => (Path: f, Key: Norm(Path.GetFileNameWithoutExtension(f))))
                .Where(f => f.Key.Length > 0)
                .ToList();
            return files.FirstOrDefault(f => f.Key == wanted).Path
                ?? files.Where(f => wanted.EndsWith(f.Key)).OrderByDescending(f => f.Key.Length).Select(f => f.Path).FirstOrDefault()
                ?? files.Where(f => wanted.Contains(f.Key)).OrderByDescending(f => f.Key.Length).Select(f => f.Path).FirstOrDefault()
                ?? string.Empty;
        }

        /// <summary>Writes results/SeleniumReport.html with every assertion and its screenshot embedded.</summary>
        public static string WriteHtmlReport(DataTable results, string resultsFolder, string scenario, string baseUrl, int exitCode)
        {
            int passed = results.AsEnumerable().Count(r => (string)r["Outcome"] == "passed");
            var html = new StringBuilder();
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Selenium report ").Append(Enc(scenario))
                .Append("</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1f2933}")
                .Append("h1{font-size:22px}h2{font-size:17px;margin-top:28px}.sum{color:#52606d}")
                .Append(".cp{border:1px solid #d9e2ec;border-radius:6px;margin:10px 0;padding:10px}")
                .Append(".passed{border-left:6px solid #2f9e44}.failed{border-left:6px solid #e03131}")
                .Append(".notreached,.skipped{border-left:6px solid #adb5bd;color:#6c757d}")
                .Append(".tag{font-weight:600;text-transform:uppercase;font-size:12px;margin-right:8px}")
                .Append(".msg{font-family:Consolas,monospace;font-size:12px;white-space:pre-wrap;color:#c92a2a}")
                .Append("img{max-width:100%;border:1px solid #d9e2ec;margin-top:8px}</style></head><body>");
            html.Append("<h1>Selenium report: ").Append(Enc(scenario)).Append("</h1>");
            html.Append("<p class=\"sum\">").Append(passed).Append(" of ").Append(results.Rows.Count)
                .Append(" assertions passed · suite exit code ").Append(exitCode).Append(" · ").Append(Enc(baseUrl))
                .Append(" · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("</p>");

            foreach (var test in results.AsEnumerable().GroupBy(r => (string)r["Test"]))
            {
                html.Append("<h2>").Append(Enc(test.Key)).Append("</h2>");
                foreach (DataRow r in test)
                {
                    string outcome = (string)r["Outcome"];
                    html.Append("<div class=\"cp ").Append(outcome.Replace(" ", "")).Append("\"><span class=\"tag\">")
                        .Append(Enc(outcome)).Append("</span>").Append((int)r["Step"]).Append(". ").Append(Enc((string)r["Name"]));
                    if (!string.IsNullOrEmpty((string)r["Message"]))
                        html.Append("<div class=\"msg\">").Append(Enc((string)r["Message"])).Append("</div>");
                    string shot = (string)r["Screenshot"];
                    if (!string.IsNullOrEmpty(shot) && File.Exists(shot))
                        html.Append("<img alt=\"screenshot\" src=\"data:image/png;base64,")
                            .Append(Convert.ToBase64String(File.ReadAllBytes(shot))).Append("\">");
                    html.Append("</div>");
                }
            }
            foreach (var (file, heading) in new[] { ("execution-trail.log", "Execution trail"), ("browser-console.log", "Browser console") })
            {
                string path = Path.Combine(resultsFolder, file);
                if (!File.Exists(path)) continue;
                html.Append("<h2>").Append(heading).Append("</h2><pre style=\"background:#f5f7fa;padding:10px;font-size:12px;white-space:pre-wrap\">")
                    .Append(Enc(File.ReadAllText(path))).Append("</pre>");
            }
            html.Append("</body></html>");
            string reportPath = Path.Combine(resultsFolder, "SeleniumReport.html");
            File.WriteAllText(reportPath, html.ToString());
            return reportPath;
        }

        /// <summary>Replaces the secret in every text file under the results folder. Returns how many files changed.</summary>
        public static int RedactSecret(string resultsFolder, string secret)
        {
            if (string.IsNullOrEmpty(secret) || !Directory.Exists(resultsFolder)) return 0;
            string[] textTypes = { ".log", ".txt", ".json", ".xml", ".html" };
            int changed = 0;
            foreach (string file in Directory.EnumerateFiles(resultsFolder, "*", SearchOption.AllDirectories)
                         .Where(f => textTypes.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                string text = File.ReadAllText(file);
                if (!text.Contains(secret)) continue;
                File.WriteAllText(file, text.Replace(secret, "********"));
                changed++;
            }
            return changed;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static string Trim(string message) =>
            message.Length > 500 ? message.Substring(0, 500).Trim() + "…" : message.Trim();

        private static string Enc(string text) => WebUtility.HtmlEncode(text);
    }
}
