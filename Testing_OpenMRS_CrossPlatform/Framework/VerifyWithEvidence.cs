using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using UiPath.CodedWorkflows;

namespace Testing_OpenMRS_CrossPlatform
{
    /// <summary>
    /// Records one check as a Test Manager assertion WITH a screenshot, whether it passes or fails.
    ///
    /// Test Manager attaches to an assertion a capture of the robot's screen at the moment the verification runs; it
    /// cannot take an image file. So the evidence is shown full-screen (borderless Edge kiosk window with a caption bar),
    /// the verification runs with "take screenshot on success" and "on failure" both on, and the window closes.
    ///   - With a Selenium screenshot: that screenshot, scaled to fill the screen.
    ///   - Without one (exit code, REST API checks): an evidence card with the check, its outcome and its details
    ///     (plus the last lines of a log file, if one is given).
    /// Needs an interactive robot session (Studio, attended or unattended robot), not a background process.
    /// </summary>
    public class VerifyWithEvidence : CodedWorkflow
    {
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SettleWait = TimeSpan.FromMilliseconds(400);
        private const int LogTailLines = 25;

        [Workflow]
        public bool Execute(string in_Title, bool in_Passed, string in_Message, string in_ScreenshotPath,
            string in_Details, string in_DetailsFile)
        {
            Process viewer = null;
            string tempDir = null;
            try
            {
                (viewer, tempDir) = ShowEvidence(in_Title, in_Passed, in_Message, in_ScreenshotPath, in_Details, in_DetailsFile);

                string format = string.IsNullOrWhiteSpace(in_Message)
                    ? null
                    : in_Message.Replace("{", "(").Replace("}", ")") + " - {Result}";
                return testing.VerifyExpression(in_Passed, format, continueOnFailure: true,
                    alternativeVerificationTitle: in_Title,
                    takeScreenshotInCaseOfFailingAssertion: true,
                    takeScreenshotInCaseOfSucceedingAssertion: true);
            }
            finally
            {
                if (viewer != null)
                {
                    try { if (!viewer.HasExited) viewer.Kill(entireProcessTree: true); }
                    catch (Exception ex) { Log($"Could not close the evidence window: {ex.Message}"); }
                    viewer.Dispose();
                }
                if (tempDir != null)
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch (Exception ex) { Log($"Temporary evidence folder left behind: {ex.Message}"); }
                }
            }
        }

