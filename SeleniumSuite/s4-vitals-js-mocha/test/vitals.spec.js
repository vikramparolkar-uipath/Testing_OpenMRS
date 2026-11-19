// S4 - Vitals out-of-range validation (nurse).  JavaScript + Mocha + selenium-webdriver.
//
// Vitals need an active visit. The test starts one if the patient has none, enters out-of-range values,
// leaves the form WITHOUT saving, and ends the visit again if it started it.
// Settings come from environment variables (see the README): OMRS_BASE_URL, OMRS_USERNAME, OMRS_PASSWORD,
// OMRS_LOCATION, OMRS_PATIENT_QUERY, OMRS_BROWSER, OMRS_HEADLESS.

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { Builder, By, until } = require('selenium-webdriver');
const edge = require('selenium-webdriver/edge');
const chrome = require('selenium-webdriver/chrome');

const cfg = {
  baseUrl: (process.env.OMRS_BASE_URL || 'http://localhost:8081/openmrs-standalone').replace(/\/$/, ''),
  username: process.env.OMRS_USERNAME || 'nurse',
  password: process.env.OMRS_PASSWORD,
  location: process.env.OMRS_LOCATION || 'Inpatient Ward',
  patientQuery: process.env.OMRS_PATIENT_QUERY || 'Smith',
  browser: (process.env.OMRS_BROWSER || 'edge').toLowerCase(),
  headless: (process.env.OMRS_HEADLESS || 'false').toLowerCase() === 'true',
};
const SHOTS = path.join(__dirname, '..', 'results', 'screenshots');
const TIMEOUT = 30000;

describe('S4 Vitals out-of-range validation', function () {
  let driver;
  let startedVisit = false;

  before(async function () {
    if (!cfg.password) throw new Error('Environment variable OMRS_PASSWORD is required');
    const args = ['--window-size=1366,900'].concat(cfg.headless ? ['--headless=new'] : []);
    const builder = new Builder().forBrowser(cfg.browser === 'chrome' ? 'chrome' : 'MicrosoftEdge');
    if (cfg.browser === 'chrome') builder.setChromeOptions(new chrome.Options().addArguments(...args));
    else builder.setEdgeOptions(new edge.Options().addArguments(...args));
    driver = await builder.build();
  });

  afterEach(async function () {
    fs.mkdirSync(SHOTS, { recursive: true });
    const name = this.currentTest.title.replace(/[^A-Za-z0-9_-]/g, '_');
    fs.writeFileSync(path.join(SHOTS, `${name}.png`), await driver.takeScreenshot(), 'base64');
  });

  after(async function () {
    // Clean-up even when an assertion failed: end only the visit this test started.
    try {
      if (startedVisit) {
        const exit = await visible(By.xpath("//a[normalize-space()='Exit Form']"), 2000).catch(() => null);
        if (exit) { await exit.click(); await driver.wait(async () => !(await driver.getCurrentUrl()).includes('htmlform'), TIMEOUT); }
        await clickText('End Visit');
        await clickDialogButton('Yes');
        await visible(By.xpath("//*[self::a or self::button][normalize-space()='Start Visit']"));
      }
    } finally {
      if (driver) await driver.quit();
    }
  });

  // ---------- helpers ----------
  async function visible(locator, timeout = TIMEOUT) {
    return driver.wait(async () => {
      for (const el of await driver.findElements(locator)) if (await el.isDisplayed()) return el;
      return null;
    }, timeout);
  }
  async function clickText(text) {
    await (await visible(By.xpath(`//*[self::a or self::button][normalize-space()='${text}']`))).click();
  }
  async function clickDialogButton(text) {
    await (await visible(By.xpath(`//*[contains(@class,'dialog')]//button[normalize-space()='${text}']`))).click();
  }
  async function type(id, value) {
    const el = await visible(By.id(id));
    await el.clear();
    await el.sendKeys(value);
  }
  async function next() { await driver.findElement(By.id('next-button')).click(); }
  async function fieldErrors() {
    const texts = [];
    for (const el of await driver.findElements(By.css('.field-error'))) {
      if (await el.isDisplayed()) { const t = (await el.getText()).trim(); if (t) texts.push(t); }
    }
    return texts;
  }

  // ---------- test ----------
  it('rejects temperature 50 and pulse 400 with the range message', async function () {
    // Sign in (every scenario signs in on its own)
    await driver.get(`${cfg.baseUrl}/referenceapplication/login.page`);
    await (await visible(By.id('username'))).sendKeys(cfg.username);
    await driver.findElement(By.id('password')).sendKeys(cfg.password);
    let picked = false;
    for (const li of await driver.findElements(By.css('#sessionLocation li'))) {
      if ((await li.getText()).trim() === cfg.location) { await li.click(); picked = true; break; }
    }
    assert.ok(picked, `Login location '${cfg.location}' not offered`);
    await driver.findElement(By.id('loginButton')).click();
    await driver.wait(until.urlContains('home.page'), TIMEOUT);

    // Find the patient and open the dashboard
    await (await visible(By.xpath("//a[contains(., 'Find Patient Record')]"))).click();
    const box = await visible(By.id('patient-search'));
    await box.sendKeys(cfg.patientQuery);
    const rows = () => driver.findElements(By.css('#patient-search-results-table tbody tr'));
    await driver.wait(async () => {
      try {   // the results table redraws while the search runs, so rows can go stale mid-check
        const r = await rows();
        if (!r.length) return false;
        for (const row of r) if (!(await row.getText()).toLowerCase().includes(cfg.patientQuery.toLowerCase())) return false;
        return true;
      } catch (e) { if (e.name === 'StaleElementReferenceError') return false; throw e; }
    }, TIMEOUT);
    await (await rows())[0].click();
    await driver.wait(until.urlContains('patient.page'), TIMEOUT);
    await visible(By.css('.patient-header'));

    // Start a visit if there is none (vitals need a checked-in patient)
    const startVisit = await visible(By.xpath("//a[normalize-space()='Start Visit']"), 3000).catch(() => null);
    if (startVisit) {
      await startVisit.click();
      await clickDialogButton('Confirm');
      startedVisit = true;
      await visible(By.xpath("//*[self::a or self::button][normalize-space()='End Visit']"));
    }

    // Capture Vitals form
    await clickText('Capture Vitals');
    await type('w8', '168'); await next();           // height (cm)
    await type('w10', '64'); await next();           // weight (kg)
    await visible(By.id('next-button')); await next(); // calculated BMI step

    await type('w12', '50'); await next();           // temperature 50 C
    assert.ok((await fieldErrors()).includes('Maximum: 43'), `Expected 'Maximum: 43', got ${JSON.stringify(await fieldErrors())}`);
    assert.ok(await driver.findElement(By.id('w12')).isDisplayed(), 'Form moved past an out-of-range temperature');

    await type('w12', '37'); await next();
    await type('w14', '400'); await next();          // pulse 400 /min
    assert.ok((await fieldErrors()).includes('Maximum: 230'), `Expected 'Maximum: 230', got ${JSON.stringify(await fieldErrors())}`);
    assert.ok(await driver.findElement(By.id('w14')).isDisplayed(), 'Form moved past an out-of-range pulse');
  });
});
