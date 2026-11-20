"""Shared pytest fixtures: browser per test, sign-in helper, screenshot after every test.

Configuration comes from environment variables, so UiPath Studio (or a CI job) can pass Test Manager data
and Orchestrator credential assets without touching the code:
  OMRS_BASE_URL       default http://localhost:8081/openmrs-standalone
  OMRS_USERNAME       default nurse
  OMRS_PASSWORD       required
  OMRS_LOCATION       default Inpatient Ward
  OMRS_PATIENT_QUERY  default Smith
  OMRS_BROWSER        edge (default) | chrome
  OMRS_HEADLESS       true | false (default false, so the demo audience sees the browser)
"""
import json
import os
import re
from pathlib import Path

import pytest
from selenium import webdriver
from selenium.common.exceptions import StaleElementReferenceException
from selenium.webdriver.common.by import By
from selenium.webdriver.support import expected_conditions as EC
from selenium.webdriver.support.ui import WebDriverWait

import checkpoints

RESULTS = Path(__file__).parent / "results"
SHOTS = RESULTS / "screenshots"


def env(name, default=None):
    value = os.environ.get(name, default)
    if value is None:
        raise RuntimeError(f"Environment variable {name} is required")
    return value


@pytest.fixture(scope="session")
def config():
    return {
        "base_url": env("OMRS_BASE_URL", "http://localhost:8081/openmrs-standalone").rstrip("/"),
        "username": env("OMRS_USERNAME", "nurse"),
        "password": env("OMRS_PASSWORD"),
        "location": env("OMRS_LOCATION", "Inpatient Ward"),
        "patient_query": env("OMRS_PATIENT_QUERY", "Smith"),
    }


@pytest.fixture
def driver(request):
    browser = os.environ.get("OMRS_BROWSER", "edge").lower()
    options = webdriver.ChromeOptions() if browser == "chrome" else webdriver.EdgeOptions()
    options.add_argument("--window-size=1366,900")
    if os.environ.get("OMRS_HEADLESS", "false").lower() == "true":
        options.add_argument("--headless=new")
    # Selenium's own logging: the browser console (JavaScript errors) and the driver log (every WebDriver command)
    options.set_capability("goog:loggingPrefs" if browser == "chrome" else "ms:loggingPrefs", {"browser": "ALL"})
    RESULTS.mkdir(parents=True, exist_ok=True)
    driver_log = str(RESULTS / "webdriver.log")
    service_args = ["--log-level=INFO", "--append-log"]
    if browser == "chrome":
        drv = webdriver.Chrome(options=options, service=webdriver.ChromeService(log_output=driver_log, service_args=service_args))
    else:
        drv = webdriver.Edge(options=options, service=webdriver.EdgeService(log_output=driver_log, service_args=service_args))
    checkpoints.trail(f"{request.node.name} | started ({browser}, {drv.capabilities.get('browserVersion', '?')})")
    yield drv
    SHOTS.mkdir(parents=True, exist_ok=True)
    drv.save_screenshot(str(SHOTS / f"{re.sub(r'[^A-Za-z0-9_-]', '_', request.node.name)}.png"))
    try:
        entries = drv.get_log("browser")
        with (RESULTS / "browser-console.log").open("a", encoding="utf-8") as f:
            f.write(f"===== {request.node.name} ({len(entries)} entries)\n")
            for e in entries:
                f.write(f"{e.get('level')}  {e.get('message')}\n")
    except Exception as ex:          # not every browser/driver exposes the console log
        checkpoints.trail(f"{request.node.name} | browser console log not available: {type(ex).__name__}")
    outcome = "FAILED" if getattr(request.node, "rep_call", None) and request.node.rep_call.failed else "PASSED"
    checkpoints.trail(f"{request.node.name} | finished: {outcome}")
    drv.quit()


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item, call):
    """Make each test's outcome available to its fixtures (for the execution trail)."""
    outcome = yield
    report = outcome.get_result()
    if report.when == "call":
        item.rep_call = report


@pytest.fixture
def wait(driver):
    # The search results table redraws while results arrive, so rows can go stale mid-check: retry instead of failing
    return WebDriverWait(driver, 30, ignored_exceptions=[StaleElementReferenceException])


@pytest.fixture
def login(driver, wait, config):
    """Every test signs in on its own, so each one can run alone."""
    def _login():
        driver.get(config["base_url"] + "/referenceapplication/login.page")
        wait.until(EC.visibility_of_element_located((By.ID, "username"))).send_keys(config["username"])
        driver.find_element(By.ID, "password").send_keys(config["password"])
        locations = driver.find_elements(By.CSS_SELECTOR, "#sessionLocation li")
        match = [li for li in locations if li.text.strip() == config["location"]]
        assert match, f"Login location '{config['location']}' not offered: {[li.text for li in locations]}"
        match[0].click()
        driver.find_element(By.ID, "loginButton").click()
        wait.until(EC.url_contains("home.page"))
    return _login


@pytest.fixture(scope="session", autouse=True)
def _fresh_checkpoint_log():
    checkpoints.reset()
    log = RESULTS / "webdriver.log"
    if log.exists():
        log.unlink()
    yield
    # The driver log records sendKeys text, including the password typed at login: redact it before anyone reads it
    secret = os.environ.get("OMRS_PASSWORD")
    if secret and log.exists():
        log.write_text(log.read_text(encoding="utf-8", errors="replace").replace(secret, "********"), encoding="utf-8")


@pytest.fixture
def check(driver, request):
    """Named checkpoint with its own screenshot: check("what is true", condition, "detail if not")."""
    cp = checkpoints.Checkpoints(driver, request.node.name)
    yield cp
    cp.close()


def write_output(data):
    """Values Studio can read back after the run (for example to check them through the REST API)."""
    RESULTS.mkdir(parents=True, exist_ok=True)
    (RESULTS / "output.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
