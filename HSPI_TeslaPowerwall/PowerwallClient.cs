using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Energy;
using HomeSeer.PluginSdk.Logging;

namespace HSPI_TeslaPowerwall
{
    public class PowerwallClient {
        public DateTime LastLogin { get; private set; }
        public bool LoggingIn { get; private set; }
        public string UpstreamIpAddress { get; private set; }
        
        private const int RequestTimeoutMs = 5000;
        
        private readonly string _ipAddress;
        private readonly ushort _port;
        private readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _jsonSerializer;
        private readonly HSPI _hs;
        private readonly string _email;
        private readonly string _password;

        private EnergyMeterSnapshot? _siteSnapshot = null;
        private EnergyMeterSnapshot? _solarSnapshot = null;

        public PowerwallClient(string ipAddress, ushort port, HSPI hs, string email, string password) {
            _ipAddress = ipAddress;
            _port = port;
            HttpClientHandler handler = new HttpClientHandler();
            _httpClient = new HttpClient(handler);
            _jsonSerializer = new JavaScriptSerializer();
            _hs = hs;
            _email = email;
            _password = password;

            UpstreamIpAddress = "";

            // Powerwall Gateway uses a self-signed certificate, so let's accept it unconditionally
            handler.ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => {
                if (certificate.SubjectName.Name != null) {
                    try {
                        string commonName = certificate.SubjectName.Name
                            .Split(',')
                            .Select(i => i.Trim())
                            .First(i => i.StartsWith("CN="))
                            .Substring(3);

                        if (commonName.StartsWith("TLSPROXY:")) {
                            string upstreamIp = commonName.Substring(9);
                            if (upstreamIp != UpstreamIpAddress) {
                                UpstreamIpAddress = upstreamIp;
                                _hs.WriteLog(ELogType.Info, $"Detected TLS proxy for upstream address {UpstreamIpAddress}");
                            }
                        }
                    } catch (Exception) {
                        // No CN, ignore
                    }
                }

                // Trust certificate always
                return true;
            };
            
            handler.UseCookies = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        }

