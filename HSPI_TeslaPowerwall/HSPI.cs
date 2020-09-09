using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using System.Web;
using HomeSeerAPI;
using Scheduler;
using Scheduler.Classes;

namespace HSPI_TeslaPowerwall
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : HspiBase
	{
		public const string PLUGIN_NAME = "Tesla Powerwall";

		private PowerwallClient _client;
		private string _gatewayIp = "";
		private GatewayDeviceRefSet _devRefSet;
		private Timer _pollTimer;
		private IPlugInAPI.enumInterfaceStatus _interfaceStatus = IPlugInAPI.enumInterfaceStatus.OK;
		private string _interfaceStatusString = "";

		public HSPI() {
			Name = PLUGIN_NAME;
			PluginIsFree = true;
		}

		public override string InitIO(string port) {
			Program.WriteLog(LogType.Verbose, "InitIO");

			hs.RegisterPage("TeslaPowerwallSettings", Name, InstanceFriendlyName());
			WebPageDesc configLink = new WebPageDesc {
				plugInName = Name,
				plugInInstance = InstanceFriendlyName(),
				link = "TeslaPowerwallSettings",
				linktext = "Settings",
				order = 1,
				page_title = "Tesla Powerwall Settings"
			};
			callbacks.RegisterConfigLink(configLink);
			callbacks.RegisterLink(configLink);
			
			CheckGatewayConnection();
			return "";
		}

		public override IPlugInAPI.strInterfaceStatus InterfaceStatus()
		{
			return new IPlugInAPI.strInterfaceStatus
				{intStatus = this._interfaceStatus, sStatus = this._interfaceStatusString};
		}

		private async void CheckGatewayConnection() {
			this._pollTimer?.Stop();
			
			this._gatewayIp = hs.GetINISetting("GatewayNetwork", "ip", "", IniFilename);

			Program.WriteLog(LogType.Info, $"Attempting to connect to Gateway at IP \"{this._gatewayIp}\"");

			Regex ipRgx = new Regex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$");
			if (ipRgx.Matches(_gatewayIp).Count == 0) {
				this._interfaceStatus = IPlugInAPI.enumInterfaceStatus.FATAL;
				this._interfaceStatusString = "No Tesla Gateway IP address configured";
				return;
			}
			
			this._client = new PowerwallClient(this._gatewayIp);

			try
			{
				SiteInfo info = await this._client.GetSiteInfo();
				// It worked!
				this._interfaceStatus = IPlugInAPI.enumInterfaceStatus.OK;
				this._interfaceStatusString = "";
				Program.WriteLog(LogType.Info, $"Successfully contacted Gateway \"{info.Name}\" at IP {this._gatewayIp}");
				FindDevices(info.Name);

				this._pollTimer = new Timer(2000) { AutoReset = true, Enabled = true };
				this._pollTimer.Elapsed += (Object source, ElapsedEventArgs e) => { UpdateDeviceData(); };
			}
			catch (Exception ex)
			{
				Program.WriteLog(LogType.Error, $"Cannot get site master from Gateway {this._gatewayIp}: {ex.Message}");
				this._interfaceStatus = IPlugInAPI.enumInterfaceStatus.FATAL;
				this._interfaceStatusString = "Cannot contact Gateway";

				this._pollTimer = new Timer(60000) {Enabled = true};
				this._pollTimer.Elapsed += (Object source, ElapsedEventArgs e) => { CheckGatewayConnection(); };
			}
		}

		public override string GetPagePlugin(string pageName, string user, int userRights, string queryString) {
			Program.WriteLog(LogType.Verbose, $"Requested page name {pageName} by user {user} with rights {userRights}");

			switch (pageName) {
				case "TeslaPowerwallSettings":
					return BuildSettingsPage(user, userRights, queryString);
			}

			return "";
		}

		private string BuildSettingsPage(string user, int userRights, string queryString, string messageBox = null, string messageBoxClass = null) {
			const string pageName = "TeslaPowerwallSettings";
			PageBuilderAndMenu.clsPageBuilder builder = new PageBuilderAndMenu.clsPageBuilder(pageName);
			if ((userRights & 2) != 2) {
				// User is not an admin
				builder.reset();
				builder.AddHeader(hs.GetPageHeader(pageName, "Tesla Powerwall Settings", "", "", false, true));
				builder.AddBody("<p><strong>Access Denied:</strong> You are not an administrative user.</p>");
				builder.AddFooter(hs.GetPageFooter());
				builder.suppressDefaultFooter = true;

				return builder.BuildPage();
			}

			StringBuilder sb = new StringBuilder();

			sb.Append(PageBuilderAndMenu.clsPageBuilder.FormStart("tesla_powerwall_config_form", "tesla_powerwall_config_form", "post"));
			sb.Append("<table width=\"1000px\" cellspacing=\"0\"><tr><td class=\"tableheader\" colspan=\"3\">Settings</td></tr>");
			
			sb.Append("<tr><td class=\"tablecell\" style=\"width:200px\" align=\"left\">Gateway Network IP:</td>");
			sb.Append("<td class=\"tablecell\">");
			clsJQuery.jqTextBox textBox = new clsJQuery.jqTextBox("GatewayIP", "text", this._gatewayIp, pageName, 30, true);
			sb.Append(textBox.Build());
			sb.Append("</td></tr>");

			sb.Append("</table>");

			clsJQuery.jqButton doneBtn = new clsJQuery.jqButton("DoneBtn", "Done", pageName, false);
			doneBtn.url = "/";
			sb.Append("<br />");
			sb.Append(doneBtn.Build());
			sb.Append("<br /><br />");

			builder.reset();
			builder.AddHeader(hs.GetPageHeader(pageName, "Tesla Powerwall Settings", "", "", false, true));
			builder.AddBody(sb.ToString());
			builder.AddFooter(hs.GetPageFooter());
			builder.suppressDefaultFooter = true;

			return builder.BuildPage();
		}

		public override string PostBackProc(string page, string data, string user, int userRights) {
			Program.WriteLog(LogType.Verbose, $"PostBackProc page name {page} by user {user} with rights {userRights}");
			if (page != "TeslaPowerwallSettings") {
				return "Unknown page " + page;
			}

			if ((userRights & 2) != 2) {
				return "Access denied: you are not an administrative user.";
			}

			NameValueCollection postData = HttpUtility.ParseQueryString(data);

			string gwIp = postData.Get("GatewayIP");
			hs.SaveINISetting("GatewayNetwork", "ip", gwIp, IniFilename);
			this._gatewayIp = gwIp;
			Program.WriteLog(LogType.Info, $"Updating Gateway IP to \"{gwIp}\"");
			CheckGatewayConnection();

			return "";
		}

		private void FindDevices(string siteName) {
			string addressBase = $"TGW:{this._gatewayIp}";
			GatewayDeviceRefSet refSet = new GatewayDeviceRefSet
			{
				Root = hs.DeviceExistsAddress(addressBase, false),
				ConnectedToTesla = hs.DeviceExistsAddress($"{addressBase}:Connected", false),
				ChargePercent = hs.DeviceExistsAddress($"{addressBase}:Charge", false),
				GridStatus = hs.DeviceExistsAddress($"{addressBase}:GridStatus", false),
				SitePower = hs.DeviceExistsAddress($"{addressBase}:SitePower", false),
				BatteryPower = hs.DeviceExistsAddress($"{addressBase}:BatteryPower", false),
				SolarPower = hs.DeviceExistsAddress($"{addressBase}:SolarPower", false),
				GridPower = hs.DeviceExistsAddress($"{addressBase}:GridPower", false)
			};

			DeviceClass rootDevice;

			if (refSet.Root == -1) {
				int hsRef = hs.NewDeviceRef(siteName);
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, null, null);
				
				VSVGPairs.VSPair stoppedStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Status = "Stopped",
					Value = 0
				};

				VSVGPairs.VSPair runningStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Status = "Running",
					Value = 1
				};

				hs.DeviceVSP_AddPair(hsRef, stoppedStatus);
				hs.DeviceVSP_AddPair(hsRef, runningStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Set_Value = 0,
					Graphic = "/images/HomeSeer/status/off.gif"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Set_Value = 1,
					Graphic = "/images/HomeSeer/status/on.gif"
				});
				
				refSet.Root = hsRef;
				rootDevice = device;
				
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for gateway {addressBase} ({siteName})");
			} else {
				rootDevice = (DeviceClass) hs.GetDeviceByRef(refSet.Root);
				Program.WriteLog(LogType.Info, $"Found root device {refSet.Root} for gateway {addressBase} ({siteName})");
			}

			if (refSet.ConnectedToTesla == -1) {
				int hsRef = hs.NewDeviceRef("Tesla Connection");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "Connected", rootDevice);

				VSVGPairs.VSPair disconnectedStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Status = "Disconnected",
					Value = 0
				};

				VSVGPairs.VSPair connectedStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Status = "Connected",
					Value = 1
				};

				hs.DeviceVSP_AddPair(hsRef, disconnectedStatus);
				hs.DeviceVSP_AddPair(hsRef, connectedStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Set_Value = 0,
					Graphic = "/images/HomeSeer/status/alarm.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Set_Value = 1,
					Graphic = "/images/HomeSeer/status/ok.png"
				});

				refSet.ConnectedToTesla = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for ConnectedToTesla");
			}
			
			if (refSet.GridStatus == -1) {
				int hsRef = hs.NewDeviceRef("Grid Status");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "GridStatus", rootDevice);

				VSVGPairs.VSPair downStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Value = 0,
					Status = "Down"
				};
				
				VSVGPairs.VSPair upStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Value = 1,
					Status = "Up"
				};

				hs.DeviceVSP_AddPair(hsRef, downStatus);
				hs.DeviceVSP_AddPair(hsRef, upStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Set_Value = 0,
					Graphic = "/images/HomeSeer/status/alarm.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.SingleValue,
					Set_Value = 1,
					Graphic = "/images/HomeSeer/status/ok.png"
				});

				refSet.GridStatus = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for GridStatus");
			}
			
			if (refSet.ChargePercent == -1) {
				int hsRef = hs.NewDeviceRef("Powerwall Charge");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "Charge", rootDevice);

				VSVGPairs.VSPair chargeStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStatusSuffix = "%",
					RangeStatusDecimals = 1,
					RangeStart = 0,
					RangeEnd = 100
				};

				hs.DeviceVSP_AddPair(hsRef, chargeStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 0,
					RangeEnd = 3,
					Graphic = "/images/HomeSeer/status/battery_0.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 4,
					RangeEnd = 36,
					Graphic = "/images/HomeSeer/status/battery_25.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 37,
					RangeEnd = 64,
					Graphic = "/images/HomeSeer/status/battery_50.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 65,
					RangeEnd = 89,
					Graphic = "/images/HomeSeer/status/battery_75.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 90,
					RangeEnd = 100,
					Graphic = "/images/HomeSeer/status/battery_100.png"
				});

				refSet.ChargePercent = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for ChargePercent");
			}

			if (refSet.SitePower == -1) {
				int hsRef = hs.NewDeviceRef("Total Site Power");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "SitePower", rootDevice);

				VSVGPairs.VSPair powerStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = 1000000,
					RangeStatusSuffix = " W",
				};

				hs.DeviceVSP_AddPair(hsRef, powerStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = -50,
					Graphic = "/images/HomeSeer/status/replay.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -49,
					RangeEnd = 49,
					Graphic = "/images/HomeSeer/status/off.gif"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 50,
					RangeEnd = 1000000,
					Graphic = "/images/HomeSeer/status/electricity.gif"
				});
				
				refSet.SitePower = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for SitePower");
			}
			
			if (refSet.BatteryPower == -1) {
				int hsRef = hs.NewDeviceRef("Powerwall Power");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "BatteryPower", rootDevice);

				VSVGPairs.VSPair powerStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = 1000000,
					RangeStatusSuffix = " W",
				};

				hs.DeviceVSP_AddPair(hsRef, powerStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = -50,
					Graphic = "/images/HomeSeer/status/replay.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -49,
					RangeEnd = 49,
					Graphic = "/images/HomeSeer/status/off.gif"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 50,
					RangeEnd = 1000000,
					Graphic = "/images/HomeSeer/status/electricity.gif"
				});
				
				refSet.BatteryPower = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for BatteryPower");
			}
			
			if (refSet.SolarPower == -1) {
				int hsRef = hs.NewDeviceRef("Solar Power");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "SolarPower", rootDevice);

				VSVGPairs.VSPair powerStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = 1000000,
					RangeStatusSuffix = " W",
				};

				hs.DeviceVSP_AddPair(hsRef, powerStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = -50,
					Graphic = "/images/HomeSeer/status/replay.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -49,
					RangeEnd = 49,
					Graphic = "/images/HomeSeer/status/off.gif"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 50,
					RangeEnd = 1000000,
					Graphic = "/images/HomeSeer/status/electricity.gif"
				});
				
				refSet.SolarPower = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for SolarPower");
			}
			
			if (refSet.GridPower == -1) {
				int hsRef = hs.NewDeviceRef("Grid Power");
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(hsRef);
				InitializeDevice(device, addressBase, "GridPower", rootDevice);

				VSVGPairs.VSPair powerStatus = new VSVGPairs.VSPair(ePairStatusControl.Status)
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = 1000000,
					RangeStatusSuffix = " W",
				};

				hs.DeviceVSP_AddPair(hsRef, powerStatus);
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -1000000,
					RangeEnd = -50,
					Graphic = "/images/HomeSeer/status/replay.png"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = -49,
					RangeEnd = 49,
					Graphic = "/images/HomeSeer/status/off.gif"
				});
				hs.DeviceVGP_AddPair(hsRef, new VSVGPairs.VGPair
				{
					PairType = VSVGPairs.VSVGPairType.Range,
					RangeStart = 50,
					RangeEnd = 1000000,
					Graphic = "/images/HomeSeer/status/electricity.gif"
				});
				
				refSet.GridPower = hsRef;
				Program.WriteLog(LogType.Info, $"Created device {hsRef} for GridPower");
			}

			this._devRefSet = refSet;
		}

		private void InitializeDevice(DeviceClass device, string addressBase, string addressSuffix, DeviceClass rootDevice) {
			string address = addressBase;
			if (addressSuffix != null) {
				address = $"{addressBase}:{addressSuffix}";
			}

			device.set_Address(hs, address);
			device.set_Interface(hs, Name);
			device.set_InterfaceInstance(hs, InstanceFriendlyName());
			device.set_Device_Type_String(hs, $"Tesla Powerwall");
			if (addressSuffix == null) {
				device.set_DeviceType_Set(hs, new DeviceTypeInfo_m.DeviceTypeInfo
				{
					Device_Type = DeviceTypeInfo_m.DeviceTypeInfo.eDeviceType_GenericRoot
				});
				
				device.set_Relationship(hs, Enums.eRelationship.Parent_Root);
			} else {
				device.set_DeviceType_Set(hs, new DeviceTypeInfo_m.DeviceTypeInfo
				{
					Device_API = DeviceTypeInfo_m.DeviceTypeInfo.eDeviceAPI.Plug_In
				});
				
				device.set_Relationship(hs, Enums.eRelationship.Child);
				rootDevice.AssociatedDevice_Add(hs, device.get_Ref(hs));
				device.AssociatedDevice_Add(hs, rootDevice.get_Ref(hs));
			}
		}

		private async void UpdateDeviceData() {
			Program.WriteLog(LogType.Verbose, "Retrieving Powerwall data");

			SiteMaster siteMaster;
			Aggregates aggregates;
			GridStatus gridStatus;

			try
			{
				siteMaster = await this._client.GetSiteMaster();
				aggregates = await this._client.GetAggregates();
				gridStatus = await this._client.GetGridStatus();
			} catch (Exception ex) {
				Program.WriteLog(LogType.Error, $"Unable to retrieve Powerwall data: {ex.Message}");
				return;
			}

			Program.WriteLog(LogType.Verbose, "Powerwall data retrieved successfully");

			hs.SetDeviceValueByRef(this._devRefSet.Root, siteMaster.Running ? 1 : 0, true);
			hs.SetDeviceValueByRef(this._devRefSet.ConnectedToTesla, siteMaster.ConnectedToTesla ? 1 : 0, true);

			hs.SetDeviceValueByRef(this._devRefSet.ChargePercent, await this._client.GetSystemChargePercentage(), true);

			hs.SetDeviceValueByRef(this._devRefSet.GridStatus, gridStatus.Status == "SystemGridConnected" ? 1 : 0, true);

			hs.SetDeviceValueByRef(this._devRefSet.SitePower, aggregates.Load.InstantPower, true);
			hs.SetDeviceString(this._devRefSet.SitePower, GetPowerString(aggregates.Load.InstantPower), false);
			hs.SetDeviceValueByRef(this._devRefSet.BatteryPower, aggregates.Battery.InstantPower, true);
			hs.SetDeviceString(this._devRefSet.BatteryPower, GetPowerString(aggregates.Battery.InstantPower), false);
			hs.SetDeviceValueByRef(this._devRefSet.SolarPower, aggregates.Solar.InstantPower, true);
			hs.SetDeviceString(this._devRefSet.SolarPower, GetPowerString(aggregates.Solar.InstantPower), false);
			hs.SetDeviceValueByRef(this._devRefSet.GridPower, aggregates.Site.InstantPower, true);
			hs.SetDeviceString(this._devRefSet.GridPower, GetPowerString(aggregates.Site.InstantPower), false);
		}

		private string GetPowerString(double watts) {
			return $"{Math.Round(watts / 1000, 1)} kW";
		}
	}

	public struct GatewayDeviceRefSet
	{
		public int Root;
		public int ConnectedToTesla;
		public int ChargePercent;
		public int GridStatus;
		public int SitePower;
		public int BatteryPower;
		public int SolarPower;
		public int GridPower;
	}
}
