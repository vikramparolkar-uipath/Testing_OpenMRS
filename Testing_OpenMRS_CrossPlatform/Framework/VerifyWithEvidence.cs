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
    /// Records one Selenium checkpoint as a Test Manager assertion WITH the screenshot Selenium took at that checkpoint.
    ///
    /// Test Manager attaches to an assertion a capture of the robot's screen at the moment the verification runs; it
    /// cannot take an image file. So the checkpoint's screenshot is shown full-screen (borderless Edge kiosk window,
    /// with a caption naming the checkpoint), the verification runs with screenshot capture on, and the window closes.
    /// Needs an interactive robot session (Studio, attended or unattended robot), not a background process.
    /// </summary>
    public class VerifyWithEvidence : CodedWorkflow
    {
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SettleWait = TimeSpan.FromMilliseconds(400);

        [Workflow]
        public bool Execute(string in_Title, bool in_Passed, string in_Message, string in_ScreenshotPath)
        {
            Process viewer = null;
            string tempDir = null;
            try
            {
                if (!string.IsNullOrEmpty(in_ScreenshotPath) && File.Exists(in_ScreenshotPath))
                    (viewer, tempDir) = ShowFullScreen(in_ScreenshotPath, in_Title, in_Passed);

                string format = string.IsNullOrWhiteSpace(in_Message)
                    ? null
                    : in_Message.Replace("{", "(").Replace("}", ")") + " - {Result}";
                return testing.VerifyExpression(in_Passed, format, continueOnFailure: true,
                    alternativeVerificationTitle: in_Title,
                    takeScreenshotInCaseOfFailingAssertion: viewer != null,
                    takeScreenshotInCaseOfSucceedingAssertion: viewer != null);
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

        private (Process, string) ShowFullScreen(string imagePath, string title, bool passed)
        {
            string edge = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            }.FirstOrDefault(File.Exists);
            if (edge == null)
            {
                Log("Microsoft Edge not found: assertion recorded without its screenshot");
                return (null, null);
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "uipath-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string image = Path.Combine(tempDir, "shot.png");
            File.Copy(imagePath, image);
            string colour = passed ? "#2f9e44" : "#e03131";
            string page = Path.Combine(tempDir, "evidence.html");
            // The page renames itself to a unique token only after the screenshot has loaded; the window title
            // tells us when it is safe to capture the screen.
            string readyToken = "EVIDENCE-READY-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(page,
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>loading</title><style>" +
                "html,body{margin:0;height:100%;background:#111;overflow:hidden;font-family:Segoe UI,Arial,sans-serif}" +
                $".bar{{height:44px;line-height:44px;padding:0 18px;color:#fff;background:{colour};font-size:18px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}" +
                ".frame{position:absolute;top:44px;bottom:0;left:0;right:0;display:flex;align-items:center;justify-content:center}" +
                ".frame img{max-width:100%;max-height:100%}" +
                "</style></head><body><div class=\"bar\">" + (passed ? "PASSED" : "FAILED") + " &middot; Selenium screenshot &middot; " +
                WebUtility.HtmlEncode(title) + "</div><div class=\"frame\"><img src=\"shot.png\" " +
                "onload=\"waitFullScreen()\"></div><script>" +
                // Ready = image loaded AND the window really fills the screen (kiosk full-screen switch finished),
                // then two painted frames later. Falls back after 6 s so a run never hangs on it.
                "var t0=Date.now();function waitFullScreen(){" +
                "var full=window.innerWidth>=screen.width&&window.innerHeight>=screen.height;" +
                "if(full||Date.now()-t0>6000){requestAnimationFrame(function(){requestAnimationFrame(function(){" +
                $"setTimeout(function(){{document.title='{readyToken}'}},300)}})}})}}" +
                "else setTimeout(waitFullScreen,100)}" +
                "</script></body></html>");

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
    }
}
