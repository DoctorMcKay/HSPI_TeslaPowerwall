#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HomeSeer.Jui.Views;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Events;
using HSPI_TeslaPowerwall.Enums;
using Newtonsoft.Json.Linq;

namespace HSPI_TeslaPowerwall.HsEvents;

public class TeslaTrigger : AbstractTriggerType2 {
	// ReSharper disable once InconsistentNaming
	public const int TriggerNumber = 1;

	protected override List<string> SubTriggerTypeNames { get; set; } = new List<string> {
		"Powerwall begins/is charging",
		"Powerwall begins/is discharging",
		"Powerwall becomes/is idle",
		"Solar begins/is producing",
		"Solar becomes/is idle",
		"Grid begins/is importing",
		"Grid begins/is exporting",
		"Grid becomes/is idle"
	};

	private string OptionIdExplainTrigger => $"{PageId}-ExplainTrig";
	private string OptionIdExplainCondition => $"{PageId}-ExplainCond";
	private HSPI Plugin => (HSPI) TriggerListener;

	protected override string GetName() => "Tesla Powerwall...";
	public override bool CanBeCondition => true;

	public SubTrigger? SubTrig {
		get {
			if (SelectedSubTriggerIndex < 0) {
				return null;
			}

			if (!Enum.IsDefined(typeof(SubTrigger), SelectedSubTriggerIndex)) {
				return null;
			}

			return (SubTrigger) SelectedSubTriggerIndex;
		}
	}

	public TeslaTrigger(TrigActInfo trigInfo, TriggerTypeCollection.ITriggerTypeListener listener, bool logDebug = false)
		: base(trigInfo, listener, logDebug) { }

	public TeslaTrigger(int id, int eventRef, int selectedSubTriggerIndex, byte[] dataIn, TriggerTypeCollection.ITriggerTypeListener listener, bool logDebug)
		: base(id, eventRef, selectedSubTriggerIndex, dataIn, listener, logDebug) { }

	public TeslaTrigger() { }

	protected override void OnInstantiateTrigger(Dictionary<string, string> viewIdValuePairs) {
		if (SelectedSubTriggerIndex < 0) {
			// No sub-trigger selected yet
			return;
		}

		PageFactory factory = PageFactory.CreateEventTriggerPage(PageId, Name);
			
		// Add a LabelView to explain how this trigger works because we can't detect whether it's a trigger or a condition
		switch (SubTrig) {
			case SubTrigger.BatteryCharging:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your Powerwall begins charging");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your Powerwall is currently charging");
				break;
				
			case SubTrigger.BatteryDischarging:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your Powerwall begins discharging");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your Powerwall is currently discharging");
				break;
			
