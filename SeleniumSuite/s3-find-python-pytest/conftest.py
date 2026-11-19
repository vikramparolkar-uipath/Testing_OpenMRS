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
    drv = webdriver.Chrome(options=options) if browser == "chrome" else webdriver.Edge(options=options)
    yield drv
    SHOTS.mkdir(parents=True, exist_ok=True)
    drv.save_screenshot(str(SHOTS / f"{re.sub(r'[^A-Za-z0-9_-]', '_', request.node.name)}.png"))
    drv.quit()


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


def write_output(data):
    """Values Studio can read back after the run (for example to check them through the REST API)."""
    RESULTS.mkdir(parents=True, exist_ok=True)
    (RESULTS / "output.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