        private (Process, string) ShowEvidence(string title, bool passed, string message, string imagePath,
            string details, string detailsFile)
        {
            string edge = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            }.FirstOrDefault(File.Exists);
            if (edge == null)
            {
                Log("Microsoft Edge not found: the assertion screenshot shows the desktop instead of the evidence");
                return (null, null);
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "uipath-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            bool hasImage = !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath);
            if (hasImage) File.Copy(imagePath, Path.Combine(tempDir, "shot.png"));

            string colour = passed ? "#2f9e44" : "#e03131";
            string outcome = passed ? "PASSED" : "FAILED";
            // The page renames itself to a unique token only once it is painted (and the image loaded); the window
            // title tells us when it is safe to capture the screen.
            string readyToken = "EVIDENCE-READY-" + Guid.NewGuid().ToString("N");

            string body;
            if (hasImage)
            {
                body = $"<div class=\"bar\">{outcome} &middot; Selenium screenshot &middot; {Enc(title)}</div>" +
                       "<div class=\"frame\"><img src=\"shot.png\" onload=\"waitFullScreen()\"></div>";
            }
            else
            {
                string text = details ?? string.Empty;
                if (!string.IsNullOrEmpty(detailsFile) && File.Exists(detailsFile))
                {
                    string[] lines = File.ReadAllLines(detailsFile).Where(l => l.Trim().Length > 0).ToArray();
                    text += (text.Length > 0 ? "\n\n" : "") + $"Last {Math.Min(LogTailLines, lines.Length)} lines of {Path.GetFileName(detailsFile)}:\n"
                            + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - LogTailLines)));
                }
                body = $"<div class=\"bar\">{outcome} &middot; {Enc(title)}</div><div class=\"card\">" +
                       $"<div class=\"badge\" style=\"background:{colour}\">{outcome}</div>" +
                       $"<h1>{Enc(title)}</h1>" +
                       (string.IsNullOrWhiteSpace(message) ? "" : $"<div class=\"msg\">{Enc(message)}</div>") +
                       (string.IsNullOrWhiteSpace(text) ? "" : $"<pre>{Enc(text)}</pre>") +
                       $"<div class=\"foot\">Checked by UiPath on {Environment.MachineName} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>" +
                       "</div><script>waitFullScreen()</script>";
            }

            string page = Path.Combine(tempDir, "evidence.html");
            File.WriteAllText(page,
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>loading</title><style>" +
                "html,body{margin:0;height:100%;background:#111;overflow:hidden;font-family:Segoe UI,Arial,sans-serif}" +
                $".bar{{height:44px;line-height:44px;padding:0 18px;color:#fff;background:{colour};font-size:18px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}" +
                // Scale the Selenium screenshot up or down to fill the space under the bar, keeping its proportions
                ".frame{position:absolute;top:44px;bottom:0;left:0;right:0}" +
                ".frame img{width:100%;height:100%;object-fit:contain;display:block}" +
                ".card{position:absolute;top:44px;bottom:0;left:0;right:0;background:#f5f7fa;color:#1f2933;padding:48px 64px;box-sizing:border-box;overflow:hidden}" +
                ".badge{display:inline-block;color:#fff;font-weight:700;font-size:20px;padding:6px 16px;border-radius:6px}" +
                ".card h1{font-size:34px;font-weight:600;margin:20px 0 16px}" +
                ".msg{font-size:20px;color:#c92a2a;margin-bottom:16px}" +
                ".card pre{font-family:Consolas,monospace;font-size:18px;background:#fff;border:1px solid #d9e2ec;border-radius:6px;padding:18px;white-space:pre-wrap;word-break:break-word;max-height:62%;overflow:hidden}" +
                ".foot{position:absolute;bottom:24px;color:#7b8794;font-size:15px}" +
                "</style><script>" +
                // Ready = content painted AND the window really fills the screen (kiosk full-screen switch finished),
                // then two painted frames later. Falls back after 6 s so a run never hangs on it.
                "var t0=Date.now();function waitFullScreen(){" +
                "var full=window.innerWidth>=screen.width&&window.innerHeight>=screen.height;" +
                "if(full||Date.now()-t0>6000){requestAnimationFrame(function(){requestAnimationFrame(function(){" +
                $"setTimeout(function(){{document.title='{readyToken}'}},300)}})}})}}" +
                "else setTimeout(waitFullScreen,100)}" +
                "</script></head><body>" + body + "</body></html>");

            var psi = new ProcessStartInfo(edge,
                $"--kiosk \"{new Uri(page).AbsoluteUri}\" --edge-kiosk-type=fullscreen --no-first-run " +
                $"--no-default-browser-check --disable-features=msEdgeFRE --user-data-dir=\"{Path.Combine(tempDir, "profile")}\"")
            { UseShellExecute = false };
            Process viewer = Process.Start(psi);

            var watch = Stopwatch.StartNew();
            bool ready = false;
            while (!ready && watch.Elapsed < ReadyTimeout)
            {
                Thread.Sleep(150);
                viewer.Refresh();
                ready = viewer.MainWindowTitle.Contains(readyToken);
            }
            if (!ready) Log($"Evidence window not confirmed ready after {ReadyTimeout.TotalSeconds:F0}s; capturing anyway");
            Thread.Sleep(SettleWait);   // the kiosk switch to full screen finishes just after the first paint
            return (viewer, tempDir);
        }

        private static string Enc(string text) => WebUtility.HtmlEncode(text ?? string.Empty);
    }
}
