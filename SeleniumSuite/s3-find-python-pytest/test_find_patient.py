"""S3 - Find a patient by name, then by OpenMRS ID (nurse).  Python + pytest."""
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


def test_find_patient_by_name_then_id(driver, wait, login, config):
    login()
    query = config["patient_query"]

    # 1. Search by name: every row returned must match the name
    box = open_find_patient(driver, wait)
    box.send_keys(query)
    wait.until(lambda d: rows(d) and all(query.lower() in r.text.lower() for r in rows(d)))

    def first_row(d):
        cells = rows(d)[0].find_elements(By.TAG_NAME, "td")
        # a "Recent" label sits next to recently viewed IDs
        return cells[0].text.replace("Recent", "").strip(), cells[1].text.strip()
    patient_id, patient_name = wait.until(first_row)
    assert patient_id, "First search result has no Patient ID"

    # 2. Search by that ID: exactly one match
    box.clear()
    box.send_keys(patient_id)
    wait.until(lambda d: len(rows(d)) == 1 and patient_id in rows(d)[0].text)

    # 3. Open the patient: the dashboard header shows the same name and ID
    def open_row(d):
        rows(d)[0].click()
        return True
    wait.until(open_row)
    wait.until(EC.url_contains("patient.page"))
    header = wait.until(EC.visibility_of_element_located((By.CSS_SELECTOR, ".patient-header"))).text
    assert patient_id in header, f"Dashboard header does not show {patient_id}: {header!r}"
    for part in patient_name.split():
        assert part in header, f"Dashboard header does not show '{part}': {header!r}"

    write_output({"patientId": patient_id, "patientName": patient_name, "query": query})


def test_search_with_no_match(driver, wait, login):
    login()
    box = open_find_patient(driver, wait)
    box.send_keys("Zzqxnomatch")
    wait.until(lambda d: "No matching records found" in d.find_element(By.ID, "patient-search-results").text)
