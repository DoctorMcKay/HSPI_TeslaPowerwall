using System;
using System.Runtime.CompilerServices;

namespace HSPI_TeslaPowerwall
{
	public class Program
	{
		public static void Main(string[] args) {
			HSPI plugin = new HSPI();
			plugin.Connect(args);
		}

		public static void WriteLog(LogType logType, string message, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null) {
#if !DEBUG
			if (logType <= LogType.Verbose) {
				// Don't log Console, Silly, and Verbose messages in production builds
				return;
			}
#endif

			string type = logType.ToString().ToLower();

#if DEBUG
			if (logType != LogType.Console) {
				// Log to HS3 log
				string hs3LogType = HSPI.PLUGIN_NAME;
				if (logType == LogType.Silly) {
					hs3LogType += " Silly";
				}

				//HsClient.WriteLog(hs3LogType, type + ": [" + caller + ":" + lineNumber + "] " + message);
			}

			Console.WriteLine("[" + type + "] " + message);
#else
			string hs3LogType = HSPI.PLUGIN_NAME;
			if (logType == LogType.Debug) {
				hs3LogType += " Debug";
			}
			
			HsClient.WriteLog(hs3LogType, type + ": " + message);
#endif
		}
	}

	public enum LogType
	{
		Console = 1,				// DEBUG ONLY: Printed to the console
		Silly = 2,					// DEBUG ONLY: Logged to HS3 log under type "PluginName Silly"
		Verbose = 3,				// DEBUG ONLY: Logged to HS3 log under normal type
		Debug = 4,					// In debug builds, logged to HS3 log under normal type. In production builds, logged to HS3 log under type "PluginName Debug"
		Info = 5,
		Warn = 6,
		Error = 7,
		Critical = 8,
	}
}
