# OpenMRS: Selenium suites under UiPath Test Manager

A UiPath Studio test project that runs **existing Selenium tests, unchanged**, in five languages, against the OpenMRS
demo server. Every Selenium test shows up as its own verification in Test Manager, with screenshots and logs attached.
UiPath adds Orchestrator credentials, scheduling, one dashboard, and extra checks; for example, S2 cross-checks the
UI result through the OpenMRS REST API.

```
Testing_OpenMRS/
├── Testing_OpenMRS_CrossPlatform/   UiPath Studio project: open this folder in Studio
├── SeleniumSuite/                   the five Selenium suites + run-scenario.ps1 (one entry point for all five)
└── Setup/                           prerequisite installer and credential-asset script
```

| Test case | Selenium suite | Persona |
|---|---|---|
| S1 Login smoke | Java 21 + TestNG (Maven) | each of Admin, Doctor, Nurse, Clerk (data-driven) |
| S2 Register a patient + REST cross-check | C# .NET 8 + NUnit | Clerk |
| S3 Find a patient by name, then by ID | Python 3 + pytest | Nurse |
| S4 Vitals out-of-range values rejected | JavaScript (Node) + Mocha | Nurse |
| S5 Clerk can't open an admin page by URL | Ruby 3.3 + RSpec | Clerk |

## Run it locally from Studio

You need a Windows machine with **UiPath Studio 26.10 or later**, connected to an Orchestrator tenant (Assistant signed in).

**1. Get the code**
```powershell
git clone https://github.com/vikramparolkar-uipath/Testing_OpenMRS.git
cd Testing_OpenMRS
```

**2. Install the Selenium toolchains.** The checker lists what's missing. Run it with `-Install` to install the missing tools (winget, plus Maven from apache.org), then run it with `-Prepare` to download each suite's packages once:
```powershell
powershell -ExecutionPolicy Bypass -File Setup\Install-Prerequisites.ps1
powershell -ExecutionPolicy Bypass -File Setup\Install-Prerequisites.ps1 -Install
powershell -ExecutionPolicy Bypass -File Setup\Install-Prerequisites.ps1 -Prepare
```
It needs: JDK 11+ and Maven, the .NET 8 SDK, Python 3, Node.js, Ruby 3 with bundler, and Microsoft Edge. **Restart Studio afterwards** so it sees the new PATH.

> **Let Claude do it.** Open [Claude Code](https://claude.com/claude-code) in the cloned `Testing_OpenMRS` folder (the terminal CLI or the desktop app's Code tab) and paste the prompts below one at a time. Claude asks before installing anything.
>
> **1. Check and install the toolchains**
> ```text
> I cloned Testing_OpenMRS to run its UiPath Studio test cases locally. Run
> Setup\Install-Prerequisites.ps1 (check mode) and tell me what's missing. Then, after I confirm,
> install only the missing tools with the same script's -Install switch. If winget fails or isn't
> available, install the same tools from their official sources instead (Eclipse Temurin JDK 21,
> Apache Maven from apache.org with the checksum verified, .NET 8 SDK, Python 3, Node.js LTS,
> RubyInstaller Ruby 3.3 plus `gem install bundler`). Finish by running the check again and showing
> me the result.
> ```
>
> **2. Warm up the five Selenium suites**
> ```text
> Run Setup\Install-Prerequisites.ps1 -Prepare to download the dependencies for all five suites in
> SeleniumSuite (Maven, NuGet, pip, npm, bundler). If any suite fails, read the error, fix the cause
> on my machine (PATH, a missing SDK, a proxy setting) rather than changing the test code, and run
> it again until all five are ready.
> ```
>
> **3. Smoke-test one suite without UiPath** (optional, proves the toolchains work before you open Studio)
> ```text
> Run SeleniumSuite\run-scenario.ps1 -Scenario S3 against the OpenMRS demo server
> (OMRS_BASE_URL=http://uilabsopenmrs.westeurope.cloudapp.azure.com:8080/openmrs,
> OMRS_USERNAME=nurse, OMRS_HEADLESS=true). Ask me for the nurse password and pass it only as the
> OMRS_PASSWORD environment variable: never put it in a file, a command line or your reply. Show me
> the pytest summary and SeleniumSuite\s3-find-python-pytest\results\junit.xml.
> ```
>
> **If something breaks**
> ```text
> A Testing_OpenMRS Selenium suite fails on my machine. Read
> SeleniumSuite\<scenario folder>\results\console.log and junit.xml, explain the root cause, and
> fix my environment. Known traps: UiPath robots hide the .NET SDK (run-scenario.ps1 already calls
> Program Files\dotnet\dotnet.exe directly), Chrome 157's driver drops input after the first page
> load (the suites use Edge), and Studio needs a restart after PATH changes.
> ```

**3. Create the persona credential assets** in the Orchestrator tenant your Studio is connected to. Create them in a
folder called `OpenMRS`, or in any folder you then pass as `in_AssetFolder`:

| Asset name | Type | User name |
|---|---|---|
| OpenMRS Admin | Credential | admin |
| OpenMRS Doctor | Credential | doctor |
| OpenMRS Nurse | Credential | nurse |
| OpenMRS Clerk | Credential | clerk |

Ask the project owner for the passwords; they are not stored in this repo. You can create the assets by hand in Orchestrator, or with the CLI:
```powershell
uip login
powershell -ExecutionPolicy Bypass -File Setup\Set-OpenMRSCredentials.ps1 -FolderPath OpenMRS
```

**4. Open and run in Studio**
1. Open `Testing_OpenMRS_CrossPlatform\project.json` in Studio and let it restore packages.
2. In the Test Explorer, run any test case under `Tests`. You can also open one and press **Run File** (or **Debug File**).
3. Watch Edge drive OpenMRS. The Output panel then lists one `Verification passed` line per Selenium test.

Defaults are set, so nothing has to be changed. To point somewhere else, change these test case arguments:

| Argument | Default | Notes |
|---|---|---|
| `in_BaseUrl` | `http://uilabsopenmrs.westeurope.cloudapp.azure.com:8080/openmrs` | OpenMRS server |
| `in_AssetFolder` | empty (the job's folder) | Orchestrator folder with the four credential assets. For a Studio run, set it to `OpenMRS` or pick that folder in Studio |
| `in_SeleniumRoot` | `..\SeleniumSuite` | Relative to the project folder |
| `in_Headless` | `False` | `True` hides the browser |
| `in_PatientQuery` (S3, S4) | `Smith` | Patient to search for |
| `in_Persona` (S1) | `Clerk` | Test Manager runs all four from `.variations\S1_Personas.json` |

## Good to know

- **Screenshots and logs are only attached in Orchestrator or Test Manager runs.** A local Studio run logs "Skipped attaching document" instead. That's expected; the files are still in `SeleniumSuite\<scenario>\results\`.
- **S2 and S4 write to the shared demo server.** S2 registers a new patient (`Mia UiPathSel<timestamp>`). S4 starts a visit on the first matching patient and ends it again. S1, S3 and S5 only read data.
- **Edge is the default browser.** Chrome 157's driver stops accepting input after the first page load.
- **The first run of each suite is slow** (package downloads) unless you ran `-Prepare`. After that, each test case takes about 20–60 seconds.
- **Running one suite without UiPath:** set `OMRS_USERNAME` and `OMRS_PASSWORD`, then run `SeleniumSuite\run-scenario.ps1 -Scenario S3`. See `SeleniumSuite\README.md`.

Last verified 2026-11-19: all five test cases pass from Studio against the demo server.
