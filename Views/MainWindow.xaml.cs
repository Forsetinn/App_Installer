using AppInstaller.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;  // ← Correct namespace for ObservableCollection
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Appearance;  // For ThemeService and ApplicationTheme
using Wpf.Ui.Controls;    // For FluentWindow (and other WPF UI controls)

namespace AppInstaller.Views
{
    public partial class MainWindow : FluentWindow
    {
        // ── Pagination constants ─────────────────────────────────────────────────
        private const int PageSize = 20;
        private int CurrentPage = 1;

        // ── Collections bound to UI ──────────────────────────────────────────────
        private ObservableCollection<SearchResult> SearchResults = new();
        private List<AppItem> PredefinedApps = new();

        // ── Winget info ─────────────────────────────────────────────────────────
        private string WingetPath;
        private Version WingetVersion;
        private bool SupportsAgreements;

        // ── Logging ──────────────────────────────────────────────────────────────
        private readonly string LogFile = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "logs", "app_installer_log.txt"
        );

        public MainWindow()
        {
            // 1) Load all XAML (including ExtendsContentIntoTitleBar="True")
            InitializeComponent();

            // 2) Apply Mica backdrop
            WindowBackdropType = WindowBackdropType.Mica;

            // 3) Watch for system‐theme changes
            SystemThemeWatcher.Watch(this);

            // 4) Hook Loaded event
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // ── 1) Verify winget.exe exists & get version ─────────────────────────
            try
            {
                var wingetExe = "winget.exe";
                var psi = new ProcessStartInfo(wingetExe, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                var versionString = proc.StandardOutput.ReadToEnd().Trim(); // e.g. "v1.4.10273"
                proc.WaitForExit();

                WingetPath = wingetExe;
                WingetVersion = Version.Parse(versionString.TrimStart('v'));
                SupportsAgreements = WingetVersion >= new Version(1, 10, 0);
                WriteLog($"Winget version {WingetVersion} found. Agreements supported: {SupportsAgreements}");
            }
            catch (Exception)
            {
                // Fully qualify MessageBox calls to avoid ambiguity with Wpf.Ui.Controls.MessageBox
                System.Windows.MessageBox.Show(
                    "winget.exe not found. Please install Windows Package Manager.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error
                );
                WriteLog("Error: winget not found. Exiting.");
                Close();
                return;
            }

            // ── 2) Initialize PredefinedApps (the “favorites” list on the left) ───
            PredefinedApps = new List<AppItem>()
            {
                new() { Name = "Microsoft 365 Apps for enterprise",  Id = "Microsoft.Office",                Source = "winget", IsChecked = false },
                new() { Name = "Adobe Acrobat Reader DC",             Id = "Adobe.Acrobat.Reader.64-bit",     Source = "winget", IsChecked = false },
                new() { Name = "Foxit PDF Reader",                    Id = "Foxit.FoxitReader",               Source = "winget", IsChecked = false },
                new() { Name = "Foxit PDF Editor (Paywall)",          Id = "Foxit.PhantomPDF",                Source = "winget", IsChecked = false },
                new() { Name = "VLC Media Player",                    Id = "VideoLAN.VLC",                    Source = "winget", IsChecked = false },
                new() { Name = "DisplayLink Graphics",                Id = "DisplayLink.GraphicsDriver",      Source = "winget", IsChecked = false },
                new() { Name = "Google Chrome",                       Id = "Google.Chrome",                   Source = "winget", IsChecked = false },
                new() { Name = "Mozilla Firefox",                     Id = "Mozilla.Firefox",                 Source = "winget", IsChecked = false },
                new() { Name = "Spotify",                             Id = "Spotify.Spotify",                 Source = "winget", IsChecked = false },
                new() { Name = "Slack",                               Id = "SlackTechnologies.Slack",         Source = "winget", IsChecked = false },
                new() { Name = "Paint.NET",                           Id = "dotPDNLLC.paintdotnet",           Source = "winget", IsChecked = false },
                new() { Name = "Notepad++",                           Id = "Notepad++.Notepad++",             Source = "winget", IsChecked = false },
                new() { Name = "Microsoft Visual Studio Code",        Id = "Microsoft.VisualStudioCode",      Source = "winget", IsChecked = false }
            };

            // ── 3) Bind the “favorites” ListBox to PredefinedApps ────────────────
            AppListBox.ItemsSource = PredefinedApps;

            // ── 4) Bind the DataGrid (ResultsGrid) to SearchResults ─────────────
            ResultsGrid.ItemsSource = SearchResults;

            // ── 5) Hide progress bar initially ───────────────────────────────────
            SearchProgressBar.Visibility = Visibility.Collapsed;
            PageInfoText.Text = "";
            StatusText.Text = "Ready";

            // ── 6) Sync theme toggle if you have one (omitted here) ─────────────
            var currentTheme = ApplicationThemeManager.GetAppTheme();
        }

        #region Search Logic

        private void ListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // If the click was on (or inside) a CheckBox, do nothing
            var original = (DependencyObject)e.OriginalSource;
            if (FindVisualParent<CheckBox>(original) != null)
                return;

            // Otherwise treat it as “single-check”
            var itemContainer = (ListBoxItem)sender;
            if (itemContainer.DataContext is AppItem clickedApp)
            {
                // Uncheck all others
                foreach (var app in PredefinedApps)
                    app.IsChecked = false;

                // Check only this one
                clickedApp.IsChecked = true;
            }

            // Prevent default ListBox selection
            e.Handled = true;
        }

