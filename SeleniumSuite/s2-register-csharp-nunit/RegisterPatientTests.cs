// S2 - Register a new patient (registration clerk).  C# + NUnit + Selenium.WebDriver.
//
// Creates a uniquely named test patient (family name "UiPathSel" + timestamp), checks the dashboard, and
// writes the new Patient ID to results/output.json so the UiPath workflow can verify it through the REST API.
// Settings come from environment variables (see the README).
using System.Text.Json;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;

namespace OpenMrsDemo;

[TestFixture]
public class RegisterPatientTests
{
    private IWebDriver _driver = null!;
    private WebDriverWait _wait = null!;
    private readonly string _baseUrl = Env("OMRS_BASE_URL", "http://localhost:8081/openmrs-standalone").TrimEnd('/');
    private static readonly string Results = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "results");

    private static string Env(string name, string? fallback = null) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : fallback ?? throw new InvalidOperationException($"Environment variable {name} is required");

    [SetUp]
    public void SignIn()
    {
        var headless = Env("OMRS_HEADLESS", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (Env("OMRS_BROWSER", "edge").Equals("chrome", StringComparison.OrdinalIgnoreCase))
        {
            var o = new ChromeOptions(); o.AddArgument("--window-size=1366,900");
            if (headless) o.AddArgument("--headless=new");
            _driver = new ChromeDriver(o);
        }
        else
        {
            var o = new EdgeOptions(); o.AddArgument("--window-size=1366,900");
            if (headless) o.AddArgument("--headless=new");
            _driver = new EdgeDriver(o);
        }
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(30));

        var location = Env("OMRS_LOCATION", "Registration Desk");
        _driver.Navigate().GoToUrl(_baseUrl + "/referenceapplication/login.page");
        _wait.Until(d => d.FindElement(By.Id("username"))).SendKeys(Env("OMRS_USERNAME", "clerk"));
        _driver.FindElement(By.Id("password")).SendKeys(Env("OMRS_PASSWORD"));
        var loc = _driver.FindElements(By.CssSelector("#sessionLocation li")).FirstOrDefault(li => li.Text.Trim() == location)
                  ?? throw new AssertionException($"Login location '{location}' not offered");
        loc.Click();
        _driver.FindElement(By.Id("loginButton")).Click();
        _wait.Until(d => d.Url.Contains("home.page"));
    }

    [TearDown]
    public void Screenshot()
    {
        var dir = Path.Combine(Results, "screenshots");
        Directory.CreateDirectory(dir);
        ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(Path.Combine(dir, TestContext.CurrentContext.Test.Name + ".png"));
        _driver.Quit();
    }

    private IWebElement Visible(By by) => _wait.Until(d => d.FindElements(by).FirstOrDefault(e => e.Displayed));
    // Registration steps slide in, so a field can be visible but not yet editable: retry until it takes the text.
    private void Type(string name, string value) => _wait.Until(d =>
    {
        try
        {
            var e = d.FindElements(By.CssSelector($"input[name='{name}']")).FirstOrDefault(x => x.Displayed && x.Enabled);  // a <label> can carry the same name
            if (e == null) return false;
            e.Clear(); e.SendKeys(value);
            return e.GetAttribute("value") == value;
        }
        catch (Exception ex) when (ex is InvalidElementStateException or ElementNotInteractableException or StaleElementReferenceException)
        {
            return false;
        }
    });
    private void Next() => _driver.FindElement(By.Id("next-button")).Click();

    [Test]
    public void RegisterNewPatientWithExactBirthdate()
    {
        var stamp = DateTime.Now.ToString("MMddHHmmss");
        var given = "Mia";
        var family = "UiPathSel" + stamp;

        Visible(By.XPath("//a[contains(., 'Register a patient')]")).Click();
        _wait.Until(d => d.Url.Contains("registerPatient"));

        // Demographics
        Type("givenName", given);
        Type("familyName", family);
        Next();
        new SelectElement(Visible(By.Name("gender"))).SelectByValue("F");
        Next();
        Type("birthdateDay", "14");
        new SelectElement(Visible(By.Name("birthdateMonth"))).SelectByValue("3");
        Type("birthdateYear", "1988");
        Next();

        // Contact info (the address step needs at least one field)
        Type("address1", "1 Demo Street");
        Type("cityVillage", "Minneapolis");
        Next();
        Type("phoneNumber", "6125550100");
        Next();

        // Relationships: none, then Confirm
        Next();
        var confirm = Visible(By.Id("confirmation"));
        Assert.That(confirm.Text, Does.Contain(given), "Confirm page does not show the given name");
        Assert.That(confirm.Text, Does.Contain(family), "Confirm page does not show the family name");
        Visible(By.Id("submit")).Click();

        // The new patient's dashboard opens with a Patient ID
        _wait.Until(d => d.Url.Contains("patient.page"));
        var header = Visible(By.CssSelector(".patient-header")).Text;
        Assert.That(header, Does.Contain(given));
        Assert.That(header, Does.Contain(family));
        var idText = Visible(By.CssSelector(".patient-header .identifiers span, .patient-header .identifiers")).Text.Trim();
        var patientId = idText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
        Assert.That(patientId, Is.Not.Empty, "No Patient ID on the dashboard");

        Directory.CreateDirectory(Results);
        File.WriteAllText(Path.Combine(Results, "output.json"), JsonSerializer.Serialize(new
        {
            patientId,
            givenName = given,
            familyName = family,
            gender = "F",
            birthdate = "1988-03-14",
            patientUuid = new Uri(_driver.Url).Query.TrimStart('?').Split('&')
                .FirstOrDefault(p => p.StartsWith("patientId="))?.Split('=')[1]
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine($"Registered {given} {family} as {patientId}");
    }
}
