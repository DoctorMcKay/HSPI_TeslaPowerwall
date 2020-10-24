# Tesla Powerwall for HS4

This is a free, open-source HomeSeer plugin that enables monitoring of a Tesla Powerwall system.
This plugin is written natively for HS4.

It is also available for HS3 via the HomeSeer plugin updater, although the HS3 version is not
receiving any new updates.

# Installation

This plugin is available in the HomeSeer plugin updater.

# Configuration

This plugin communicates with the Tesla Gateway over the LAN. Consequently, it does not require
Internet access nor does it require your Tesla Account credentials. However, your Gateway must
be connected to your local network (either via Wi-Fi or wired connection), and must have a static
IP address configured.

You may configure a static IP address either in your router's control panel by assigning a static
DHCP lease (this feature is not available on all routers), or directly in the Gateway's web control
panel.

### Gateway Network Connectivity

The Tesla Gateway can connect to the Internet in three ways:

- An internal cellular connection
- A Wi-Fi connection provided by the site
- A wired Ethernet connection provided by the site

If possible, a wired connection is recommended for best results. If unavailable, a strong Wi-Fi signal
will work as well. If your Gateway is installed outdoors, it may not have good Wi-Fi reception, so you
may need to install an access point or repeater closer to the Gateway. **While the internal cellular
connection will enable you to monitor your system via the mobile app, it will not work with this plugin.**

Tesla encourages you to configure both wired Ethernet and Wi-Fi if possible, but the plugin will only
be able to work with one of these connections, since both connections cannot use the same IP address
and this plugin can only work with one IP. therefore, if you have both connections configured, you should
configure your static IP on the wired Ethernet interface and use that IP in the plugin configuration.

### Configuring a Static IP in Your Gateway

To configure a static IP via the Gateway's web control panel, first log into the Gateway. If you
know your Gateway's LAN IP address already, you can type it directly into your web browser to access
the control panel.

If you do not know your Gateway's LAN IP, you can connect to the Gateway's Wi-Fi
signal, which is named "TEG-###" where ### is the last 3 characters of the Gateway's serial number.
The Wi-Fi password is on a sticker inside the Gateway cabinet's door. If there is no password on the
label, then the password is "S" followed by the full serial number of the Gateway. Once connected to
the Gateway's Wi-Fi signal, visit https://192.168.91.1 into your browser.

Regardless of how you connect to your Gateway, you will need to click through the privacy warning
given by your browser. Since you will be communicating with your Gateway over your internal network,
you needn't worry about the implications of this warning.

Click "Network" in the control panel and sign in with your Powerwall password. This is separate from
your Tesla Account password. If you don't know your Powerwall password, you may reset it by following
the instructions in the control panel. Make sure you're logging in as Customer rather than Installer.

Once signed in, select either Ethernet (for a wired connection) or Wi-Fi (for a wireless connection).
First take a note of the information currently displayed, then select "Static" and enter that same
information into the form. Finally, click "Connect".

### Configuring the Plugin

Once you have your static IP configured, open HomeSeer and select Plug-Ins > Tesla Powerwall > Settings.
In the form that appears, enter the IP address you configured in your Gateway and click Done. The plugin
will automatically connect to the Gateway and will create a set of status devices to indicate the status
of your system.

If you do not see the status devices, make sure that your filters are set properly. The type of the devices
is "Tesla Powerwall". You may also need to adjust your floor/room filters as the devices do not have these
values configured by default.

# Monitored Values

Here are the values that are monitored by the plugin. Each value is available as a separate status device.

- System Status (default name is your Gateway name): Indicates whether the system is running or stopped
- Tesla Connection: Indicates whether the Gateway is connected to Tesla
- Grid Status: Indicates whether the utility grid is up or down
- Powerwall Charge: Indicates the current state of charge of your Powerwall network
- Total Site Power: The instantaneous current draw from all sources (grid, solar, and Powerwall)
- Powerwall Power: The instantaneous current draw from all your Powerwall units combined
	- May be negative if Powerwalls are currently charging
- Solar Power: The instantaneous current draw from your solar source
- Grid Power: The instantaneous current draw from your utility connection
	- May be negative if you are currently exporting solar power to the grid

### Power Values

Please note that power values are in Watts, but are displayed in the HomeSeer UI as a rounded kW value.
Therefore, when creating events comparing power usage, you will need to work in watts. For example, if you
wanted an event to trigger when you draw more than 5 kW from the grid, you will need this trigger:

> IF **This device had its value set and is greater than...** **Grid Power** is greater than a value in the range...
**(value) W** the value, using values from -1000000 to 1000000 is **5000**.

Please note that this event will trigger every two seconds while power is over 5 kW, so you will likely also want
to check the status of the device you want to control in response to power draw.

Additionally, please note that solar power **may be negative**. Under normal circumstances, the UI will not show
negative solar power due to rounding, but during the nighttime, your inverter will draw current from the grid to stay
awake, which will register a few watts in the negative direction. Therefore, if you want an event to trigger when solar
is not producing, you should check whether solar power is less than 1 W rather than checking if it's 0.

# Removing Your Gateway Device

Should you ever need to delete your Backup Gateway from HS4, which you may need to do if your Gateway's IP changes, as
this will create a new set of devices in HS4, first disable the plugin and then manually delete the device and all its
features.

At the top-right of the Devices page is a button which looks like a checklist. Click this button to enable bulk device
editing. Then select the checkbox next to the Gateway device, which should also automatically select all of its features.
If the features are not automatically selected, manually select each one. Then scroll back to the top of the page and
select Bulk Action > Delete.
