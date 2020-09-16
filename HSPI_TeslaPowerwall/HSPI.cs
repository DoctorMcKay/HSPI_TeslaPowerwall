using System;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Timers;
using HomeSeer.Jui.Views;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Logging;

namespace HSPI_TeslaPowerwall
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : AbstractPlugin
	{
		public override string Name { get; } = "Tesla Powerwall";
		public override string Id { get; } = "TeslaPowerwall";
		protected override string SettingsFileName { get; } = "Tesla Powerwall.ini";

		private readonly Regex _ipRegex = new Regex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$");
		private PowerwallClient _client;
		private string _gatewayIp = "";
		private GatewayDeviceRefSet _devRefSet;
		private Timer _pollTimer;
		private bool _debugLogging = false;

		protected override void Initialize() {
			WriteLog(ELogType.Trace, "Initialize");
			
			// Build the settings page
			PageFactory settingsPageFactory = PageFactory
				.CreateSettingsPage("TeslaPowerwallSettings", "Tesla Powerwall Settings")
				.WithLabel(
					"gateway_ip_label",
					"<a href=\"https://forums.homeseer.com/forum/energy-management-plug-ins/energy-management-discussion/tesla-powerwall-dr-mckay\" target=\"_blank\">Support and Documentation</a>",
					"Enter the LAN IP address of your Tesla Backup Gateway."
				)
				.WithInput("gateway_ip", "Gateway IP")
#if DEBUG
				.WithLabel("debug_log", "Enable Debug Logging", "ON - DEBUG BUILD");
#else
				.WithToggle("debug_log", "Enable Debug Logging");
#endif
			
			Settings.Add(settingsPageFactory.Page);

			Status = PluginStatus.Ok();

			_debugLogging = HomeSeerSystem.GetINISetting("Debug", "debug_log", "0", SettingsFileName) == "1";
			
			CheckGatewayConnection();
		}

		protected override void OnSettingsLoad() {
			Settings.Pages[0].GetViewById("gateway_ip").UpdateValue(_gatewayIp);
		}

		protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView) {
			WriteLog(ELogType.Debug, $"Request to save setting {currentView.Id} on page {pageId}");

			if (pageId != "TeslaPowerwallSettings") {
				WriteLog(ELogType.Warning, $"Request to save settings on unknown page {pageId}!");
				return true;
			}

			switch (currentView.Id) {
				case "gateway_ip":
					// We want to update the gateway IP. Firstly, did it change?
					string newValue = changedView.GetStringValue();
					if (newValue == currentView.GetStringValue()) {
						return true; // no change
					}
				
					// Make sure it's a valid IP format
					if (newValue == "" || _ipRegex.Matches(newValue).Count > 0) {
						HomeSeerSystem.SaveINISetting("GatewayNetwork", "ip", newValue, SettingsFileName);
						CheckGatewayConnection();
						return true;
					}

					throw new Exception("Invalid IP address format.");
				
				case "debug_log":
					_debugLogging = changedView.GetStringValue() == "True";
					return true;
			}
			
			WriteLog(ELogType.Info, $"Request to save unknown setting {currentView.Id}");
			return false;
		}

		protected override void BeforeReturnStatus() {
			// Nothing happens here as we update the status as events happen
		}

		private async void CheckGatewayConnection() {
			_pollTimer?.Stop();
			
			_gatewayIp = HomeSeerSystem.GetINISetting("GatewayNetwork", "ip", "", SettingsFileName);

			WriteLog(ELogType.Info, $"Attempting to connect to Gateway at IP \"{_gatewayIp}\"");

			if (_ipRegex.Matches(_gatewayIp).Count == 0) {
				Status = PluginStatus.Fatal("No Tesla Gateway IP address configured");
				return;
			}
			
			_client = new PowerwallClient(_gatewayIp);

			try {
				SiteInfo info = await _client.GetSiteInfo();
				// It worked!
				Status = PluginStatus.Ok();
				WriteLog(ELogType.Info, $"Successfully contacted Gateway \"{info.Name}\" at IP {this._gatewayIp}");
				FindDevices(info.Name);

				_pollTimer = new Timer(2000) { AutoReset = true, Enabled = true };
				_pollTimer.Elapsed += (Object source, ElapsedEventArgs e) => { UpdateDeviceData(); };
			} catch (Exception ex) {
				string errorMsg = ex.Message;
				Exception innerEx = ex;
				while (innerEx.InnerException != null) {
					errorMsg += $" [{innerEx.Message}]";
				}
				
				WriteLog(ELogType.Error, $"Cannot get site master from Gateway {_gatewayIp}: {errorMsg}");
				Status = PluginStatus.Fatal("Cannot contact Gateway");

				_pollTimer = new Timer(60000) {Enabled = true};
				_pollTimer.Elapsed += (Object source, ElapsedEventArgs e) => { CheckGatewayConnection(); };
			}
		}

		private void FindDevices(string siteName) {
			string addressBase = $"TGW:{_gatewayIp}";

			int? root = HomeSeerSystem.GetDeviceByAddress(addressBase)?.Ref;
			int? systemStatus = HomeSeerSystem.GetDeviceByAddress($"{addressBase}:SystemStatus")?.Ref;
			int? connectedToTesla = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:Connected")?.Ref;
			int? chargePercent = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:Charge")?.Ref;
			int? gridStatus = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:GridStatus")?.Ref;
			int? sitePower = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:SitePower")?.Ref;
			int? batteryPower = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:BatteryPower")?.Ref;
			int? solarPower = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:SolarPower")?.Ref;
			int? gridPower = HomeSeerSystem.GetFeatureByAddress($"{addressBase}:GridPower")?.Ref;
			
			GatewayDeviceRefSet devRefSet = new GatewayDeviceRefSet();

			if (root == null) {
				DeviceFactory factory = DeviceFactory.CreateDevice(Name)
					.WithLocation("Powerwall")
					.WithLocation2("Tesla")
					.WithName(siteName)
					.WithMiscFlags(EMiscFlag.StatusOnly);

				HsDevice device = HomeSeerSystem.GetDeviceByRef(HomeSeerSystem.CreateDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.Address, addressBase);
				HomeSeerSystem.AddRefToCategory("Energy", device.Ref);

				devRefSet.Root = device.Ref;
				WriteLog(ELogType.Info, $"Created device {device.Ref} for gateway {addressBase} ({siteName})");
			} else {
				devRefSet.Root = (int) root;
				WriteLog(ELogType.Info, $"Found root device {devRefSet.Root} for gateway {addressBase} ({siteName})");
			}

			if (systemStatus == null) {
				// This should only happen when transitioning from the HS3 to HS4 plugin
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("System Status")
					.AddGraphicForValue("/images/HomeSeer/status/off.gif", 0, "Stopped")
					.AddGraphicForValue("/images/HomeSeer/status/on.gif", 1, "Running");
				
				InitializeFeatureFactory(factory);

				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:SystemStatus");

				devRefSet.SystemStatus = feature.Ref;
				WriteLog(ELogType.Info, $"Created feature {feature.Ref} for SystemStatus");
				
				// Let's also remove status pairs from the root device
				HomeSeerSystem.ClearStatusControlsByRef(devRefSet.Root);
			} else {
				devRefSet.SystemStatus = (int) systemStatus;
			}

			if (connectedToTesla == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Tesla Connection")
					.AddGraphicForValue("/images/HomeSeer/status/alarm.png", 0, "Disconnected")
					.AddGraphicForValue("/images/HomeSeer/status/ok.png", 1, "Connected");

				InitializeFeatureFactory(factory);

				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:Connected");

				devRefSet.ConnectedToTesla = feature.Ref;
				WriteLog(ELogType.Info, $"Created feature {feature.Ref} for ConnectedToTesla");
			} else {
				devRefSet.ConnectedToTesla = (int) connectedToTesla;
			}

			if (gridStatus == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Grid Status")
					.AddGraphicForValue("/images/HomeSeer/status/alarm.png", 0, "Down")
					.AddGraphicForValue("/images/HomeSeer/status/ok.png", 1, "Up");

				InitializeFeatureFactory(factory);
				
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:GridStatus");

				devRefSet.GridStatus = feature.Ref;
				WriteLog(ELogType.Info, $"Created device {feature.Ref} for GridStatus");
			} else {
				devRefSet.GridStatus = (int) gridStatus;
			}
			
			if (chargePercent == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Powerwall Charge")
					.AddGraphicForRange("/images/HomeSeer/status/battery_0.png", 0, 3)
					.AddGraphicForRange("/images/HomeSeer/status/battery_25.png", 4, 36)
					.AddGraphicForRange("/images/HomeSeer/status/battery_50.png", 37, 64)
					.AddGraphicForRange("/images/HomeSeer/status/battery_75.png", 65, 89)
					.AddGraphicForRange("/images/HomeSeer/status/battery_100.png", 90, 100);

				InitializeFeatureFactory(factory);
				
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:Charge");

				devRefSet.ChargePercent = feature.Ref;
				WriteLog(ELogType.Info, $"Created device {feature.Ref} for ChargePercent");
			} else {
				devRefSet.ChargePercent = (int) chargePercent;
			}

			if (sitePower == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Total Site Power")
					.AddGraphicForRange("/images/HomeSeer/status/replay.png", -1000000, -50)
					.AddGraphicForRange("/images/HomeSeer/status/off.gif", -49, 49)
					.AddGraphicForRange("/images/HomeSeer/status/electricity.gif", 50, 1000000);

				InitializeFeatureFactory(factory);
				
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:SitePower");
				
				devRefSet.SitePower = feature.Ref;
				WriteLog(ELogType.Info, $"Created device {feature.Ref} for SitePower");
			} else {
				devRefSet.SitePower = (int) sitePower;
			}
			
			if (batteryPower == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Powerwall Power")
					.AddGraphicForRange("/images/HomeSeer/status/replay.png", -1000000, -50)
					.AddGraphicForRange("/images/HomeSeer/status/off.gif", -49, 49)
					.AddGraphicForRange("/images/HomeSeer/status/electricity.gif", 50, 1000000);

				InitializeFeatureFactory(factory);
				
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:BatteryPower");

				devRefSet.BatteryPower = feature.Ref;
				WriteLog(ELogType.Info, $"Created device {feature.Ref} for BatteryPower");
			} else {
				devRefSet.BatteryPower = (int) batteryPower;
			}
			
			if (solarPower == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Solar Power")
					.AddGraphicForRange("/images/HomeSeer/status/replay.png", -1000000, -50)
					.AddGraphicForRange("/images/HomeSeer/status/off.gif", -49, 49)
					.AddGraphicForRange("/images/HomeSeer/status/electricity.gif", 50, 1000000);

				InitializeFeatureFactory(factory);
				
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:SolarPower");

				devRefSet.SolarPower = feature.Ref;
				WriteLog(ELogType.Info, $"Created device {feature.Ref} for SolarPower");
			} else {
				devRefSet.SolarPower = (int) solarPower;
			}
			
			if (gridPower == null) {
				FeatureFactory factory = FeatureFactory.CreateFeature(Name, devRefSet.Root)
					.WithName("Grid Power")
					.AddGraphicForRange("/images/HomeSeer/status/replay.png", -1000000, -50)
					.AddGraphicForRange("/images/HomeSeer/status/off.gif", -49, 49)
					.AddGraphicForRange("/images/HomeSeer/status/electricity.gif", 50, 1000000);

				InitializeFeatureFactory(factory);
				
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(HomeSeerSystem.CreateFeatureForDevice(factory.PrepareForHs()));
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"{addressBase}:GridPower");

				devRefSet.GridPower = feature.Ref;
				WriteLog(ELogType.Info, $"Created device {feature.Ref} for GridPower");
			} else {
				devRefSet.GridPower = (int) gridPower;
			}

			_devRefSet = devRefSet;
		}

		private void InitializeFeatureFactory(FeatureFactory factory) {
			factory.WithMiscFlags(EMiscFlag.StatusOnly)
				.WithLocation("Powerwall")
				.WithLocation2("Tesla");
		}

		private async void UpdateDeviceData() {
			WriteLog(ELogType.Debug, "Retrieving Powerwall data");

			SiteMaster siteMaster;
			Aggregates aggregates;
			GridStatus gridStatus;

			try
			{
				siteMaster = await _client.GetSiteMaster();
				aggregates = await _client.GetAggregates();
				gridStatus = await _client.GetGridStatus();
			} catch (Exception ex) {
				WriteLog(ELogType.Error, $"Unable to retrieve Powerwall data: {ex.Message}");
				return;
			}

			WriteLog(ELogType.Debug, "Powerwall data retrieved successfully");

			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.SystemStatus, siteMaster.Running ? 1 : 0);
			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.ConnectedToTesla, siteMaster.ConnectedToTesla ? 1 : 0);

			double chargePct = Math.Round(await _client.GetSystemChargePercentage(), 1);
			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.ChargePercent, chargePct);
			HomeSeerSystem.UpdateFeatureValueStringByRef(_devRefSet.ChargePercent, chargePct + "%");

			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.GridStatus, gridStatus.Status == "SystemGridConnected" ? 1 : 0);

			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.SitePower, Math.Round(aggregates.Load.InstantPower));
			HomeSeerSystem.UpdateFeatureValueStringByRef(_devRefSet.SitePower, GetPowerString(aggregates.Load.InstantPower));
			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.BatteryPower, Math.Round(aggregates.Battery.InstantPower));
			HomeSeerSystem.UpdateFeatureValueStringByRef(_devRefSet.BatteryPower, GetPowerString(aggregates.Battery.InstantPower));
			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.SolarPower, Math.Round(aggregates.Solar.InstantPower));
			HomeSeerSystem.UpdateFeatureValueStringByRef(_devRefSet.SolarPower, GetPowerString(aggregates.Solar.InstantPower));
			HomeSeerSystem.UpdateFeatureValueByRef(_devRefSet.GridPower, Math.Round(aggregates.Site.InstantPower));
			HomeSeerSystem.UpdateFeatureValueStringByRef(_devRefSet.GridPower, GetPowerString(aggregates.Site.InstantPower));
		}

		private string GetPowerString(double watts) {
			return $"{Math.Round(watts / 1000, 1)} kW";
		}

		public void WriteLog(ELogType logType, string message, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null) {
#if DEBUG
			bool isDebugMode = true;

			// Prepend calling function and line number
			message = $"[{caller}:{lineNumber}] {message}";
			
			// Also print to console in debug builds
			string type = logType.ToString().ToLower();
			Console.WriteLine($"[{type}] {message}");
#else
			bool isDebugMode = _debugLogging;
#endif

			if (logType <= ELogType.Debug && !isDebugMode) {
				return;
			}
			
			HomeSeerSystem.WriteLog(logType, message, Name);
		}
	}

	public struct GatewayDeviceRefSet
	{
		public int Root;
		public int SystemStatus;
		public int ConnectedToTesla;
		public int ChargePercent;
		public int GridStatus;
		public int SitePower;
		public int BatteryPower;
		public int SolarPower;
		public int GridPower;
	}
}
