using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using HomeSeer.PluginSdk.Logging;

namespace HSPI_TeslaPowerwall
{
    public class PowerwallClient {
        private const int RequestTimeoutMs = 10000;
        
        private readonly string _ipAddress;
        private readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _jsonSerializer;
        private readonly HSPI _hs;

        public PowerwallClient(string ipAddress, HSPI hs) {
            _ipAddress = ipAddress;
            HttpClientHandler handler = new HttpClientHandler();
            _httpClient = new HttpClient(handler);
            _jsonSerializer = new JavaScriptSerializer();
            _hs = hs;

            // Powerwall Gateway uses a self-signed certificate, so let's accept it unconditionally
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        }

        public async Task<SiteInfo> GetSiteInfo() {
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
            CancellationTokenSource cancelSrc = new CancellationTokenSource();
            CancellationToken cancel = cancelSrc.Token;

            Task timeout = Task.Delay(RequestTimeoutMs, cancel);

            _hs.WriteLog(ELogType.Trace, $"Requesting https://{_ipAddress}/api{endpoint}");
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://{_ipAddress}/api{endpoint}");
            Task<HttpResponseMessage> responseTask = _httpClient.SendAsync(req, cancel);

            Task finished = await Task.WhenAny(timeout, responseTask);
            cancelSrc.Cancel();
            if (finished == timeout) {
                string[] parts = endpoint.Split('/');
                throw new Exception($"{parts[parts.Length - 1]} request timed out");
            }

            HttpResponseMessage res = ((Task<HttpResponseMessage>) finished).Result;
            string responseText = await res.Content.ReadAsStringAsync();
            dynamic content = _jsonSerializer.DeserializeObject(responseText);
            _hs.WriteLog(ELogType.Trace, $"Request complete with status {res.StatusCode}");
            
            req.Dispose();
            res.Dispose();
            return content;
        }
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