        private async Task Login() {
            if (LoggingIn) {
                _hs.WriteLog(ELogType.Trace, "Suppressing login attempt because we're already trying to login");
                return;
            }
            
            if (_email.Length == 0 || _password.Length == 0) {
                throw new Exception("No credentials configured");
            }
            
            LoggingIn = true;
            
            CancellationTokenSource cancelSrc = new CancellationTokenSource();
            CancellationToken cancel = cancelSrc.Token;

            Task timeout = Task.Delay(RequestTimeoutMs, cancel);

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"https://{_ipAddress}:{_port}/api/login/Basic")) {
                req.Headers.Host = "teg"; // necessary to pass the gateway's SNI checks

                string loginRequestString = _jsonSerializer.Serialize(new {
                    email = _email,
                    password = _password,
                    username = "customer",
                    force_sm_off = false
                });

                req.Content = new StringContent(loginRequestString, Encoding.UTF8, "application/json");
                Task<HttpResponseMessage> responseTask = _httpClient.SendAsync(req, cancel);

                Task finished = await Task.WhenAny(timeout, responseTask);
                cancelSrc.Cancel();
                if (finished == timeout) {
                    LoggingIn = false;
                    throw new Exception("Login request timed out");
                }

                using (HttpResponseMessage res = ((Task<HttpResponseMessage>) finished).Result) {
                    string responseText = await res.Content.ReadAsStringAsync();
                    dynamic content = _jsonSerializer.DeserializeObject(responseText);
                    _hs.WriteLog(ELogType.Trace, $"Login request complete with status {res.StatusCode}");

                    if (content.ContainsKey("error") && content["error"] != null) {
                        LoggingIn = false;
                        throw new Exception($"Login failed ({content["error"]})");
                    }

                    _hs.WriteLog(ELogType.Info, "Successfully logged into Gateway API");
                    LastLogin = DateTime.Now;
                    LoggingIn = false;
                } // using HttpResponseMessage
            } // using HttpRequestMessage
        }

        public async Task<SiteInfo> GetSiteInfo() {
            dynamic content = await GetApiContent("/site_info/site_name", true);
            return new SiteInfo
            {
                Name = content["site_name"],
                Timezone = content["timezone"]
            };
        }

        public async Task<SiteMaster> GetSiteMaster() {
            dynamic content = await GetApiContent("/sitemaster", true);
            return new SiteMaster
            {
                Status = content["status"],
                Running = content["running"],
                ConnectedToTesla = content["connected_to_tesla"]
            };
        }

        public async Task<Aggregates> GetAggregates() {
            dynamic content = await GetApiContent("/meters/aggregates", true);
            Aggregates aggregates = new Aggregates
            {
                Site = GetAggregateEntry(content["site"]),
                Battery = GetAggregateEntry(content["battery"]),
                Load = GetAggregateEntry(content["load"]),
                Solar = content.ContainsKey("solar") ? GetAggregateEntry(content["solar"]) : GetBlankAggregateEntry()
            };

            if (HSPI.ENABLE_ENERGY_INTEGRATION) {
                EnergyMeterSnapshot siteSnapshot = new EnergyMeterSnapshot {
                    Timestamp = DateTime.Now,
                    EnergyExported = aggregates.Site.EnergyExported,
                    EnergyImported = aggregates.Site.EnergyImported
                };

                EnergyMeterSnapshot solarSnapshot = new EnergyMeterSnapshot {
                    Timestamp = DateTime.Now,
                    EnergyExported = aggregates.Solar.EnergyExported,
                    EnergyImported = aggregates.Solar.EnergyImported
                };

                if (_siteSnapshot != null) {
                    EnergyMeterSnapshot prev = _siteSnapshot.Value;
                    double kWh = (siteSnapshot.EnergyImported - prev.EnergyImported - (siteSnapshot.EnergyExported - prev.EnergyExported)) / 1000;
                    aggregates.SiteEnergyData = new EnergyData(kWh >= 0 ? Constants.enumEnergyDirection.Consumed : Constants.enumEnergyDirection.Produced) {
                        // The docs claim that Amount is "Watts" but it appears that it should actually be kWh
                        Amount = Math.Abs(kWh),
                        Amount_End = siteSnapshot.Timestamp,
                        Amount_Start = prev.Timestamp,
                        Device = Constants.enumEnergyDevice.Meter_Service,
                        // The docs claim that Rate is "kWH" but it seems to be kW
                        //Rate = Math.Abs((float) aggregates.Site.InstantPower / 1000)
                    };
                }

                if (_solarSnapshot != null) {
                    EnergyMeterSnapshot prev = _solarSnapshot.Value;
                    double kWh = (solarSnapshot.EnergyExported - prev.EnergyExported - (solarSnapshot.EnergyImported - prev.EnergyImported)) / 1000;
                    aggregates.SolarEnergyData = new EnergyData(kWh >= 0 ? Constants.enumEnergyDirection.Produced : Constants.enumEnergyDirection.Consumed) {
                        Amount = Math.Abs(kWh),
                        Amount_End = solarSnapshot.Timestamp,
                        Amount_Start = prev.Timestamp,
                        Device = Constants.enumEnergyDevice.Solar_Panel,
                        //Rate = Math.Abs((float) aggregates.Solar.InstantPower / 1000)
                    };
                }

                _siteSnapshot = siteSnapshot;
                _solarSnapshot = solarSnapshot;
            }

            return aggregates;
        }

        private static Aggregates.Entry GetAggregateEntry(dynamic content) {
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

        private static Aggregates.Entry GetBlankAggregateEntry() {
            return new Aggregates.Entry {
                LastCommunicationTime = "1970-01-01T00:00:00.000Z",
                InstantPower = 0.0,
                InstantReactivePower = 0.0,
                InstantApparentPower = 0.0,
                Frequency = 0,
                EnergyExported = 0.0,
                EnergyImported = 0.0,
                InstantAverageVoltage = 0.0,
                InstantTotalCurrent = 0.0
            };
        }

        public async Task<GridStatus> GetGridStatus() {
            dynamic content = await GetApiContent("/system_status/grid_status", true);
            return new GridStatus
            {
                Status = content["grid_status"],
                GridServicesActive = content["grid_services_active"]
            };
        }

        public async Task<double> GetSystemChargePercentage() {
            dynamic content = await GetApiContent("/system_status/soe", true);
            return (double) content["percentage"];
        }

        public async Task<OperationConfig> GetSystemOperationConfig() {
            dynamic content = await GetApiContent("/operation", true);
            return new OperationConfig {
                RealMode = content["real_mode"],
                BackupReservePercent = content["backup_reserve_percent"]
            };
        }

        private async Task<dynamic> GetApiContent(string endpoint, bool failOnUnsuccessfulCode = false) {
            if (LoggingIn) {
                _hs.WriteLog(ELogType.Trace, $"Suppressing {endpoint} request because we are actively logging in.");
                throw new Exception($"Suppressing {endpoint} request because we are actively logging in.");
            }
            
            CancellationTokenSource cancelSrc = new CancellationTokenSource();
            CancellationToken cancel = cancelSrc.Token;

            Task timeout = Task.Delay(RequestTimeoutMs, cancel);

            _hs.WriteLog(ELogType.Trace, $"Requesting https://{_ipAddress}:{_port}/api{endpoint}");
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://{_ipAddress}:{_port}/api{endpoint}")) {
                req.Headers.Host = "teg"; // necessary to pass the gateway's SNI checks
                Task<HttpResponseMessage> responseTask = _httpClient.SendAsync(req, cancel);

                Task finished = await Task.WhenAny(timeout, responseTask);
                cancelSrc.Cancel();
                if (finished == timeout) {
                    string[] parts = endpoint.Split('/');
                    throw new Exception($"{parts[parts.Length - 1]} request timed out");
                }

                using (HttpResponseMessage res = ((Task<HttpResponseMessage>) finished).Result) {
                    string responseText = await res.Content.ReadAsStringAsync();
                    dynamic content = _jsonSerializer.DeserializeObject(responseText);
                    _hs.WriteLog(ELogType.Trace, $"Request complete with status {res.StatusCode}");

                    if (res.StatusCode == HttpStatusCode.Forbidden) {
                        // We need to log in
                        _hs.WriteLog(ELogType.Warning, $"Request to {endpoint} failed with status code Forbidden; attempting to login");
                        await Login();
                        return await GetApiContent(endpoint);
                    }

                    if (failOnUnsuccessfulCode && !res.IsSuccessStatusCode) {
                        throw new Exception($"Request \"{endpoint}\" failed with status code \"{res.StatusCode}\"");
                    }
                    
                    return content;
                } // using HttpResponseMessage
            } // using HttpRequestMessage
        }
        
        private struct EnergyMeterSnapshot {
            public DateTime Timestamp;
            public double EnergyExported;
            public double EnergyImported;
        }
    }

    public struct SiteInfo {
        public string Name;
        public string Timezone;
    }

    public struct SiteMaster {
        public string Status;
        public bool Running;
        public bool ConnectedToTesla;
    }

    public struct Aggregates {
        public struct Entry {
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

        public EnergyData SiteEnergyData;
        public EnergyData SolarEnergyData;
    }

    public struct GridStatus {
        public string Status;
        public bool GridServicesActive;
    }

    public struct OperationConfig {
        public string RealMode;
        public decimal BackupReservePercent;
    }
}