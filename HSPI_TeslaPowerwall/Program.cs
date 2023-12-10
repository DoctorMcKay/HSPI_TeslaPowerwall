namespace HSPI_TeslaPowerwall;

public class Program {
	public static void Main(string[] args) {
		HSPI plugin = new HSPI();
		plugin.Connect(args);
	}
}