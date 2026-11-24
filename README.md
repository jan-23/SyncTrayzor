
SyncTrayzor [![Build](https://github.com/GermanCoding/SyncTrayzor/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/GermanCoding/SyncTrayzor/actions/workflows/dotnet-build.yml) [![Translation status](https://hosted.weblate.org/widget/syncthing/synctrayzor/synctrayzor/svg-badge.svg)](https://hosted.weblate.org/engage/syncthing/)
===========

> [!NOTE]
> This is SyncTrayzor v2, a continuation of the (discontinued) original SyncTrayzor by @canton7 (Antony Male)


Quick Links
-----------

 - [Download and install SyncTrayzor now](#installation)
 - [Something went wrong](#something-went-wrong)
 - [Find Syncthing/SyncTrayzor useful? Please donate!](DONATING.md#donating)


Introduction
------------

SyncTrayzor is a little tray utility for [Syncthing](https://syncthing.net/) on Windows.
It hosts and wraps Syncthing, making it behave more like a native Windows application and less like a command-line utility with a web browser interface.

Features include:

 - Has a built-in web browser, so you don't need to fire up an external browser.
 - Optionally starts on login, so you don't need to set up Syncthing as a service.
 - Has drop-box style file download / progress window
 - Tray icon indicates when synchronization is occurring.
 - Alerts you when:
    - You have file conflicts
    - One of your folders is out of sync
    - Folders finish syncing
    - Devices connect / disconnect
 - Has a tool to help you resolve file conflicts
 - Can pause devices on metered networks, to stop Syncthing transferring data on e.g. a mobile connection or wifi hotspot.
 - Optional [Windows Explorer integration](#windows-explorer-integration): choose which subfolders are kept on this device, pause folders from the right-click menu, and see the sync status as icons.
 - Contains translations for many languages


![Screenshot](readme/screenshot.png)

Installation
------------

SyncTrayzor is packaged as both an installer and standalone zip. It will run on Windows 10 or newer.

Additionally, SyncTrayzor is available on several package managers:

* **winget** - `winget install GermanCoding.SyncTrayzor`
* **chocolatey** - `choco install synctrayzor`
* **scoop** - `scoop install extras/synctrayzor`

### Installer

[Download `SyncTrayzorSetup-x64.exe` or `SyncTrayzorSetup-arm64.exe`](https://github.com/GermanCoding/SyncTrayzor/releases/latest) and run it.
If you already have SyncTrayzor installed, this will update it.

Please note that 32-bit builds are no longer offered. If you still have a 32-bit SyncTrayzor install, you should uninstall it first.

### Standalone

[Download `SyncTrayzorPortable-x64.zip` or `SyncTrayzorPortable-arm64.zip`](https://github.com/GermanCoding/SyncTrayzor/releases/latest).
Unzip, and run `SyncTrayzor.exe`. If you're updating, you'll need to copy the `data` folder across from your previous standalone installation.

Something went wrong!
---------------------

First, what went wrong? Remember that SyncTrayzor is a wrapper around [Syncthing](https://github.com/syncthing/syncthing).
Syncthing is the file synchronization program, which has a web interface.
SyncTrayzor is the tray utility, looks like a normal Windows program, and has menus and suchlike, and a big area where Syncthing's web interface is displayed.

 - If you're having problems connecting to other devices, your files aren't synchronizing, or you're getting error messages in the console area, [raise an issue with Syncthing](https://github.com/syncthing/syncthing/issues/new) or [post on the Syncthing forums](https://forum.syncthing.net).
 - If SyncTrayzor isn't talking to Syncthing, you're getting error dialogs, it's not auto-starting, etc, [raise an issue with SyncTrayzor](https://github.com/GermanCoding/SyncTrayzor/issues/new).
 - If you're not sure, open a new discussion topic and someone will guide you to an appropiate place.


Contributing
------------

Multi-lingual? SyncTrayzor needs you! Please read [Localization](https://github.com/GermanCoding/SyncTrayzor/wiki/Localization).

Want to make a contribution? Fantastic, and thank you! Please read [Contributing](https://github.com/GermanCoding/SyncTrayzor/wiki/Contributing) first.


Find Syncthing/SyncTrayzor useful? Please donate!
-------------------------------------------------

Please see [DONATING](DONATING.md#donating).


Will SyncTrayzor phone home / give away my secrets / etc?
---------------------------------------------------------

SyncTrayzor's auto-update mechanism will periodically call a script (found in `server/version_check.php`, hosted on a server of mine) to find out if there's an upgrade available.
It will disclose your current SyncTrayzor version, and the package you downloaded (installer/portable and x64/x86).
It will also reveal your IP address, which is unavoidable.

You can turn off auto-updates in the Settings, but in that case you are responsible for manually checking for updates.

Usage information, details of your computer / network / syncthing setup / etc are not reported.


How can I be sure that I'm not downloading a virus?
---------------------------------------------------

You can be paranoid and build everything from source (after auditing the source), or you can trust that I'm a good person who's trusted by the Syncthing developers.

You can verify that this GitHub account hasn't been compromised and is releasing malicious downloads by following the steps in the [Security README](https://github.com/GermanCoding/SyncTrayzor/tree/master/security).
This process also lets you verify that Syncthing's developers trust me to release non-malicious software.


What will SyncTrayzor do to Syncthing?
--------------------------------------

It's worth noting that SyncTrayzor will override the 'GUI Listen Address' and 'API Key' in Syncthing's configuration.
This is because it needs to fully control these values, in order to ensure that it can communicate with Syncthing.

However, you can set these values in File -> Settings, if you want to customise them.


What will SyncTrayzor do to my system?
--------------------------------------

Good question. The answer depends on whether you installed SyncTrayzor using the installer, or are running it standalone.

### Installed

The Installed version of SyncTrayzor will install itself into `C:\Program Files\SyncTrayzor` or `%LocalAppData%\Programs\SyncTrayzor` for all-users or current-user version respectively.

By default, SyncTrayzor will put its own configuration in `C:\Users\<You>\AppData\Roaming\SyncTrayzor`, and let Syncthing use its default folder for its database, which is `C:\Users\<You>\AppData\Local\Syncthing`.
It will also create a registry key at `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\SyncTrayzor` the first time that it is run, which will let it start when you log in.

You can delete this registry key by unchecking "Automatically start on login" in the settings.

You can set a custom home path for Syncthing in the settings if you wish (Settings -> Syncthing -> Advanced -> Syncthing Custom Home Path).
This is useful if you want to keep the copy of Syncthing managed by SyncTrayzor separate from another copy running on your machine.

The auto-update mechanism may download updates to `%TEMP%\SyncTrayzor`.
This location is periodically cleared out.

### Portable

SyncTrayzor Portable will put its own configuration in `SyncTrayzorPortable\data`, and tell Syncthing to use `SyncTrayzorPortable\data\syncthing` for its database.
This means that, when manually upgrading, you can simply move the 'data' folder over to move all your settings and database.

The portable version won't start on login by default. If you check "Automatically start on login" in the settings, then a registry key will be created at `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run\SyncTrayzor`.

The auto-update mechanism may download updates to `%TEMP%\SyncTrayzor`.
This location is periodically cleared out.


Migrating between "raw" Syncthing, Portable, and Installed SyncTrayzor
----------------------------------------------------------------------


Here's a handy table of what you need to copy where if you're migrating between different combinations of "raw" Syncthing, SyncTrayzor Installed, and SyncTrayzor Portable.

| From | To | Actions |
| --- | --- | --- |
| "raw" Syncthing | SyncTrayzor Installed | None | 
| "raw" Syncthing | SyncTrayzor Portable | Copy the contents of `C:\Users\<You>\AppData\Local\Syncthing` into the `data\syncthing` folder inside the portable directory |
| SyncTrayzor Installed | "raw" Syncthing | None |
| SyncTrayzor Installed | SyncTrayzor Portable | <ol><li>Copy the contents of `C:\Users\<You>\AppData\Local\Syncthing` into the `data\syncthing` folder inside the portable directory</li><li>Copy `C:\Users\<You>\AppData\Roaming\SyncTrayzor\config.xml` to `data\config.xml`</li></ol> |
| SyncTrayzor Portable | "raw" Syncthing | Copy the contents of the `data\syncthing\` folder inside the portable directly into `C:\Users\<You>\AppData\Local\Syncthing` |
| SyncTrayzor Portable | SyncTrayzor Installed | <ol><li>Copy the contents of the `data\syncthing` folder inside the portable directory into `C:\Users\<You>\AppData\Local\Syncthing`</li><li>Copy `data\config.xml` to `C:\Users\<You>\AppData\Roaming\SyncTrayzor\config.xml`</li></ol> |


Backups
-------

For the installed version of SyncTrayzor, in order to save your configuration (both Syncthing's and SyncTrayzor's), you will need to backup the following paths:

 - `C:\Users\<You>\AppData\Roaming\SyncTrayzor`
 - `C:\Users\<You>\AppData\Local\Syncthing`


Windows Explorer integration
----------------------------

Enable it in File -> Settings -> Windows Explorer. It's off by default.

**Choose what's kept on this device.** Like "selective sync" in other sync tools, you can keep only some subfolders of a Syncthing folder on this device, while your other devices keep everything.
Right-click a subfolder and choose SyncTrayzor -> "Remove from this device", or right-click a Syncthing folder and choose "Choose subfolders…" to tick the subfolders to keep.

Removing a subfolder only deletes it here, never on other devices, and asks for confirmation first. SyncTrayzor:

 1. has Syncthing rescan the subfolder, and checks every file in it: Syncthing's index must match the file on disk, this device must have the newest version (no unsynced local changes), and another device that's connected right now must have a valid copy of that version (an untrusted device, which only has an encrypted copy, doesn't count),
 2. adds the subfolder to the folder's ignore patterns (a line like `/Photos/2019` at the top of `.stignore`),
 3. waits until Syncthing confirms that it's ignored, so Syncthing won't notice the deletion and won't sync it to other devices,
 4. checks again that another device still has each file (in case it removed the same subfolder at the same time), and moves the checked files to the Recycle Bin, if they haven't changed since, leaving an empty placeholder folder with a grey icon.

If a check fails, nothing is changed. It also refuses if:
 - the subfolder contains files which the ignore patterns already exclude (this device has the only copy of those, except for `(?d)` patterns),
 - it contains another Syncthing folder, or another Syncthing folder contains it (which would sync the deletion to its own devices),
 - it, a folder above it, or anything in it is a link or junction,
 - it contains a file used by an `#include` in the ignore patterns,
 - its drive has no Recycle Bin, or a path in it is too long for the Recycle Bin.

If something goes wrong once `.stignore` is being changed (Syncthing doesn't confirm, a file changes meanwhile, another device no longer has a file, a file can't be moved to the Recycle Bin), `.stignore` is restored: the subfolder is synced as before, and Syncthing downloads anything already moved to the Recycle Bin again.
If even that fails (e.g. Syncthing stopped meanwhile), SyncTrayzor says so, and restores it the next time Syncthing runs.
Several subfolders removed together (e.g. in "Choose subfolders…") change `.stignore` only once, so Syncthing only rescans once.

Removing subfolders needs a Syncthing version newer than v2.1.7. Older versions also report a file as present on another device when that device ignores it (and may have deleted it, e.g. by removing the same subfolder), so SyncTrayzor couldn't make sure the files are safe elsewhere. With older versions, "Remove from this device" isn't offered; downloading subfolders still works. If you know that no other device ignores or removes these subfolders, you can allow it anyway in Settings ("Allow removing subfolders with Syncthing v2.1.7 and older"), at your own risk.
At the time of writing, no such Syncthing release exists yet (the fix is merged, but not released), and SyncTrayzor ships Syncthing v2.0.3: removing subfolders becomes available once Syncthing is updated to a newer release.

Keep in mind that removing a subfolder relies on your other devices: once it's removed here, deleting it on the device that still has it deletes the last copy. SyncTrayzor can't tell whether another device still has the files on disk, only what it last reported.

To bring a subfolder back, right-click its placeholder and choose "Download to this device", or tick it in "Choose subfolders…". Syncthing then downloads it again.
Deleting a placeholder is harmless: it's ignored, so nothing happens on other devices, and it's still listed in "Choose subfolders…".
Files you put into a placeholder aren't synced (it's ignored), until you download the subfolder again.
New subfolders created on other devices are synced to this device as usual: you choose which subfolders not to keep, rather than which ones to keep.

The right-click menu also lets you pause or resume a Syncthing folder and rescan items. On Windows 11, it's under "Show more options".

**Status icons.** These need the "Show sync status icons in Windows Explorer" option of the installer, and Explorer to be restarted (or sign out and in again).

 - Up to date (green check): this device has the latest version of the item, and of everything inside it.
   Syncthing doesn't track whether *other* devices have received your changes, so this says nothing about them.
 - Syncing (blue arrow): this device is missing the latest version, or the item was changed and Syncthing hasn't scanned it yet.
 - Ignored (grey minus): the item isn't synced to this device, e.g. a removed subfolder's placeholder.
 - Error (red cross): the item (or something inside it) failed to sync.

Windows can show only a few kinds of overlay icons in total, across all programs, and sync tools like OneDrive and Dropbox take most of them.
If those are installed, some or all of SyncTrayzor's icons may not appear.

**What it does to your system.** SyncTrayzor registers its context menu handler (`SyncTrayzor.ShellExtension.dll`) for your user in `HKEY_CURRENT_USER\Software\Classes` while the setting is enabled, and removes it when you disable it.
The installer option registers the status icon handlers for all users.
Explorer talks to SyncTrayzor over a named pipe that only your user can connect to; if SyncTrayzor isn't running, Explorer shows nothing.


Using Syncthing beta releases
-----------------------------

### Installed

SyncTrayzor keeps two copies of syncthing.exe.

The copy that is executed is at `C:\Users\<You>\AppData\Roaming\SyncTrayzor\syncthing.exe`.
This one is writable by the user, allowing Syncthing to auto-upgrade.

There's also a backup copy in SyncTrayzors main install directory.
This one is copied to `C:\Users\<You>\AppData\Roaming\SyncTrayzor\syncthing.exe` if that one does not exist (SyncTrayzor has never been run before for that user, for example). SyncTrayzor will attempt to upgrade this version when copying it.

If you want to manually upgrade (e.g. to a beta version) you should download Syncthing from [the releases page](https://github.com/syncthing/syncthing/releases), and replace the `syncthing.exe` in `C:\Users\<You>\AppData\Roaming\SyncTrayzor`.


### Portable

SyncTrayzor keeps two copies of syncthing.exe.

There's a copy in `data\syncthing.exe`, which is the one which is run. There's also a copy next to `SyncTrayzor.exe`, which is used if the one in `data\` is missing.

If you want to manually upgrade (e.g. to a beta version) you should download Syncthing from [the releases page](https://github.com/syncthing/syncthing/releases), and replace the one at `data\syncthing.exe`.


Building from Source
--------------------

You'll need the [VC++ redist](https://aka.ms/vs/17/release/vc_redist.x64.exe) (2022+ release) installed. Most Windows machines have it already. Next, you need to install the [.NET SDK](https://dotnet.microsoft.com/en-us/download), currently 8.0 LTS. Some IDEs, like Visual Studio Code or JetBrains Rider can also help you in setting this up.

Clone/download the repository, open `src\SyncTrayzor.sln` in your IDE, and compile. If you've compiled .NET programs before, this should be familiar.

The Explorer integration (`src\SyncTrayzor.ShellExtension`) is a C++ project and needs Visual Studio with the "Desktop development with C++" workload. It isn't part of `SyncTrayzor.sln`; `task build-portable` and `task build-installer` build it, or build it directly with `msbuild src\SyncTrayzor.ShellExtension\SyncTrayzor.ShellExtension.vcxproj -p:Configuration=Release -p:Platform=x64`. To build a distribution without it (and without the Explorer integration), set the environment variable `SYNCTRAYZOR_SKIP_SHELL_EXTENSION=1`.

You'll also need to [download syncthing.exe](https://github.com/syncthing/syncthing/releases) and place it in the binary output folder as appropriate (SyncTrayzor will guide you to the exact location in an error message, if required).


Notes for System Administrators
-------------------------------

The installer is built using Inno Setup, and has various command-line options, [documented here](http://www.jrsoftware.org/ishelp/index.php?topic=setupcmdline).
If you pass the `/silent` command-line flag when SyncTrayzor won't be launched when the installer completes: add `/StartSyncTrayzor` to override this (which also causes SyncTrayzor to be launched minimized).

There are various parameters inside the file `SyncTrayzor.exe.config` which can be customised by system administrators, including the default SyncTrayzor configuration (used to create the user's SyncTrayzor config file when SyncTrayzor is first launched).
To override these, pass the flag `/SyncTrayzorExeConfig="Path\To\Customized\SyncTrayzor.exe.config"` to the installer -- the specified `SyncTrayzor.exe.config` will overwrite the default.

Note that the contents / structure of `SyncTrayzor.exe.config` may change between releases.
Using the wrong version may cause a crash, or incorrect behaviour.
