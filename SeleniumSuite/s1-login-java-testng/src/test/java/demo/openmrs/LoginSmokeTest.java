package demo.openmrs;

import org.openqa.selenium.By;
import org.openqa.selenium.OutputType;
import org.openqa.selenium.TakesScreenshot;
import org.openqa.selenium.WebDriver;
import org.openqa.selenium.WebElement;
import org.openqa.selenium.chrome.ChromeDriver;
import org.openqa.selenium.chrome.ChromeOptions;
import org.openqa.selenium.edge.EdgeDriver;
import org.openqa.selenium.edge.EdgeOptions;
import org.openqa.selenium.support.ui.ExpectedConditions;
import org.openqa.selenium.support.ui.WebDriverWait;
import org.testng.ITestResult;
import org.testng.annotations.AfterClass;
import org.testng.annotations.AfterMethod;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.time.Duration;
import java.util.*;
import java.util.stream.Collectors;

import static org.testng.Assert.*;

/**
 * S1 - Login smoke for one persona (admin, doctor, nurse or clerk).  Java + TestNG.
 *
 * Data-driven from outside: Test Manager runs it once per persona row, passing the persona through
 * environment variables (OMRS_USERNAME, OMRS_PASSWORD, OMRS_LOCATION). Signs in once per run, then
 * three dependent checks give three results in the report.
 */
public class LoginSmokeTest {

    /** Home page tiles each persona must see (verified on Reference Application 2.x demo data). */
    private static final Map<String, List<String>> EXPECTED_TILES = Map.of(
        "admin", List.of("Find Patient Record", "Active Visits", "Register a patient", "Capture Vitals",
                         "Appointment Scheduling", "Reports", "Data Management", "Configure Metadata",
                         "System Administration"),
        "doctor", List.of("Find Patient Record", "Active Visits", "Appointment Scheduling"),
        "nurse", List.of("Find Patient Record", "Active Visits", "Capture Vitals", "Appointment Scheduling"),
        "clerk", List.of("Active Visits", "Register a patient", "Appointment Scheduling"));

    /** Tiles only an administrator may see. */
    private static final List<String> ADMIN_ONLY =
        List.of("Reports", "Data Management", "Configure Metadata", "System Administration");

    private static final Map<String, String> DEFAULT_LOCATION = Map.of(
        "admin", "Outpatient Clinic", "doctor", "Outpatient Clinic",
        "nurse", "Inpatient Ward", "clerk", "Registration Desk");

    private WebDriver driver;
    private WebDriverWait wait;
    private String baseUrl, username, password, location;

    private static String env(String name, String fallback) {
        String v = System.getenv(name);
        if (v == null || v.isBlank()) {
            if (fallback == null) throw new IllegalStateException("Environment variable " + name + " is required");
            return fallback;
        }
        return v;
    }

    @BeforeClass
    public void signIn() {
        baseUrl = env("OMRS_BASE_URL", "http://localhost:8081/openmrs-standalone").replaceAll("/$", "");
        username = env("OMRS_USERNAME", "clerk");
        password = env("OMRS_PASSWORD", null);
        location = env("OMRS_LOCATION", DEFAULT_LOCATION.getOrDefault(username, "Outpatient Clinic"));

        boolean headless = env("OMRS_HEADLESS", "false").equalsIgnoreCase("true");
        if (env("OMRS_BROWSER", "edge").equalsIgnoreCase("chrome")) {
            ChromeOptions o = new ChromeOptions().addArguments("--window-size=1366,900");
            if (headless) o.addArguments("--headless=new");
            driver = new ChromeDriver(o);
        } else {
            EdgeOptions o = new EdgeOptions().addArguments("--window-size=1366,900");
            if (headless) o.addArguments("--headless=new");
            driver = new EdgeDriver(o);
        }
        wait = new WebDriverWait(driver, Duration.ofSeconds(30));

        driver.get(baseUrl + "/referenceapplication/login.page");
        wait.until(ExpectedConditions.visibilityOfElementLocated(By.id("username"))).sendKeys(username);
        driver.findElement(By.id("password")).sendKeys(password);
        List<WebElement> locations = driver.findElements(By.cssSelector("#sessionLocation li"));
        WebElement loc = locations.stream().filter(li -> li.getText().trim().equals(location)).findFirst()
            .orElseThrow(() -> new AssertionError("Login location '" + location + "' not offered"));
        loc.click();
        driver.findElement(By.id("loginButton")).click();
    }

    @Test(description = "Sign-in lands on the home page")
    public void homePageReached() {
        wait.until(ExpectedConditions.urlContains("home.page"));
        assertTrue(driver.getCurrentUrl().contains("home.page"), "Not on the home page: " + driver.getCurrentUrl());
    }

    @Test(dependsOnMethods = "homePageReached", description = "Header shows the user and the session location")
    public void headerShowsUserAndLocation() {
        String header = driver.findElement(By.tagName("header")).getText();
        assertTrue(header.contains(username), "Header does not show user '" + username + "': " + header);
        assertTrue(header.contains(location), "Header does not show location '" + location + "': " + header);
    }

    @Test(dependsOnMethods = "homePageReached", description = "Home page tiles match the persona's role")
    public void tilesMatchRole() {
        Set<String> tiles = driver.findElements(By.cssSelector("#apps a")).stream()
            .map(e -> e.getText().trim()).filter(t -> !t.isEmpty()).collect(Collectors.toCollection(TreeSet::new));
        List<String> expected = EXPECTED_TILES.get(username);
        assertNotNull(expected, "No expected tiles defined for persona '" + username + "'");

        List<String> missing = expected.stream().filter(t -> !tiles.contains(t)).collect(Collectors.toList());
        assertTrue(missing.isEmpty(), "Missing tiles " + missing + "; page shows " + tiles);

        if (!username.equals("admin")) {
            List<String> leaked = ADMIN_ONLY.stream().filter(tiles::contains).collect(Collectors.toList());
            assertTrue(leaked.isEmpty(), "Admin-only tiles visible to " + username + ": " + leaked);
        }
    }

    @AfterMethod(alwaysRun = true)
    public void screenshot(ITestResult result) throws Exception {
        Path dir = Paths.get("results", "screenshots");
        Files.createDirectories(dir);
        Path shot = ((TakesScreenshot) driver).getScreenshotAs(OutputType.FILE).toPath();
        Files.copy(shot, dir.resolve(result.getMethod().getMethodName() + ".png"), StandardCopyOption.REPLACE_EXISTING);
    }

    @AfterClass(alwaysRun = true)
    public void quit() {
        if (driver != null) driver.quit();
    }
}
