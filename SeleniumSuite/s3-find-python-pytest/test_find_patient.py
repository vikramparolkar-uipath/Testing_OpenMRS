"""S3 - Find a patient by name, then by OpenMRS ID (nurse).  Python + pytest.

Assertions are named checkpoints (see checkpoints.py): each one takes its own screenshot and is reported
individually, so Test Manager shows one assertion per checkpoint.
"""
from selenium.common.exceptions import TimeoutException
from selenium.webdriver.common.by import By
from selenium.webdriver.support import expected_conditions as EC

from conftest import write_output

RESULT_ROWS = "#patient-search-results-table tbody tr"


def open_find_patient(driver, wait):
    wait.until(EC.element_to_be_clickable((By.XPATH, "//a[contains(., 'Find Patient Record')]"))).click()
    wait.until(EC.url_contains("findPatient"))
    return wait.until(EC.element_to_be_clickable((By.ID, "patient-search")))


def rows(driver):
    return driver.find_elements(By.CSS_SELECTOR, RESULT_ROWS)


def waited(wait, condition):
    """True if the condition came true within the wait timeout (a checkpoint then records the outcome)."""
    try:
        return wait.until(condition)
    except TimeoutException:
        return False


def test_find_patient_by_name_then_id(driver, wait, login, config, check):
    check.plan([
        "Name search returns only matching patients",
        "First result has a Patient ID",
        "ID search returns exactly one match",
        "Dashboard header shows the Patient ID",
        "Dashboard header shows the patient's name",
    ])
    login()
    query = config["patient_query"]

    # 1. Search by name: every row returned must match the name
    box = open_find_patient(driver, wait)
    box.send_keys(query)
    check("Name search returns only matching patients",
          waited(wait, lambda d: rows(d) and all(query.lower() in r.text.lower() for r in rows(d))),
          f"No result list where every row contains '{query}'")

    def first_row(d):
        cells = rows(d)[0].find_elements(By.TAG_NAME, "td")
        # a "Recent" label sits next to recently viewed IDs
        return cells[0].text.replace("Recent", "").strip(), cells[1].text.strip()
    patient_id, patient_name = waited(wait, first_row) or ("", "")
    check("First result has a Patient ID", patient_id, "The first search result shows no Patient ID")

    # 2. Search by that ID: exactly one match
    box.clear()
    box.send_keys(patient_id)
    check("ID search returns exactly one match",
          waited(wait, lambda d: len(rows(d)) == 1 and patient_id in rows(d)[0].text),
          f"Searching '{patient_id}' did not return exactly one matching row")

    # 3. Open the patient: the dashboard header shows the same ID and name
    def open_row(d):
        rows(d)[0].click()
        return True
    wait.until(open_row)
    wait.until(EC.url_contains("patient.page"))
    header = wait.until(EC.visibility_of_element_located((By.CSS_SELECTOR, ".patient-header"))).text
    check("Dashboard header shows the Patient ID", patient_id in header,
          f"Header does not show {patient_id}: {header!r}")
    missing = [part for part in patient_name.split() if part not in header]
    check("Dashboard header shows the patient's name", not missing,
          f"Header is missing {missing}: {header!r}")

    write_output({"patientId": patient_id, "patientName": patient_name, "query": query})


def test_search_with_no_match(driver, wait, login, check):
    check.plan(["Search for an unknown name shows 'No matching records found'"])
    login()
    box = open_find_patient(driver, wait)
    box.send_keys("Zzqxnomatch")
    check("Search for an unknown name shows 'No matching records found'",
          waited(wait, lambda d: "No matching records found" in d.find_element(By.ID, "patient-search-results").text),
          "The no-match message did not appear")
