# Testing_OpenMRS_CrossPlatform: Selenium suites under UiPath Test Manager

Demo project. Five Selenium scenarios, each written in a different language, run **unchanged** from
UiPath test cases. Every Selenium test becomes its own verification in Test Manager, with screenshots, the
JUnit file and the console log attached. Studio adds what Selenium doesn't do: one dashboard, credentials from
Orchestrator, scheduling, and extra checks such as the REST cross-check in S2.

## Test cases

| Test case | Selenium suite | Persona asset | What UiPath adds |
|---|---|---|---|
| `Tests/S1_LoginSmoke_JavaTestNG` | Java + TestNG (`mvn test`) | `OpenMRS <in_Persona>` | Data-driven: `.variations/S1_Personas.json` runs Admin, Doctor, Nurse, Clerk |
| `Tests/S2_RegisterPatient_CSharpNUnit` | C# + NUnit (`dotnet test`) | `OpenMRS Clerk` | Reads the new Patient ID and checks name, gender and birthdate through the OpenMRS REST API |
| `Tests/S3_FindPatient_PythonPytest` | Python + pytest | `OpenMRS Nurse` | â€” |
| `Tests/S4_VitalsRangeCheck_JavaScriptMocha` | JavaScript + Mocha | `OpenMRS Nurse` | â€” |
| `Tests/S5_ClerkAdminUrl_RubyRSpec` | Ruby + RSpec | `OpenMRS Clerk` | Passes on the Azure demo server (the clerk is redirected to login). Fails on the local 2.12.2 copy, where the clerk role is over-privileged (defect D1) |

Each test case follows Given / When / Then:
1. **Given:** log the scenario.
2. **When:** `Framework/RunSeleniumScenario.cs` reads the credential asset and starts the suite's `run-scenario.ps1`. The password goes to the process as an environment variable only.
3. **Then:** `Framework/ReportSeleniumResults.xaml` turns each JUnit test case into a Verify Expression named `<Scenario> | <Selenium test name>` and attaches the evidence. A last check verifies that the suite's exit code is 0.

## Arguments (Test Manager parameters)

| Argument | Default | Notes |
|---|---|---|
| `in_BaseUrl` | `http://uilabsopenmrs.westeurope.cloudapp.azure.com:8080/openmrs` | Local copy: `http://localhost:8081/openmrs-standalone` |
| `in_AssetFolder` | `OpenMRS` | Orchestrator folder that holds the persona credential assets. Leave it empty on an Orchestrator job to use the job's folder |
| `in_SeleniumRoot` | `..\SeleniumSuite` | The Selenium suites sit next to the project, the way a customer's would sit in their own repo. A relative path resolves against the project folder |
| `in_Headless` | `False` | `True` hides the browser |
| `in_PatientQuery` (S3, S4) | `Smith` | Name to search |
| `in_Persona` (S1) | `Clerk` | Filled from the data variation when run from Test Manager |

## Setup before running

See the repository's top-level `README.md`. In short:
1. **Toolchains:** `Setup\Install-Prerequisites.ps1` (JDK + Maven, .NET 8 SDK, Python 3, Node, Ruby 3 + bundler, Edge).
2. **Credential assets:** `OpenMRS Admin`, `OpenMRS Doctor`, `OpenMRS Nurse` and `OpenMRS Clerk` in folder `OpenMRS` of the tenant your Studio is connected to. Create them by hand or with `Setup\Set-OpenMRSCredentials.ps1`.
3. Edge is the browser. Chrome 157's driver drops input after the first page load.

Run one from the command line:

```powershell
uip rpa run-file --file-path "Tests\S3_FindPatient_PythonPytest.xaml" --project-dir . --input-arguments '{"in_BaseUrl":"http://localhost:8081/openmrs-standalone","in_Headless":true}'
```
