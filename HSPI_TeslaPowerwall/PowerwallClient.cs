using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Scheduler;

namespace HSPI_TeslaPowerwall
{
    public class PowerwallClient
    {
        private readonly string _ipAddress;
        private readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _jsonSerializer;

        public PowerwallClient(string ipAddress) {
            this._ipAddress = ipAddress;
            HttpClientHandler handler = new HttpClientHandler();
            this._httpClient = new HttpClient(handler);
            this._jsonSerializer = new JavaScriptSerializer();

            // Powerwall Gateway uses a self-signed certificate, so let's accept it unconditionally
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
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
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://{this._ipAddress}/api{endpoint}");
            HttpResponseMessage res = await this._httpClient.SendAsync(req);
            string responseText = await res.Content.ReadAsStringAsync();
            dynamic content = this._jsonSerializer.DeserializeObject(responseText);
            
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