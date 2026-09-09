# WinCalendar

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md)

Click the clock in the Windows taskbar to view your calendar, holidays, and events.

**[Download the latest version](https://github.com/houjie1212/WinCalendar/releases/latest)** · [Report an issue](https://github.com/houjie1212/WinCalendar/issues)

For Windows 11 x64. No additional runtime installation is needed. See the [development guide (Chinese)](docs/development.md#兼容与验证) for Windows version testing status.

- **Installer (recommended)**: Download `WinCalendar-Setup-{version}-x64.exe` and follow the setup instructions.
- **Portable version**: Download `WinCalendar-{version}-win-x64.zip`, extract everything, and run `WinCalendar.exe`. Keep the entire folder together.

The installer is unsigned, so Windows may show an unknown publisher warning. Download it from the release page linked above.

## Features

- **Holidays and workdays**: Add a compatible China holidays subscription to see holiday names, days off, and compensatory workdays.
- **Optional lunar calendar**: Enable the Chinese lunar calendar in settings.
- **Calendar subscriptions**: Choose a preset for China, Japan, or US holidays, or add your own calendar subscription link.
- **Subscription colors**: Distinguish subscriptions by their background colors. Each day shows up to three colors; all events remain available.
- **Quick navigation**: Pick a year and month, use the arrow buttons or mouse wheel, or select “Today”.
- **System appearance**: Follows light and dark themes and supports Simplified Chinese, Traditional Chinese, English, and Japanese.

## Screenshots

### Calendar

These screenshots show the Chinese interface. Holiday subscriptions and the lunar calendar are enabled in the example; these are not the default settings.

<img src="docs/images/main-window.png" alt="WinCalendar calendar with holiday colors, workday indicators, and Chinese lunar dates" width="440">

### Settings

<img src="docs/images/settings-window.png" alt="WinCalendar settings with subscriptions, presets, and display options" width="520">

## Getting started

1. Install and start WinCalendar. Exit any other taskbar calendar tools first.
2. Click the system clock to open the calendar. Click it again, click outside the panel, or press Esc to close it.
3. Open settings with the gear button. Choose a preset or select “Add subscription” to enter your own link.
4. Save the subscription editor, then select “Save” in settings. You can also enable lunar dates or launch at startup here.

## Frequently asked questions

### Why are holidays and lunar dates missing?

No subscriptions are added by default, and lunar dates are initially off. Add and enable the China holidays preset to display holidays and workday indicators. Custom links must use the “China holidays” type and a compatible source format. Enable the lunar calendar separately.

Ordinary calendar subscriptions display events and colors without generating Chinese workday indicators. Event titles, locations, and descriptions stay in their original language.

### How often do subscriptions refresh? What if a refresh fails?

Subscriptions refresh every 30 minutes by default; you can change this in settings. “Refresh now” saves your settings before fetching events. Previously downloaded events remain available if a refresh fails.

Only directly accessible public calendar links are supported. Private-network links and subscriptions requiring a proxy cannot refresh. Check your connection and link. WinCalendar only reads calendars; it does not edit the original events or offer account sign-in.

### How do I update?

Select “Check for updates” in settings, then follow the prompts to update and restart. WinCalendar does not check for or download updates automatically. Save any pending settings changes first.

If updating fails, download the latest installer and install over the existing version to keep your settings and subscriptions.

### How do I exit or uninstall?

Select “Exit” in settings to restore the clock's normal behavior. Launch at startup is off by default and can be enabled in settings.

For installed copies, select “Uninstall” in settings or use Windows “Installed apps”. **Uninstalling deletes subscriptions, settings, and downloaded data by default.** Select the option to keep subscriptions and settings if you want to retain them. Canceling the uninstall does not delete data.

### Where is my data stored?

Settings and downloaded events are stored locally in `%LOCALAPPDATA%\WinCalendar`.

Subscription addresses are encrypted. You may need to enter them again after switching computers or Windows accounts. Downloaded events are not encrypted; do not share the entire data folder. This protection cannot prevent programs that have already taken control of your Windows account from reading your data.

## Feedback and development

[Report an issue or suggest a feature](https://github.com/houjie1212/WinCalendar/issues). Include your Windows version, app version, and steps to reproduce the issue. Keep private subscription links out of screenshots.

The following documentation is currently in Chinese:

- [Development guide](docs/development.md): building, checks, subscription rules, and update recovery.
- [Installer guide](Installer/README.md): installation, removal, and packaging.
- [Validation records](QA.md): completed checks and scenarios still awaiting testing.
- [Third-party notices](THIRD-PARTY-NOTICES.md).
