# OpenMRS Selenium demo – five scenarios, five languages

These are existing-style Selenium tests that a UiPath Studio workflow invokes **as they are**. UiPath then puts
their results in Test Manager (central dashboard, scheduling, reporting) without anyone rebuilding them.

Each scenario is independent and **signs in on its own**, so any one of them can run alone or in any order.

| # | Scenario | Language / framework | Persona | Tests | Result on the demo build |
|---|---|---|---|---|---|
| S1 | Login smoke: home page, header, role tiles | Java 21 + TestNG (Maven) | any: admin, doctor, nurse, clerk | 3 | Pass for all four personas |
| S2 | Register a new patient | C# .NET 8 + NUnit | clerk | 1 | Pass. Writes the new Patient ID to `output.json` |
| S3 | Find a patient by name, then by ID; no-match search | Python 3.13 + pytest | nurse | 2 | Pass. Writes the found patient to `output.json` |
| S4 | Vitals out-of-range check (temperature 50, pulse 400) | JavaScript (Node 24) + Mocha | nurse | 1 | Pass |
| S5 | Clerk must not reach an admin page by URL | Ruby 3.3 + RSpec | clerk | 2 | Azure demo server: 2 pass (the clerk is redirected to login). Local 2.12.2 copy: 1 pass, 1 FAIL on defect D1 |

Run on 2026-11-19 against the local OpenMRS 2.12.2 copy, headless Edge: 9 tests, 8 passed, 1 failed (S5, defect D1). From the UiPath test cases against the Azure demo server: all five pass. That is S1 for all four personas (3 tests each), S2 (1 Selenium test plus 6 UiPath checks, including 4 REST cross-checks), S3 (2), S4 (1) and S5 (2).
Each scenario takes 15–25 seconds.

## The same contract for every scenario

**Inputs** come from environment variables. The caller sets them, and the password never goes on a command line.

| Variable | Meaning | Default |
|---|---|---|
| `OMRS_BASE_URL` | OpenMRS root | `http://localhost:8081/openmrs-standalone`. For the demo use `http://uilabsopenmrs.westeurope.cloudapp.azure.com:8080/openmrs` |
| `OMRS_USERNAME` | Persona user name | Per scenario (see the table) |
| `OMRS_PASSWORD` | Persona password | **Required.** Take it from the Orchestrator credential asset (`OpenMRS Clerk` and the others) |
| `OMRS_LOCATION` | Login location | admin/doctor: Outpatient Clinic · nurse: Inpatient Ward · clerk: Registration Desk |
| `OMRS_PATIENT_QUERY` | Name to search (S3, S4) | `Smith` |
| `OMRS_BROWSER` | `edge` or `chrome` | `edge` |
| `OMRS_HEADLESS` | `true` hides the browser | `false` (so the audience can watch) |

**Outputs**, in each scenario folder:
- `results\junit.xml`: JUnit-style results (every framework produces this)
- `results\screenshots\*.png`: one screenshot per test, pass or fail
- `results\output.json`: values to hand on (S2 and S3 only)
- Exit code `0` = all passed, non-zero = at least one failure

## Running them

One entry point for all five. This is what the Studio workflow calls:

```powershell
$env:OMRS_USERNAME = 'nurse'; $env:OMRS_PASSWORD = '<from credential asset>'
powershell -ExecutionPolicy Bypass -File run-scenario.ps1 -Scenario S3
```

Each scenario's native command is what `run-scenario.ps1` runs. These are the commands Capgemini would already use:

| # | Folder | Native command |
|---|---|---|
| S1 | `s1-login-java-testng` | `mvn test` (suite in `testng.xml`) |
| S2 | `s2-register-csharp-nunit` | `dotnet test --logger "junit;LogFilePath=results/junit.xml"` |
| S3 | `s3-find-python-pytest` | `.venv\Scripts\python -m pytest` |
| S4 | `s4-vitals-js-mocha` | `npm test` |
| S5 | `s5-rbac-ruby-rspec` | `bundle exec rspec` |

**Toolchains on this machine:**
- JDK 21: Temurin
- Maven 3.9.16: in `%USERPROFILE%\tools`
- .NET 8 SDK
- Python 3.13
- Node 24
- Ruby 3.3: `C:\Ruby33-x64`

`run-scenario.ps1` finds Maven, the JDK and Ruby even when a new install isn't on PATH yet. On the first run it creates the Python venv and runs `npm install` and `bundle install`. Selenium Manager downloads the matching browser driver automatically.

## Wiring into UiPath (outline)

One Studio test case per scenario (S1 is data-driven: one Test Manager data row per persona):
1. **Get Credential**: the persona's Orchestrator asset (`OpenMRS Clerk`, `OpenMRS Nurse`, …).
2. **Invoke Code** (or Invoke PowerShell): start `run-scenario.ps1 -Scenario Sx` with `ProcessStartInfo`. Set the `OMRS_*` values in `EnvironmentVariables` and wait for exit.
3. **Read** `results\junit.xml` and log each test case's name and outcome. Attach the screenshots to the Test Manager result.
4. **Verify** the exit code is 0, so the Test Manager result is pass or fail.
5. **S2 extra:** read `output.json` and call `GET /ws/rest/v1/patient?q=<patientId>` with an HTTP Request. Check that name, gender and birthdate match. Selenium doesn't do that check; UiPath adds it.

Test Manager then gives one dashboard for all five languages. An Orchestrator trigger or a Test Manager test-set schedule runs them. Any failure flows into defect logging (Jira).

## Things to know before the demo

- **Edge is the default on purpose.** With Chrome 157 and its matching chromedriver, keyboard and mouse input stops reaching the page after the first navigation, so a script can sign in but can't type anything afterwards. Edge 157 works. `OMRS_BROWSER=chrome` is there once a fixed chromedriver ships.
- **S2 creates a real patient every run** (`Mia UiPathSel<MMddHHmmss>`). That's harmless on the demo server, but it adds records.
- **S4 needs an active visit.** If the patient has none, S4 starts one, enters the out-of-range values, leaves the form **without saving**, and ends the visit it started. That leaves an empty, ended visit on that patient. To keep demo patients untouched, set `OMRS_PATIENT_QUERY` to a patient that S2 created.
- **S5 depends on the server.** On the local 2.12.2 copy it fails on defect D1: the Registration Clerk role carries 258 privileges, so Manage Accounts opens by URL. On the Azure demo server, checked 2026-11-19, the clerk is redirected to `/login.htm`, so it passes. The console log records where the browser ended up.
- **Azure instance:** it is the same Reference Application 2.x as the local copy. Check before the call that its personas, login locations and a `Smith` patient exist, since the scripts so far have only run against the local copy.
- Test data created: local `1003KK` plus one more `Mia UiPathSel…`; on Azure, `100LK2` (Mia UiPathSel1119124133) from the S2 run on 2026-11-19, and one ended, empty visit on the first "Smith" patient from S4.