        // Helper to walk up the Visual Tree
        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T correctlyTyped) return correctlyTyped;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            string term = SearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(term))
            {
                StatusText.Text = "Please enter a search term.";
                return;
            }

            bool searchWinget = SearchWingetCheck.IsChecked == true;
            bool searchMsStore = SearchMSStoreCheck.IsChecked == true;
            if (!searchWinget && !searchMsStore)
            {
                StatusText.Text = "Please select at least one source.";
                return;
            }

            // Disable Search button, show progress bar
            SearchButton.IsEnabled = false;
            SearchProgressBar.Visibility = Visibility.Visible;
            StatusText.Text = "Searching…";

            // Clear previous results, reset page
            SearchResults.Clear();
            CurrentPage = 1;
            UpdateResultsGrid();

            // Build array of sources
            var sources = new List<string>();
            if (searchWinget) sources.Add("winget");
            if (searchMsStore) sources.Add("msstore");

            try
            {
                var results = await RunSearchAsync(term, sources.ToArray());
                foreach (var r in results.OrderByDescending(r => r.RelevanceScore))
                {
                    SearchResults.Add(r);
                }
                StatusText.Text = $"Found {SearchResults.Count} results";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Search error: {ex.Message}";
                WriteLog($"Search error: {ex}");
            }
            finally
            {
                SearchButton.IsEnabled = true;
                SearchProgressBar.Visibility = Visibility.Collapsed;
                UpdateResultsGrid();
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true; // eliminate the “ding”
                SearchButton_Click(SearchButton, new RoutedEventArgs());
            }
        }


        private async Task<List<SearchResult>> RunSearchAsync(string searchTerm, string[] sources)
        {
            return await Task.Run(() =>
            {
                var allResults = new List<SearchResult>();

                foreach (var source in sources)
                {
                    WriteLog($"Searching '{searchTerm}' in source: {source}");
                    var resultsForSource = new List<SearchResult>();

                    try
                    {
                        string jsonArgs;
                        if (source == "msstore")
                        {
                            jsonArgs = $"search \"{searchTerm}\" --source {source} --output json" +
                                      (SupportsAgreements ? " --accept-source-agreements" : "");
                        }
                        else
                        {
                            jsonArgs = $"search --query \"{searchTerm}\" --source {source} --output json" +
                                      (SupportsAgreements ? " --accept-source-agreements" : "");
                        }

                        WriteLog($"Running command: {WingetPath} {jsonArgs}");

                        var psi = new ProcessStartInfo(WingetPath, jsonArgs)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var proc = Process.Start(psi);
                        string rawJson = proc.StandardOutput.ReadToEnd();
                        string errorOutput = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();

                        WriteLog($"Exit code: {proc.ExitCode}");
                        if (!string.IsNullOrWhiteSpace(errorOutput))
                        {
                            WriteLog($"Error output: {errorOutput}");
                        }
                        WriteLog($"JSON output length: {rawJson.Length}");

                        // Attempt to parse JSON
                        var jsonElems = JsonSerializer.Deserialize<List<JsonElement>>(rawJson);
                        if (jsonElems != null && jsonElems.Count > 0)
                        {
                            WriteLog($"Found {jsonElems.Count} JSON elements");
                            foreach (var elem in jsonElems)
                            {
                                if (!elem.TryGetProperty("Name", out var nameElem) ||
                                    !elem.TryGetProperty("Id", out var idElem))
                                {
                                    continue;
                                }

                                string name = nameElem.GetString() ?? "";
                                string id = idElem.GetString() ?? "";

                                WriteLog($"Processing: {name} ({id})");

                                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                                    continue;

                                // Log if skipping due to non-English
                                if (!TestEnglishOnly(name))
                                {
                                    WriteLog($"Skipped non-English: {name}");
                                    continue;
                                }

                                string version = "Unknown";
                                if (elem.TryGetProperty("Version", out var vElem))
                                    version = vElem.GetString() ?? "Unknown";

                                var sr = new SearchResult
                                {
                                    Name = name,
                                    Id = id,
                                    Source = source,
                                    Version = version,
                                    RelevanceScore = ComputeRelevanceScore(searchTerm, name, id)
                                };
                                resultsForSource.Add(sr);
                                WriteLog($"Added result: {name} from {source}");
                            }
                        }
                        else
                        {
                            WriteLog("No JSON elements found or null result");
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"JSON parsing failed for {source}: {ex.Message}");
                    }

                    WriteLog($"JSON search found {resultsForSource.Count} results for {source}");

                    if (resultsForSource.Count > 0)
                    {
                        allResults.AddRange(resultsForSource);
                    }
                    else
                    {
                        WriteLog($"Falling back to text parsing for {source}");
                        var textResults = ParseTextOutput(searchTerm, source).ToList();
                        WriteLog($"Text parsing found {textResults.Count} results for {source}");
                        allResults.AddRange(textResults);
                    }
                }

                WriteLog($"Total results before deduplication: {allResults.Count}");

                // Deduplicate by ID
                var unique = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in allResults.OrderByDescending(r => r.RelevanceScore))
                {
                    if (!unique.ContainsKey(item.Id))
                        unique[item.Id] = item;
                }

                WriteLog($"Total unique results: {unique.Count}");

                return unique.Values
                             .OrderByDescending(r => r.RelevanceScore)
                             .ToList();
            });
        }

        private IEnumerable<SearchResult> ParseTextOutput(string searchTerm, string source)
        {
            var results = new List<SearchResult>();

            string command;
            if (source == "msstore")
            {
                command = $"search \"{searchTerm}\" --source {source}" +
                          (SupportsAgreements ? " --accept-source-agreements" : "");
            }
            else
            {
                command = $"search --query \"{searchTerm}\" --source {source}" +
                          (SupportsAgreements ? " --accept-source-agreements" : "");
            }

            WriteLog($"Text parsing command: {WingetPath} {command}");

            var psi = new ProcessStartInfo(WingetPath, command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            string textOut = proc.StandardOutput.ReadToEnd();
            string errorOut = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(errorOut))
            {
                WriteLog($"Text parse error output: {errorOut}");
            }

            var lines = textOut
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            WriteLog($"Text output has {lines.Count} lines");

            // Log first few lines for debugging
            for (int i = 0; i < Math.Min(5, lines.Count); i++)
            {
                WriteLog($"Line {i}: {lines[i]}");
            }

            if (lines.Count <= 2)
                return results;

            bool inData = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (!inData && Regex.IsMatch(line, @"^[\s-]+$"))
                {
                    inData = true;
                    continue;
                }

                if (!inData || (line.StartsWith("Name") && line.Contains("Id") && line.Contains("Version")))
                    continue;

                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;

                // Fixed version detection - look from the END of the array
                int versionIndex = -1;
                for (int i = parts.Length - 1; i >= 2; i--)  // Start from last index, stop at 2
                {
                    if (Regex.IsMatch(parts[i], @"^[\d\.]+$") ||
                        parts[i].Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        versionIndex = i;
                        break;
                    }
                }

                if (versionIndex < 2)  // Need at least 2 positions before version for name and ID
                    continue;

                // Now assemble Name, Id, Version
                string version = parts[versionIndex];
                string id = parts[versionIndex - 1];
                string name = string.Join(' ', parts[0..(versionIndex - 1)]);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                    continue;

                WriteLog($"Parsed line: Name='{name}', Id='{id}', Version='{version}'");

                if (!TestEnglishOnly(name))
                {
                    WriteLog($"Skipped non-English in text parse: {name}");
                    continue;
                }

                var sr = new SearchResult
                {
                    Name = name,
                    Id = id,
                    Source = source,
                    Version = version,
                    RelevanceScore = ComputeRelevanceScore(searchTerm, name, id)
                };
                results.Add(sr);
                WriteLog($"Added text result: {name} from {source}");
            }

            WriteLog($"Text parsing returning {results.Count} results");
            return results;
        }


        private static bool TestEnglishOnly(string text)
        {
            return Regex.IsMatch(
                text,
                @"^[a-zA-Z0-9\s\.\-_\(\)\[\]&@#\$%\^\*\+=/\\:;,""'\!\?<>\|~\{\}]+$"
            );
        }


        private static int ComputeRelevanceScore(string searchTerm, string appName, string appId)
        {
            int score = 0;
            string sl = searchTerm.ToLower();
            string nl = appName.ToLower();
            string il = appId.ToLower();

            if (nl == sl || il == sl) { score += 200; }
            if (nl.StartsWith(sl)) { score += 50; }
            if (il.StartsWith(sl)) { score += 45; }
            if (nl.Contains(sl)) { score += 30; }
            if (il.Contains(sl)) { score += 25; }

            string searchClean = Regex.Replace(sl, @"[\.\-_]", "");
            string nameClean = Regex.Replace(nl, @"[\.\-_]", "");
            string idClean = Regex.Replace(il, @"[\.\-_]", "");
            if (nameClean.Contains(searchClean) || searchClean.Contains(nameClean)) { score += 20; }
            if (idClean.Contains(searchClean) || searchClean.Contains(idClean)) { score += 15; }
            if (appName.Length <= 20) { score += 5; }

            return score;
        }

        private void UpdateResultsGrid()
        {
            if (SearchResults.Count == 0)
            {
                ResultsGrid.ItemsSource = null;
                PageInfoText.Text = "";
                return;
            }

            int total = SearchResults.Count;
            int totalPages = (int)Math.Ceiling(total / (double)PageSize);
            if (CurrentPage < 1) CurrentPage = 1;
            if (CurrentPage > totalPages) CurrentPage = totalPages;

            int start = (CurrentPage - 1) * PageSize;
            ResultsGrid.ItemsSource = SearchResults.Skip(start).Take(PageSize).ToList();
            PageInfoText.Text = $"Page {CurrentPage} of {totalPages} ({total} results)";
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                UpdateResultsGrid();
            }
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            int total = SearchResults.Count;
            int totalPages = (int)Math.Ceiling(total / (double)PageSize);
            if (CurrentPage < totalPages)
            {
                CurrentPage++;
                UpdateResultsGrid();
            }
        }

        #endregion

        #region Add / Remove Logic

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is SearchResult selected &&
                !string.IsNullOrWhiteSpace(selected.Id))
            {
                // If that ID is not already in our favorites
                if (!PredefinedApps.Any(a => a.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    PredefinedApps.Add(new AppItem
                    {
                        Name = selected.Name,
                        Id = selected.Id,
                        Source = selected.Source,
                        IsChecked = false
                    });
                    AppListBox.Items.Refresh();
                    StatusText.Text = $"Added {selected.Name}";
                }
                else
                {
                    StatusText.Text = $"{selected.Name} already in list";
                }
            }
            else
            {
                StatusText.Text = "Please select a valid app.";
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var toRemove = PredefinedApps.Where(a => a.IsChecked).ToList();
            if (toRemove.Count == 0)
            {
                StatusText.Text = "No apps checked.";
                return;
            }
            foreach (var item in toRemove)
            {
                PredefinedApps.Remove(item);
            }
            AppListBox.Items.Refresh();
            StatusText.Text = $"Removed {toRemove.Count} item(s)";
        }

        #endregion

        #region Install Logic

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            var checkedItems = PredefinedApps.Where(a => a.IsChecked).ToList();
            if (checkedItems.Count == 0)
            {
                StatusText.Text = "No apps checked.";
                return;
            }

            InstallButton.IsEnabled = false;
            StatusText.Text = $"Preparing to install {checkedItems.Count} app(s)…";

            await Task.Run(() =>
            {
                foreach (var entry in checkedItems)
                {
                    string id = entry.Id;
                    string src = entry.Source;
                    string name = entry.Name;

                    // (A) Update UI before each install
                    Dispatcher.Invoke(() => { StatusText.Text = $"Installing {name}…"; });
                    WriteLog($"Installing {name} ({id})");

                    // (B) Build install arguments
                    var args = $"install --id {id} --exact --silent";
                    if (SupportsAgreements)
                        args += " --accept-package-agreements --accept-source-agreements";
                    if (!src.Equals("winget", StringComparison.OrdinalIgnoreCase))
                        args += $" --source {src}";

                    try
                    {
                        var psi = new ProcessStartInfo(WingetPath, args)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var proc = Process.Start(psi);
                        proc.WaitForExit();
                        int exitCode = proc.ExitCode;

                        string status = exitCode == 0
                                        ? "Success"
                                        : $"Failed (Exit code: {exitCode})";
                        WriteLog($"{name}: {status}");
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"ERROR installing {name} ({id}): {ex}");
                    }
                }

                // (C) All done
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Installation complete";
                    InstallButton.IsEnabled = true;
                });
            });
        }

        #endregion

        #region Export / Import Logic

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
                FileName = "app_list.json"
            };
            if (dlg.ShowDialog() == true)
            {
                var listToExport = PredefinedApps.Select(a => new
                {
                    Name = a.Name,
                    Id = a.Id,
                    Source = a.Source
                }).ToList();

                var jsonText = JsonSerializer.Serialize(
                    listToExport, new JsonSerializerOptions { WriteIndented = true }
                );
                System.IO.File.WriteAllText(dlg.FileName, jsonText);
                StatusText.Text = $"Exported {listToExport.Count} apps";
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string raw = System.IO.File.ReadAllText(dlg.FileName);
                    var imported = JsonSerializer.Deserialize<List<AppItem>>(raw);
                    if (imported != null)
                    {
                        PredefinedApps.Clear();
                        foreach (var item in imported)
                        {
                            item.IsChecked = false;
                            PredefinedApps.Add(item);
                        }
                        AppListBox.Items.Refresh();
                        StatusText.Text = $"Imported {imported.Count} apps";
                    }
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Import failed: {ex.Message}";
                }
            }
        }

        #endregion

        #region Logging

        private void WriteLog(string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                System.IO.File.AppendAllText(LogFile, $"{timestamp}\t{message}{Environment.NewLine}");
            }
            catch
            {
                // ignore any logging errors
            }
        }

        #endregion
    }
}
