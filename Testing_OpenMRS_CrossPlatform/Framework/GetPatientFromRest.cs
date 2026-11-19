using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UiPath.CodedWorkflows;

namespace Testing_OpenMRS_CrossPlatform
{
    /// <summary>
    /// Looks a patient up through the OpenMRS REST API by Patient ID and returns what the server stored,
    /// so a test can compare it with what the Selenium script typed into the UI.
    /// </summary>
    public class GetPatientFromRest : CodedWorkflow
    {
        [Workflow]
        public (bool out_Found, string out_FullName, string out_Gender, string out_Birthdate) Execute(
            string in_BaseUrl,
            string in_CredentialAsset,
            string in_AssetFolder,
            string in_PatientId)
        {
            var (userName, password) = system.GetCredential(in_CredentialAsset,
                string.IsNullOrWhiteSpace(in_AssetFolder) ? null : in_AssetFolder, timeoutMS: 30000);
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                userName + ":" + new NetworkCredential(string.Empty, password).Password));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            string url = in_BaseUrl.TrimEnd('/') + "/ws/rest/v1/patient?q=" + Uri.EscapeDataString(in_PatientId)
                       + "&v=custom:(uuid,identifiers:(identifier),person:(gender,birthdate,preferredName:(givenName,familyName)))";
            Log($"REST GET /ws/rest/v1/patient?q={in_PatientId}");

            using HttpResponseMessage response = http.GetAsync(url).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

            JsonElement match = json.RootElement.GetProperty("results").EnumerateArray()
                .FirstOrDefault(p => p.GetProperty("identifiers").EnumerateArray()
                    .Any(i => i.GetProperty("identifier").GetString() == in_PatientId));
            if (match.ValueKind != JsonValueKind.Object)
            {
                Log($"Patient {in_PatientId} not found through REST");
                return (out_Found: false, out_FullName: string.Empty, out_Gender: string.Empty, out_Birthdate: string.Empty);
            }

            JsonElement person = match.GetProperty("person");
            JsonElement name = person.GetProperty("preferredName");
            string fullName = name.GetProperty("givenName").GetString() + " " + name.GetProperty("familyName").GetString();
            string birthdate = (person.GetProperty("birthdate").GetString() ?? string.Empty).Split('T')[0];
            string gender = person.GetProperty("gender").GetString() ?? string.Empty;
            Log($"REST has {in_PatientId}: {fullName}, {gender}, born {birthdate}");

            return (out_Found: true, out_FullName: fullName, out_Gender: gender, out_Birthdate: birthdate);
        }
    }
}
