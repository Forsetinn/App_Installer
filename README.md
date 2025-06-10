# App Installer

A desktop utility that helps you install common applications on Windows. It
provides a friendly front end for the `winget` command line tool and the
Microsoft Store.

The interface uses the WPF UI library with a dark theme and Mica backdrop,
automatically matching the system theme.

## Features

- **Predefined app list** – check items in the left pane to quickly install popular applications.
- **Search** – look up packages from `winget` or the Microsoft Store and view them in a paged grid.
- **Batch install** – install all checked apps with a single click. The app runs `winget` silently in the background.
- **Import/Export** – save or load your app list to a JSON file.
- **Logging** – operations are recorded in `logs/app_installer_log.txt`.

## Getting started

1. Build the project with Visual Studio or `dotnet build`.
2. Run the resulting executable.
3. Use the search box to find packages or choose from the predefined list.
4. Click **Install Selected** to install checked apps.

This simple UI helps streamline setting up a new Windows machine by bundling
your favorite applications into a single installer.

### This is a personal project and will probably never fully release or be signed. Originally written in PowerShell but recently ported to C#
