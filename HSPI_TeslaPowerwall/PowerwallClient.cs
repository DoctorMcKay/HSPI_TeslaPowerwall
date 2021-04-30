using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Scheduler;

namespace HSPI_TeslaPowerwall
{
    public class PowerwallClient
    {
        public DateTime LastLogin { get; private set; }
        public bool LoggingIn { get; private set; }

        private const int RequestTimeoutMs = 10000;
        
        private readonly string _ipAddress;
        private readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _jsonSerializer;
        private readonly string _email;
        private readonly string _password;

        public PowerwallClient(string ipAddress, string email, string password) {
            this._ipAddress = ipAddress;
            HttpClientHandler handler = new HttpClientHandler();
            this._httpClient = new HttpClient(handler);
            this._jsonSerializer = new JavaScriptSerializer();
            this._email = email;
            this._password = password;

            // Powerwall Gateway uses a self-signed certificate, so let's accept it unconditionally
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            handler.UseCookies = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        }
        
        public async Task Login() {
            if (_email.Length == 0 || _password.Length == 0) {
                throw new Exception("No credentials configured");
            }

            LoggingIn = true;

            CancellationTokenSource cancelSrc = new CancellationTokenSource();
            CancellationToken cancel = cancelSrc.Token;

            Task timeout = Task.Delay(RequestTimeoutMs, cancel);

            string loginUrl = $"https://{_ipAddress}/api/login/Basic";
            Program.WriteLog(LogType.Console, loginUrl);
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, loginUrl);

            LoginRequest loginRequest = new LoginRequest {
                email = _email,
                password = _password,
                username = "customer",
                force_sm_off = false
            };

            string loginRequestString = _jsonSerializer.Serialize(loginRequest);
            req.Content = new StringContent(loginRequestString, Encoding.UTF8, "application/json");
            Task<HttpResponseMessage> responseTask = _httpClient.SendAsync(req, cancel);

            Task finished = await Task.WhenAny(timeout, responseTask);
            cancelSrc.Cancel();
            if (finished == timeout) {
                LoggingIn = false;
                throw new Exception("Login request timed out");
            }

            HttpResponseMessage res = ((Task<HttpResponseMessage>) finished).Result;
            string responseText = await res.Content.ReadAsStringAsync();
            dynamic content = _jsonSerializer.DeserializeObject(responseText);
            Program.WriteLog(LogType.Console, $"Login request complete with status {res.StatusCode}");

            req.Dispose();
            res.Dispose();

            if (content.ContainsKey("error") && content["error"] != null) {
                LoggingIn = false;
                throw new Exception($"Login failed ({content["error"]})");
            }

            Program.WriteLog(LogType.Info, "Successfully logged into Gateway API");
            LastLogin = DateTime.Now;
            LoggingIn = false;
        }

        public async Task<SiteInfo> GetSiteInfo()  {
            dynamic content = await GetApiContent("/site_info/site_name");
            return new SiteInfo
            {
                Name = content["site_name"],
                Timezone = content["timezone"]
            };
        }

        public async Task<SiteMaster> GetSiteMaster() {
            dynamic content = await GetApiContent("/sitemaster");
            return new SiteMaster
            {
                Status = content["status"],
                Running = content["running"],
                ConnectedToTesla = content["connected_to_tesla"]
            };
        }

        public async Task<Aggregates> GetAggregates() {
            dynamic content = await GetApiContent("/meters/aggregates");
            return new Aggregates
            {
                Site = GetAggregateEntry(content["site"]),
                Battery = GetAggregateEntry(content["battery"]),
                Load = GetAggregateEntry(content["load"]),
                Solar = GetAggregateEntry(content["solar"])
            };
        }

        private Aggregates.Entry GetAggregateEntry(dynamic content) {
            return new Aggregates.Entry
            {
                LastCommunicationTime = content["last_communication_time"],
                InstantPower = (double) content["instant_power"],
                InstantReactivePower = (double) content["instant_reactive_power"],
                InstantApparentPower = (double) content["instant_apparent_power"],
                Frequency = (double) content["frequency"],
                EnergyExported = (double) content["energy_exported"],
                EnergyImported = (double) content["energy_imported"],
                InstantAverageVoltage = (double) content["instant_average_voltage"],
                InstantTotalCurrent = (double) content["instant_total_current"]
            };
        }

        public async Task<GridStatus> GetGridStatus() {
            dynamic content = await GetApiContent("/system_status/grid_status");
            return new GridStatus
            {
                Status = content["grid_status"],
                GridServicesActive = content["grid_services_active"]
            };
        }

        public async Task<double> GetSystemChargePercentage() {
            dynamic content = await GetApiContent("/system_status/soe");
            return (double) content["percentage"];
        }

        private async Task<dynamic> GetApiContent(string endpoint) {
            if (LoggingIn) {
                Program.WriteLog(LogType.Console, $"Suppressing {endpoint} because we are actively logging in.");
            }
            
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://{this._ipAddress}/api{endpoint}");
            HttpResponseMessage res = await this._httpClient.SendAsync(req);
            string responseText = await res.Content.ReadAsStringAsync();
            dynamic content = this._jsonSerializer.DeserializeObject(responseText);
            Program.WriteLog(LogType.Console, $"Request complete with status {res.StatusCode}");

            if (res.StatusCode == HttpStatusCode.Forbidden) {
                // We need to log in
                req.Dispose();
                res.Dispose();
                
                Program.WriteLog(LogType.Console, $"Request to {endpoint} failed with status code Forbidden; attempting to login");
                await Login();
                return await GetApiContent(endpoint);
            }
            
            req.Dispose();
            res.Dispose();
            return content;
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal struct LoginRequest {
        public string email;
        public bool force_sm_off;
        public string password;
        public string username;
    }

    public struct SiteInfo
    {
        public string Name;
        public string Timezone;
    }

    public struct SiteMaster
    {
        public string Status;
        public bool Running;
        public bool ConnectedToTesla;
    }

    public struct Aggregates
    {
        public struct Entry
        {
            public string LastCommunicationTime;
            public double InstantPower;
            public double InstantReactivePower;
            public double InstantApparentPower;
            public double Frequency;
            public double EnergyExported;
            public double EnergyImported;
            public double InstantAverageVoltage;
            public double InstantTotalCurrent;
        }

        public Entry Site;
        public Entry Battery;
        public Entry Load;
        public Entry Solar;
    }

    public struct GridStatus
    {
        public string Status;
        public bool GridServicesActive;
    }
}