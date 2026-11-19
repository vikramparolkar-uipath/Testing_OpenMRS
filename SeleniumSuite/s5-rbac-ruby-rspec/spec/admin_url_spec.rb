# S5 - Registration clerk must not reach an admin page by URL.  Ruby + RSpec + selenium-webdriver.
#
# EXPECTED TO FAIL on the demo instance: the clerk role carries admin privileges (defect D1), so the
# Manage Accounts page opens. The first example passes (the tile is hidden), the second one fails,
# which gives the dashboard a real defect to show.
# Settings come from environment variables (see the README).
require "fileutils"
require "selenium-webdriver"

CFG = {
  base_url: (ENV["OMRS_BASE_URL"] || "http://localhost:8081/openmrs-standalone").chomp("/"),
  username: ENV["OMRS_USERNAME"] || "clerk",
  password: ENV["OMRS_PASSWORD"] || raise("Environment variable OMRS_PASSWORD is required"),
  location: ENV["OMRS_LOCATION"] || "Registration Desk",
  browser: (ENV["OMRS_BROWSER"] || "edge").downcase,
  headless: (ENV["OMRS_HEADLESS"] || "false").downcase == "true"
}.freeze
SHOTS = File.join(__dir__, "..", "results", "screenshots")

RSpec.describe "S5 Clerk cannot open admin pages" do
  let(:wait) { Selenium::WebDriver::Wait.new(timeout: 30) }

  # A fresh browser and sign-in for every example, so each one stands alone
  before do
    args = ["--window-size=1366,900"]
    args << "--headless=new" if CFG[:headless]
    @driver = if CFG[:browser] == "chrome"
                Selenium::WebDriver.for(:chrome, options: Selenium::WebDriver::Chrome::Options.new(args: args))
              else
                Selenium::WebDriver.for(:edge, options: Selenium::WebDriver::Edge::Options.new(args: args))
              end
    sign_in
  end

  after do |example|
    FileUtils.mkdir_p(SHOTS)
    @driver.save_screenshot(File.join(SHOTS, "#{example.description.gsub(/[^A-Za-z0-9_-]/, '_')}.png"))
    @driver.quit
  end

  def sign_in
    @driver.get("#{CFG[:base_url]}/referenceapplication/login.page")
    wait.until { @driver.find_element(id: "username").displayed? }
    @driver.find_element(id: "username").send_keys(CFG[:username])
    @driver.find_element(id: "password").send_keys(CFG[:password])
    location = @driver.find_elements(css: "#sessionLocation li").find { |li| li.text.strip == CFG[:location] }
    raise "Login location '#{CFG[:location]}' not offered" unless location
    location.click
    @driver.find_element(id: "loginButton").click
    wait.until { @driver.current_url.include?("home.page") }
  end

  it "does not show the System Administration tile" do
    tiles = @driver.find_elements(css: "#apps a").map { |a| a.text.strip }.reject(&:empty?)
    expect(tiles).not_to be_empty
    expect(tiles).not_to include("System Administration")
  end

  it "blocks Manage Accounts opened by URL" do
    @driver.get("#{CFG[:base_url]}/adminui/systemadmin/accounts/manageAccounts.page")
    wait.until { @driver.execute_script("return document.readyState") == "complete" }
    body = @driver.find_element(tag_name: "body").text
    landed = @driver.current_url.sub(CFG[:base_url], "")
    puts "    Opened Manage Accounts URL as #{CFG[:username]}; browser ended on #{landed}"
    # Blocked = the admin page's own content is not shown (redirect, error page or login all count)
    admin_page_shown = body.include?("Add New Account") || body =~ /Manage Accounts\s+Add New Account/
    expect(admin_page_shown).to be_falsey,
      "Clerk reached Manage Accounts (#{@driver.current_url.sub(CFG[:base_url], '')}). " \
      "Page shows: #{body.gsub(/\s+/, ' ')[0, 160]}"
  end
end