			case SubTrigger.BatteryIdle:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your Powerwall becomes idle (neither charging nor discharging)");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your Powerwall is currently idle (neither charging nor discharging)");
				break;
			
			case SubTrigger.SolarProducing:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your solar panels begin producing power");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your solar panels are currently producing power");
				break;
			
			case SubTrigger.SolarIdle:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your solar panels stop producing power");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your solar panels are currently not producing power");
				break;
			
			case SubTrigger.GridImporting:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your home begins importing/buying power from the grid");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your home is currently importing/buying power from the grid");
				break;
			
			case SubTrigger.GridExporting:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your home begins exporting/selling power to the grid");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your home is currently exporting/selling power to the grid");
				break;
			
			case SubTrigger.GridIdle:
				factory.WithLabel(OptionIdExplainTrigger, "When used as a trigger (WHEN/OR WHEN)", "Triggers the event when your home stops importing or exporting power to or from the grid (as is the case when excess solar is charging your Powerwall, or during a grid outage)");
				factory.WithLabel(OptionIdExplainCondition, "When used as a condition (IF/AND IF)", "Passes if your home is currently not importing or exporting power to or from the grid (as is the case when excess solar is charging your Powerwall, or during a grid outage)");
				break;
		}
		
		ConfigPage = factory.Page;
	}

	public override bool IsFullyConfigured() {
		return true;
	}

	protected override bool OnConfigItemUpdate(AbstractView configViewChange) {
		// We don't have any possible config items, so this shouldn't ever be called
		return true;
	}

	public override string GetPrettyString() {
		string triggerName = SubTriggerTypeNames[SelectedSubTriggerIndex];
		string[] words = triggerName.Split(' ');
		string subject = words[0];
		triggerName = string.Join(" ", words.Skip(1));
		
		bool? isCondition = _triggerIsCondition();

		if (isCondition != null) {
			bool isTrigger = !(bool) isCondition;

			switch (SubTrig) {
				case SubTrigger.BatteryCharging:
					triggerName = isTrigger ? "begins charging" : "is charging";
					break;
				
				case SubTrigger.BatteryDischarging:
					triggerName = isTrigger ? "begins discharging" : "is discharging";
					break;
				
				case SubTrigger.BatteryIdle:
					triggerName = isTrigger ? "becomes idle" : "is idle";
					break;
				
				case SubTrigger.SolarProducing:
					triggerName = isTrigger ? "begins producing" : "is producing";
					break;
				
				case SubTrigger.SolarIdle:
					triggerName = isTrigger ? "becomes idle" : "is idle";
					break;
				
				case SubTrigger.GridImporting:
					triggerName = isTrigger ? "begins importing" : "is importing";
					break;
				
				case SubTrigger.GridExporting:
					triggerName = isTrigger ? "begins exporting" : "is exporting";
					break;
				
				case SubTrigger.GridIdle:
					triggerName = isTrigger ? "becomes idle" : "is idle" ;
					break;
			}
		}

		return $"Tesla: {subject} <font class=\"event_Txt_Selection\">{triggerName}</font>";
	}

	public override bool IsTriggerTrue(bool isCondition) {
		switch (SubTrig) {
			case SubTrigger.BatteryCharging:
			case SubTrigger.BatteryDischarging:
			case SubTrigger.BatteryIdle:
				int? powerwallRef = Plugin.DevRefSet?.BatteryPower;
				if (powerwallRef == null) {
					return false;
				}

				int powerwallPowerValue = (int) Math.Round((double) Plugin.GetHsController().GetPropertyByRef(powerwallRef.Value, EProperty.Value));
				PowerFlow batteryFlow = HSPI.GetPowerFlow(powerwallPowerValue);

				if (SubTrig == SubTrigger.BatteryIdle) {
					return batteryFlow == PowerFlow.Zero;
				}
				
				return SubTrig == SubTrigger.BatteryCharging
					? batteryFlow == PowerFlow.Negative        // Powerwall is charging if its value is below -50W
					: batteryFlow == PowerFlow.Positive;       // Powerwall is discharging if its value is above 50W
			
			case SubTrigger.SolarProducing:
			case SubTrigger.SolarIdle:
				int? solarRef = Plugin.DevRefSet?.SolarPower;
				if (solarRef == null) {
					return false;
				}

				int solarPowerValue = (int) Math.Round((double) Plugin.GetHsController().GetPropertyByRef(solarRef.Value, EProperty.Value));
				PowerFlow solarFlow = HSPI.GetPowerFlow(solarPowerValue);

				return SubTrig == SubTrigger.SolarProducing
					? solarFlow == PowerFlow.Positive
					: solarFlow == PowerFlow.Zero;
			
			case SubTrigger.GridImporting:
			case SubTrigger.GridExporting:
			case SubTrigger.GridIdle:
				int? gridRef = Plugin.DevRefSet?.GridPower;
				if (gridRef == null) {
					return false;
				}

				int gridPowerValue = (int) Math.Round((double) Plugin.GetHsController().GetPropertyByRef(gridRef.Value, EProperty.Value));
				PowerFlow gridFlow = HSPI.GetPowerFlow(gridPowerValue);

				if (SubTrig == SubTrigger.GridIdle) {
					return gridFlow == PowerFlow.Zero;
				}
				
				return SubTrig == SubTrigger.GridImporting
					? gridFlow == PowerFlow.Positive
					: gridFlow == PowerFlow.Negative;
			
			default:
				return false;
		}
	}

	public override bool ReferencesDeviceOrFeature(int devOrFeatRef) {
		return (string) Plugin.GetHsController().GetPropertyByRef(devOrFeatRef, EProperty.Interface) == Plugin.Id;
	}
	
	private bool? _triggerIsCondition() {
		// This is the best we can do until this is added to the plugin SDK proper.
		// It's not going to be perfect.
		// See: https://github.com/HomeSeer/Plugin-SDK/issues/237

		string appPath = Plugin.GetHsController().GetAppPath();
		FileStream eventsJsonFile;

		try {
			eventsJsonFile = File.OpenRead(Path.Combine(appPath, "Data", "HomeSeerData.json", "events.json"));
		} catch (Exception) {
			// Also try alternative path
			try {
				eventsJsonFile = File.OpenRead(Path.Combine(appPath, "Data", "HS4Data.json", "events.json"));
			} catch (Exception) {
				// Maybe we're running remotely
				return null;
			}
		}

		JToken? eventObj = null;
		
		using (eventsJsonFile) {
			byte[] buffer = new byte[eventsJsonFile.Length];
			int offset = 0;
			while (offset < eventsJsonFile.Length) {
				offset += eventsJsonFile.Read(buffer, offset, (int) eventsJsonFile.Length - offset);
			}
			
			JArray eventsArray = JArray.Parse(Encoding.UTF8.GetString(buffer));
			eventObj = eventsArray.ToList().Find(obj => (int) obj.SelectToken("evRef") == EventRef);
		}

		// Loop through all TrigGroups and see if this trigger is first in a group
		JObject? trigGroups = eventObj?.SelectToken("Triggers.TrigGroups")?.Value<JObject>();
		if (trigGroups == null) {
			return null;
		}

		int trigGroupCount = Plugin.GetHsController().GetEventByRef(EventRef).Trigger_Groups.Length;
		if (trigGroupCount != trigGroups.Count) {
			// The json file hasn't been saved to disk yet, so we're dealing with old data
			return null;
		}

		foreach (JProperty prop in trigGroups.Properties()) {
			JArray? triggers = prop.Value.SelectToken("$values")?.Value<JArray>();
			if (triggers == null) {
				continue;
			}

			List<JToken> triggerList = triggers.ToList();
			JToken trigger = triggerList.Find(trig => trig.SelectToken("mvarTI.UID") != null && (int) trig.SelectToken("mvarTI.UID") == Id);
			if (trigger == null) {
				continue;
			}

			int triggerIdx = triggerList.IndexOf(trigger);
			return triggerIdx > 0; // it's a condition if it's not the first trigger in the group
		}

		return null;
	}

	public enum SubTrigger : int {
		BatteryCharging = 0,
		BatteryDischarging,
		BatteryIdle,
		SolarProducing,
		SolarIdle,
		GridImporting,
		GridExporting,
		GridIdle
	}
}
