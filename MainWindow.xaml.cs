using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using CefSharp;
using CefSharp.DevTools.Emulation;
using CefSharp.Wpf;

namespace BlatmetroBrowser;

public partial class MainWindow : Window
{
    private const string DefaultSearchEngine = "DuckDuckGo";
    private static readonly Dictionary<string, string> SearchEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DuckDuckGo"] = "https://duckduckgo.com/?q=",
        ["Google"] = "https://www.google.com/search?q=",
        ["Microsoft Bing"] = "https://www.bing.com/search?q=",
        ["Brave Search"] = "https://search.brave.com/search?q=",
        ["Яндекс"] = "https://yandex.ru/search/?text=",
        ["Ecosia"] = "https://www.ecosia.org/search?q=",
        ["Qwant"] = "https://www.qwant.com/?q=",
        ["Startpage"] = "https://www.startpage.com/sp/search?query=",
        ["Yahoo"] = "https://search.yahoo.com/search?p=",
        ["PrivacyWall"] = "https://www.privacywall.org/search/secure/?q=",
        ["Perplexity"] = "https://www.perplexity.ai/search?q=",
        ["@MAIL.RU"] = "https://go.mail.ru/search?q="
    };
    private readonly List<BrowserTab> _tabs = [];
    private readonly string _appDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Blatmetro",
        "Browser");
    private readonly string _bookmarksFile;
    private readonly string _settingsFile;
    private readonly string _downloadsFile;
    private readonly List<SearchEngineOption> _searchEngineOptions;
    private string _selectedSearchEngine = DefaultSearchEngine;
    private string _selectedTheme = "System";
    private bool _loadingSettings;
    private List<Bookmark> _bookmarks = [];
    private BrowserTab? _activeTab;
    private readonly Dictionary<int, DownloadEntry> _downloadsByCefId = [];
    private readonly List<DownloadEntry> _downloadEntries = [];
    private readonly DispatcherTimer _downloadRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private GridLength[]? _previousRowHeights;
    private bool _isFullscreen;
    private bool _downloadStorageWarningShown;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;

    public MainWindow()
    {
        InitializeComponent();
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "BlatmetroBrowser.png");
        if (File.Exists(logoPath))
        {
            var logo = new BitmapImage();
            logo.BeginInit();
            logo.UriSource = new Uri(logoPath, UriKind.Absolute);
            logo.CacheOption = BitmapCacheOption.OnLoad;
            logo.EndInit();
            logo.Freeze();
            Icon = logo;
        }
        Directory.CreateDirectory(_appDataDirectory);
        _bookmarksFile = Path.Combine(_appDataDirectory, "bookmarks.json");
        _settingsFile = Path.Combine(_appDataDirectory, "settings.json");
        _downloadsFile = Path.Combine(_appDataDirectory, "downloads.json");
        LoadBookmarks();
        LoadSettings();
        LoadDownloadHistory();
        _downloadRefreshTimer.Tick += (_, _) =>
        {
            _downloadRefreshTimer.Stop();
            RenderDownloads();
        };
        _searchEngineOptions = CreateSearchEngineOptions();
        _loadingSettings = true;
        SearchEngineBox.ItemsSource = _searchEngineOptions;
        SearchEngineBox.SelectedValue = _selectedSearchEngine;
        ThemeModeBox.SelectedValue = _selectedTheme;
        _loadingSettings = false;

        // Follow Windows app-theme changes only while the user has selected System.
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyTheme();
        RefreshBookmarksBar();
        AddTab();
    }

    private static readonly (string Name, string IconFile)[] SearchEngineCatalog =
    [
        ("DuckDuckGo", "duckduckgo.png"),
        ("Google", "google.png"),
        ("Microsoft Bing", "bing.png"),
        ("Brave Search", "brave.png"),
        ("Яндекс", "yandex.png"),
        ("Ecosia", "ecosia.png"),
        ("Qwant", "qwant.png"),
        ("Startpage", "startpage.png"),
        ("Yahoo", "yahoo.png"),
        ("PrivacyWall", "privacywall.png"),
        ("Perplexity", "perplexity.png"),
        ("@MAIL.RU", "mailru.png")
    ];

    private static List<SearchEngineOption> CreateSearchEngineOptions()
    {
        var options = new List<SearchEngineOption>(SearchEngineCatalog.Length);
        foreach (var (name, iconFile) in SearchEngineCatalog)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SearchEngines", iconFile);
            if (!File.Exists(iconPath))
            {
                throw new FileNotFoundException($"Не найден логотип поисковика: {iconFile}", iconPath);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48;
            bitmap.EndInit();
            bitmap.Freeze();
            options.Add(new SearchEngineOption(name, bitmap));
        }

        return options;
    }

    private static bool IsValidThemeMode(string? theme) =>
        theme is not null &&
        (theme.Equals("System", StringComparison.OrdinalIgnoreCase) ||
         theme.Equals("Light", StringComparison.OrdinalIgnoreCase) ||
         theme.Equals("Dark", StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowsAppThemeLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int lightTheme ? lightTheme != 0 : true;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private string GetEffectiveTheme()
    {
        if (_selectedTheme.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return IsWindowsAppThemeLight() ? "Light" : "Dark";
        }

        return _selectedTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
    }

    private void ApplyTheme()
    {
        var effectiveTheme = GetEffectiveTheme();
        var isDark = effectiveTheme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        var palette = isDark
            ? new Dictionary<string, string>
            {
                ["WindowBackground"] = "#10131B", ["Surface"] = "#191E2A",
                ["SurfaceHover"] = "#252D3D", ["SurfaceActive"] = "#303B50",
                ["Accent"] = "#8B7CFF", ["TextPrimary"] = "#F3F4FA", ["TextSecondary"] = "#A6AEC0",
                ["TabStripBackground"] = "#0C0F16", ["TabActiveBackground"] = "#303B50",
                ["TabInactiveBackground"] = "#191E2A", ["BookmarkBarBackground"] = "#141925",
                ["BookmarkBorder"] = "#252D3D", ["AddressBackground"] = "#202737",
                ["AddressBorder"] = "#39445A", ["SearchControlBackground"] = "#202737",
                ["SearchControlBorder"] = "#39445A", ["SearchPopupBackground"] = "#171D2A",
                ["SearchPopupBorder"] = "#3A4560", ["SearchItemHoverBackground"] = "#252D3D",
                ["SearchItemSelectedBackground"] = "#332D5E", ["SearchIconTileBackground"] = "#FFFFFF",
                ["SearchFocusBorder"] = "#8B7CFF"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackground"] = "#F5F7FB", ["Surface"] = "#FFFFFF",
                ["SurfaceHover"] = "#EEF1F7", ["SurfaceActive"] = "#E4E9F3",
                ["Accent"] = "#6757E8", ["TextPrimary"] = "#1B2333", ["TextSecondary"] = "#657087",
                ["TabStripBackground"] = "#E9EDF5", ["TabActiveBackground"] = "#FFFFFF",
                ["TabInactiveBackground"] = "#E6EAF2", ["BookmarkBarBackground"] = "#F7F8FC",
                ["BookmarkBorder"] = "#DCE2ED", ["AddressBackground"] = "#FFFFFF",
                ["AddressBorder"] = "#D5DCE8", ["SearchControlBackground"] = "#FFFFFF",
                ["SearchControlBorder"] = "#D4DCE8", ["SearchPopupBackground"] = "#FFFFFF",
                ["SearchPopupBorder"] = "#D9DFEA", ["SearchItemHoverBackground"] = "#F0F2F8",
                ["SearchItemSelectedBackground"] = "#ECE9FF", ["SearchIconTileBackground"] = "#F4F6FA",
                ["SearchFocusBorder"] = "#7462F4"
            };

        foreach (var (key, hex) in palette)
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
            {
                Application.Current.Resources[key] = new SolidColorBrush(color);
            }
        }

        ThemeModeBox.ToolTip = _selectedTheme switch
        {
            "System" => $"Тема: Системная ({(isDark ? "тёмная" : "светлая")})",
            "Light" => "Тема: Светлая",
            _ => "Тема: Тёмная"
        };

        foreach (var tab in _tabs)
        {
            tab.Header.SetResourceReference(Border.BackgroundProperty,
                tab == _activeTab ? "TabActiveBackground" : "TabInactiveBackground");

            // Tell Chromium which color scheme the user selected. Pages that support
            // prefers-color-scheme can update their own CSS without being force-recolored.
            _ = ApplyWebsiteColorSchemeAsync(tab.Browser, effectiveTheme);

            // The bundled start page has its own theme variables, so update it directly too.
            if (IsNewTabAddress(tab.Browser.Address))
            {
                try
                {
                    tab.Browser.ExecuteScriptAsync(
                        $"window.setBlatmetroTheme && window.setBlatmetroTheme('{effectiveTheme.ToLowerInvariant()}');");
                }
                catch (InvalidOperationException)
                {
                    // The initial URL also carries the selected theme, so first render is correct.
                }
            }
        }
    }

    private async Task ApplyWebsiteColorSchemeAsync(ChromiumWebBrowser browser, string? effectiveTheme = null)
    {
        if (!browser.IsBrowserInitialized)
        {
            return;
        }

        var colorScheme = (effectiveTheme ?? GetEffectiveTheme()).ToLowerInvariant();

        try
        {
            // CDP Emulation.setEmulatedMedia changes the live CSS media-query value.
            // This raises matchMedia('(prefers-color-scheme: ...)') changes in pages
            // that implement automatic light/dark appearance.
            var devTools = browser.GetDevToolsClient();
            await devTools.Emulation.SetEmulatedMediaAsync(
                media: string.Empty,
                features: new List<MediaFeature>
                {
                    new()
                    {
                        Name = "prefers-color-scheme",
                        Value = colorScheme
                    }
                });
        }
        catch (Exception ex)
        {
            // A page remains usable even if this CEF build cannot apply the emulation.
            System.Diagnostics.Debug.WriteLine($"Unable to apply website color scheme: {ex.Message}");
        }
    }

    private void ThemeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeModeBox.SelectedValue is not string theme || !IsValidThemeMode(theme))
        {
            return;
        }

        _selectedTheme = theme;
        ApplyTheme();
        SaveSettings();
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!_selectedTheme.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_selectedTheme.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTheme();
            }
        });
    }

    private void AddTab(string? initialAddress = null)
    {
        var browser = new ChromiumWebBrowser
        {
            BrowserSettings =
            {
                DefaultEncoding = "UTF-8"
            }
        };
        browser.DownloadHandler = new BrowserDownloadHandler(this);
        browser.LifeSpanHandler = new PopupToTabHandler(this);

        // Apply the selected website color scheme as soon as this tab's CEF target exists.
        browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (browser.IsBrowserInitialized)
            {
                _ = ApplyWebsiteColorSchemeAsync(browser);
            }
        };

        // Receive search requests from the local new-tab page through CefSharp's
        // built-in PostMessage bridge. This avoids async JS-object binding races.
        browser.JavascriptMessageReceived += (_, args) =>
        {
            var frame = args.Frame;
            if (frame is null || !frame.IsMain ||
                !TryGetTrustedPage(frame.Url, out var source) ||
                args.Message is not string message ||
                string.IsNullOrWhiteSpace(message) || message.Length > 2048)
            {
                return;
            }

            var sourceUrl = frame.Url;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    // Navigation may have changed while the message was queued.
                    if (!_tabs.Any(tab => ReferenceEquals(tab.Browser, browser)) ||
                        !IsTrustedPage(browser.Address, source) || !IsSameLocalPage(sourceUrl, source)) return;
                    HandlePageMessage(message, source);
                });
            }
            catch (InvalidOperationException) { }
        };

        var title = new TextBlock
        {
            Text = "Новая вкладка",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150
        };

        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var closeButton = new Button
        {
            Content = "×",

            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 0, 5, 1),
            FontSize = 16,
            Cursor = Cursors.Hand,
            ToolTip = "Закрыть вкладку"
        };
        closeButton.SetResourceReference(Control.ForegroundProperty, "TextSecondary");

        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerContent.Children.Add(title);
        headerContent.Children.Add(closeButton);

        var header = new Border
        {
            Child = headerContent,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Padding = new Thickness(12, 7, 6, 7),
            Margin = new Thickness(0, 0, 5, 0),
            MinWidth = 130,
            Cursor = Cursors.Hand
        };
        header.SetResourceReference(Border.BackgroundProperty, "TabInactiveBackground");

        var tab = new BrowserTab(browser, header, title);
        _tabs.Add(tab);
        TabsStrip.Children.Add(header);
        BrowserHost.Children.Add(browser);

        header.MouseLeftButtonUp += (_, _) => SelectTab(tab);
        closeButton.Click += (_, _) => CloseTab(tab);

        browser.AddressChanged += (_, _) =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_activeTab == tab)
                    {
                        var currentAddress = browser.Address;
                        AddressBox.Text = IsNewTabAddress(currentAddress) ? string.Empty : currentAddress;
                        UpdateSecurityIndicator(currentAddress);
                        UpdateBookmarkButton();
                    }
                });
            }
            catch (InvalidOperationException) { }
        };

        browser.TitleChanged += (_, _) =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    var currentTitle = browser.Title;
                    var pageTitle = string.IsNullOrWhiteSpace(currentTitle) ? "Новая вкладка" : currentTitle;
                    tab.Title.Text = pageTitle.Length > 24 ? pageTitle[..24] + "…" : pageTitle;
                });
            }
            catch (InvalidOperationException) { }
        };

        browser.LoadingStateChanged += (_, _) =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    UpdateNavigationButtons();
                    var currentTab = _tabs.FirstOrDefault(item => ReferenceEquals(item.Browser, browser));
                    if (currentTab?.IsDownloadsTab == true && !browser.IsLoading)
                    {
                        RenderDownloads(currentTab);
                    }
                });
            }
            catch (InvalidOperationException) { }
        };

        SelectTab(tab);
        browser.Address = string.IsNullOrWhiteSpace(initialAddress)
            ? GetNewTabAddress()
            : initialAddress;
    }

    private enum TrustedLocalPage
    {
        NewTab,
        Downloads
    }

    private static bool TryGetTrustedPage(string? address, out TrustedLocalPage page)
    {
        if (IsSameLocalPage(address, TrustedLocalPage.NewTab))
        {
            page = TrustedLocalPage.NewTab;
            return true;
        }
        if (IsSameLocalPage(address, TrustedLocalPage.Downloads))
        {
            page = TrustedLocalPage.Downloads;
            return true;
        }
        page = default;
        return false;
    }

    private static bool IsTrustedPage(string? address, TrustedLocalPage page) =>
        IsSameLocalPage(address, page);

    private static bool IsSameLocalPage(string? address, TrustedLocalPage page)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        var fileName = page == TrustedLocalPage.NewTab ? "NewTab.html" : "Downloads.html";
        var trustedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", fileName));
        try
        {
            return string.Equals(Path.GetFullPath(uri.LocalPath), trustedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetDownloadPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(GetDownloadsDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetDownloadsDirectory()
    {
        var downloadsFolderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref downloadsFolderId, 0, IntPtr.Zero, out pathPointer) == 0)
            {
                var path = Marshal.PtrToStringUni(pathPointer);
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Debug.WriteLine($"Unable to resolve the Windows Downloads known folder: {ex.Message}");
        }
        finally
        {
            if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static string SanitizeDownloadName(string? suggestedName, int downloadId)
    {
        var name = Path.GetFileName(suggestedName ?? string.Empty);
        var invalidChars = Path.GetInvalidFileNameChars();
        name = new string(name.Where(character => !invalidChars.Contains(character)).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? $"download-{downloadId}" : name;
    }

    private static string GetUniqueDownloadPath(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 1; suffix < 100_000; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать свободное имя для файла загрузки.");
    }

    private void HandlePageMessage(string message, TrustedLocalPage source)
    {
        if (source == TrustedLocalPage.NewTab)
        {
            Navigate(message.Trim());
            return;
        }
        if (source != TrustedLocalPage.Downloads) return;

        var parts = message.Split(':');
        if (parts.Length != 3 || parts[0] != "download" ||
            !Guid.TryParseExact(parts[2], "D", out var key)) return;

        var download = _downloadEntries.FirstOrDefault(item => item.Key == key);
        if (download is null) return;
        if (parts[1] == "cancel")
        {
            if (download.InProgress) download.Callback?.Cancel();
            return;
        }
        if (parts[1] == "open" && download.Complete)
        {
            if (TryGetDownloadPath(download.Path, out var safePath) && File.Exists(safePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(safePath) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    MessageBox.Show($"Не удалось открыть файл: {ex.Message}", "Blatmetro Browser",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private bool IsNewTabAddress(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var currentUri) || !currentUri.IsFile)
        {
            return false;
        }

        var newTabPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "NewTab.html"));
        return string.Equals(Path.GetFullPath(currentUri.LocalPath), newTabPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private string GetNewTabAddress()
    {
        var newTabPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NewTab.html");
        return File.Exists(newTabPath)
            ? $"{new Uri(newTabPath).AbsoluteUri}?theme={GetEffectiveTheme().ToLowerInvariant()}"
            : "https://duckduckgo.com/";
    }

    private void SelectTab(BrowserTab tab)
    {
        _activeTab = tab;

        foreach (var item in _tabs)
        {
            var isSelected = item == tab;
            item.Browser.Visibility = isSelected ? Visibility.Visible : Visibility.Hidden;
            item.Header.SetResourceReference(Border.BackgroundProperty,
                isSelected ? "TabActiveBackground" : "TabInactiveBackground");
        }

        AddressBox.Text = IsNewTabAddress(tab.Browser.Address) ? string.Empty : tab.Browser.Address ?? string.Empty;
        UpdateSecurityIndicator(tab.Browser.Address);
        UpdateNavigationButtons();
        UpdateBookmarkButton();
        UpdateZoomLabel();
    }

    private void UpdateSecurityIndicator(string? address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            SecurityIndicator.Text = "🔒";
            SecurityIndicator.ToolTip = $"Защищённое HTTPS-соединение с {uri.Host}";
            SecurityIndicator.Foreground = Brushes.SeaGreen;
        }
        else if (Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme is "http")
        {
            SecurityIndicator.Text = "⚠";
            SecurityIndicator.ToolTip = $"Незащищённое HTTP-соединение с {uri.Host}";
            SecurityIndicator.Foreground = Brushes.DarkOrange;
        }
        else
        {
            SecurityIndicator.Text = "⌕";
            SecurityIndicator.ToolTip = "Введите адрес сайта";
            SecurityIndicator.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.Remove(tab);
        TabsStrip.Children.Remove(tab.Header);
        BrowserHost.Children.Remove(tab.Browser);
        tab.Browser.Dispose();

        if (_tabs.Count == 0)
        {
            _activeTab = null;
            AddTab();
            return;
        }

        if (_activeTab == tab)
        {
            SelectTab(_tabs[Math.Clamp(index - 1, 0, _tabs.Count - 1)]);
        }
    }

    private void Navigate(string input)
    {
        if (_activeTab is null)
        {
            return;
        }

        var value = input.Trim();
        if (value.Length == 0)
        {
            return;
        }

        string target;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri) &&
            absoluteUri.Scheme is "http" or "https" or "file" or "about")
        {
            target = absoluteUri.AbsoluteUri;
        }
        else if (!value.Contains(' ') &&
                 (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                  value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
                  value.Contains('.')))
        {
            target = "https://" + value;
        }
        else
        {
            // Read the current ComboBox selection at the moment of searching.
            // This prevents a stale saved value from overriding the visible choice.
            if (SearchEngineBox.SelectedItem is SearchEngineOption selectedEngine &&
                SearchEngines.ContainsKey(selectedEngine.Name))
            {
                _selectedSearchEngine = selectedEngine.Name;
                SaveSettings();
            }

            var searchUrl = SearchEngines.TryGetValue(_selectedSearchEngine, out var configuredUrl)
                ? configuredUrl
                : SearchEngines[DefaultSearchEngine];
            target = searchUrl + Uri.EscapeDataString(value);
        }

        _activeTab.Browser.Address = target;
        AddressBox.Text = target;
    }

    private void UpdateNavigationButtons()
    {
        if (_activeTab is null)
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            return;
        }

        BackButton.IsEnabled = _activeTab.Browser.CanGoBack;
        ForwardButton.IsEnabled = _activeTab.Browser.CanGoForward;
    }

    private void UpdateBookmarkButton()
    {
        if (_activeTab is null || !Uri.TryCreate(_activeTab.Browser.Address, UriKind.Absolute, out var uri))
        {
            BookmarkButton.Content = "☆";
            return;
        }

        var exists = _bookmarks.Any(item => string.Equals(item.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
        BookmarkButton.Content = exists ? "★" : "☆";
    }


    private void SearchEngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SearchEngineBox.SelectedItem is not SearchEngineOption selected ||
            !SearchEngines.ContainsKey(selected.Name))
        {
            return;
        }

        _selectedSearchEngine = selected.Name;
        SaveSettings();
    }

    private void SearchEngineBox_DropDownClosed(object sender, EventArgs e)
    {
        if (!_loadingSettings && SearchEngineBox.SelectedItem is SearchEngineOption selected &&
            SearchEngines.ContainsKey(selected.Name))
        {
            _selectedSearchEngine = selected.Name;
            SaveSettings();
        }
    }

    private void LoadSettings()
    {
        var settings = ReadJson<BrowserSettings>(_settingsFile);
        if (settings?.SearchEngine is string engine && SearchEngines.ContainsKey(engine))
            _selectedSearchEngine = engine;
        if (settings?.Theme is string theme && IsValidThemeMode(theme))
            _selectedTheme = theme;
    }

    private void SaveSettings()
    {
        try
        {
            WriteJsonAtomically(_settingsFile, new BrowserSettings(_selectedSearchEngine, _selectedTheme));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            MessageBox.Show($"Не удалось сохранить настройки: {ex.Message}", "Blatmetro Browser",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadBookmarks()
    {
        _bookmarks = (ReadJson<List<Bookmark>>(_bookmarksFile) ?? [])
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Url) &&
                           !string.IsNullOrWhiteSpace(item.Title))
            .ToList();
    }

    private void SaveBookmarks()
    {
        try
        {
            WriteJsonAtomically(_bookmarksFile, _bookmarks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            MessageBox.Show($"Не удалось сохранить закладки: {ex.Message}", "Blatmetro Browser",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshBookmarksBar()
    {
        BookmarksPanel.Children.Clear();

        if (_bookmarks.Count == 0)
        {
            BookmarksPanel.Children.Add(new TextBlock
            {
                Text = "Закладки появятся здесь",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });
            ((TextBlock)BookmarksPanel.Children[0]).SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return;
        }

        foreach (var bookmark in _bookmarks)
        {
            var button = new Button
            {
                Content = bookmark.Title,
                Tag = bookmark.Url,
                Style = (Style)FindResource("ToolbarButton"),
                FontSize = 11,
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 5, 0),
                MaxWidth = 180
            };
            button.Click += (_, _) =>
            {
                if (button.Tag is string url && _activeTab is not null)
                {
                    _activeTab.Browser.Address = url;
                }
            };
            BookmarksPanel.Children.Add(button);
        }
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        CloseMainMenu();
        if (_activeTab is null || !Uri.TryCreate(_activeTab.Browser.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        var existing = _bookmarks.FirstOrDefault(item =>
            string.Equals(item.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _bookmarks.Remove(existing);
        }
        else
        {
            var title = string.IsNullOrWhiteSpace(_activeTab.Title.Text) || _activeTab.Title.Text == "Новая вкладка"
                ? uri.Host
                : _activeTab.Title.Text;
            _bookmarks.Add(new Bookmark(title, uri.AbsoluteUri));
        }

        SaveBookmarks();
        RefreshBookmarksBar();
        UpdateBookmarkButton();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _activeTab?.Browser.Back();
    private void Forward_Click(object sender, RoutedEventArgs e) => _activeTab?.Browser.Forward();
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        CloseMainMenu();
        _activeTab?.Browser.Reload();
    }
    private void Home_Click(object sender, RoutedEventArgs e)
    {
        CloseMainMenu();
        if (_activeTab is not null)
        {
            _activeTab.Browser.Address = GetNewTabAddress();
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        CloseMainMenu();
        AddTab();
    }

    private void MainMenuButton_Click(object sender, RoutedEventArgs e)
    {
        MainMenuPopup.IsOpen = !MainMenuPopup.IsOpen;
        if (MainMenuPopup.IsOpen)
        {
            UpdateZoomLabel();
        }
    }

    private void LoadDownloadHistory()
    {
        var records = ReadJson<List<DownloadRecord>>(_downloadsFile) ?? [];
        var recoveredInterrupted = false;
        foreach (var record in records)
        {
            if (record is null || record.Key == Guid.Empty) continue;
            var entry = new DownloadEntry(record);
            if (entry.InProgress)
            {
                entry.InProgress = false;
                entry.Callback = null;
                entry.Status = "Прервано при закрытии браузера";
                recoveredInterrupted = true;
            }
            if (entry.Complete && !File.Exists(entry.Path))
            {
                entry.Complete = false;
                entry.Status = "Файл отсутствует";
            }
            _downloadEntries.Add(entry);
        }
        if (recoveredInterrupted) SaveDownloadHistory();
    }

    private void SaveDownloadHistory()
    {
        try
        {
            var records = _downloadEntries.Select(item => new DownloadRecord(
                item.Key, item.Name, item.Path, item.Status, item.Received, item.Total,
                item.Progress, item.Complete, item.InProgress, item.CreatedAt, item.UpdatedAt)).ToList();
            WriteJsonAtomically(_downloadsFile, records);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine($"Unable to save download history: {ex.Message}");
            if (!_downloadStorageWarningShown)
            {
                _downloadStorageWarningShown = true;
                MessageBox.Show($"Не удалось сохранить историю загрузок: {ex.Message}", "Blatmetro Browser",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var value = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate));
                    if (value is not null) return value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
            {
                Debug.WriteLine($"Unable to read {candidate}: {ex.Message}");
            }
        }
        return null;
    }

    private static bool IsValidJsonFile<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return false;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value) where T : class
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = path + ".bak";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                if (IsValidJsonFile<T>(path)) File.Copy(path, backupPath, overwrite: true);
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                Debug.WriteLine($"Unable to remove temporary JSON file {tempPath}: {ex.Message}");
            }
        }
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        CloseMainMenu();
        var tab = _tabs.FirstOrDefault(t => t.IsDownloadsTab);
        if (tab is null)
        {
            AddTab("about:blank");
            tab = _activeTab!;
            tab.IsDownloadsTab = true;
            tab.Title.Text = "Загрузки";
            var downloadsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Downloads.html");
            tab.Browser.Address = File.Exists(downloadsPath) ? new Uri(downloadsPath).AbsoluteUri : "about:blank";
        }
        SelectTab(tab);
        RenderDownloads(tab);
    }

    private void RenderDownloads(BrowserTab? onlyTab = null)
    {
        foreach (var tab in onlyTab is null ? _tabs.Where(t => t.IsDownloadsTab) : [onlyTab])
        {
            if (!tab.Browser.IsBrowserInitialized || tab.Browser.IsLoading) continue;
            var json = JsonSerializer.Serialize(_downloadEntries.OrderByDescending(d => d.CreatedAt).Select(d => new { id=d.Key, name=d.Name, size=$"{FormatBytes(d.Received)} / {FormatBytes(d.Total)}", status=d.Status, progress=d.Progress, complete=d.Complete && File.Exists(d.Path), active=d.InProgress }));
            tab.Browser.ExecuteScriptAsync($"render({json});");
        }
    }

    private static string FormatBytes(long bytes) => bytes < 0 ? "Размер неизвестен" : bytes < 1024 ? $"{bytes} Б" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} КБ" : $"{bytes / (1024d * 1024):0.#} МБ";

    private void UpdateDownload(DownloadItem downloadItem, IDownloadItemCallback callback)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                var isNew = false;
                if (!_downloadsByCefId.TryGetValue(downloadItem.Id, out var item))
                {
                    item = new DownloadEntry(downloadItem.Id);
                    _downloadsByCefId[downloadItem.Id] = item;
                    _downloadEntries.Add(item);
                    isNew = true;
                }
                item.Name = Path.GetFileName(downloadItem.SuggestedFileName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(item.Name)) item.Name = "Загрузка";
                item.Path = downloadItem.FullPath ?? string.Empty;
                item.Received = downloadItem.ReceivedBytes;
                item.Total = downloadItem.TotalBytes;
                item.Progress = downloadItem.PercentComplete < 0 && downloadItem.TotalBytes > 0
                    ? (int)Math.Clamp(downloadItem.ReceivedBytes * 100d / downloadItem.TotalBytes, 0, 100)
                    : Math.Clamp(downloadItem.PercentComplete, 0, 100);
                item.Complete = downloadItem.IsComplete;
                item.InProgress = downloadItem.IsInProgress;
                item.Callback = callback;
                item.Status = downloadItem.IsComplete ? "Завершено" : downloadItem.IsCancelled
                    ? "Отменено или прервано" : downloadItem.IsInProgress ? "Загрузка" : "Ошибка загрузки";
                item.UpdatedAt = DateTimeOffset.Now;
                if (isNew || !item.InProgress) SaveDownloadHistory();
                ScheduleDownloadRefresh(item.InProgress);
            });
        }
        catch (InvalidOperationException)
        {
            // The window may be closing while CEF reports a final download update.
        }
    }

    private void ScheduleDownloadRefresh(bool isProgressUpdate)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!isProgressUpdate)
        {
            _downloadRefreshTimer.Stop();
            RenderDownloads();
        }
        else if (!_downloadRefreshTimer.IsEnabled)
        {
            _downloadRefreshTimer.Start();
        }
    }

    private sealed class DownloadEntry
    {
        public DownloadEntry(int cefId)
        {
            CefId = cefId;
            Key = Guid.NewGuid();
        }

        public DownloadEntry(DownloadRecord record)
        {
            Key = record.Key;
            CefId = -1;
            Name = string.IsNullOrWhiteSpace(record.Name) ? "Загрузка" : record.Name;
            Path = record.Path ?? string.Empty;
            Status = string.IsNullOrWhiteSpace(record.Status) ? "Неизвестный статус" : record.Status;
            Received = Math.Max(0, record.Received);
            Total = record.Total;
            Progress = Math.Clamp(record.Progress, 0, 100);
            Complete = record.Complete;
            InProgress = record.InProgress;
            CreatedAt = record.CreatedAt == default ? record.UpdatedAt : record.CreatedAt;
            UpdatedAt = record.UpdatedAt;
        }

        public Guid Key { get; }
        public int CefId { get; }
        public string Name { get; set; } = "Загрузка";
        public string Path { get; set; } = "";
        public string Status { get; set; } = "Подготовка";
        public long Received { get; set; }
        public long Total { get; set; }
        public int Progress { get; set; }
        public bool Complete { get; set; }
        public bool InProgress { get; set; }
        public IDownloadItemCallback? Callback { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    }

    private sealed record DownloadRecord(Guid Key, string Name, string Path, string Status,
        long Received, long Total, int Progress, bool Complete, bool InProgress,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed class BrowserDownloadHandler(MainWindow owner) : CefSharp.Handler.DownloadHandler
    {
        protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IBeforeDownloadCallback callback)
        {
            if (!callback.IsDisposed)
            {
                try
                {
                    var directory = GetDownloadsDirectory();
                    Directory.CreateDirectory(directory);
                    var name = SanitizeDownloadName(downloadItem.SuggestedFileName, downloadItem.Id);
                    callback.Continue(GetUniqueDownloadPath(directory, name), showDialog: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
                {
                    Debug.WriteLine($"Unable to prepare download destination: {ex.Message}");
                    var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    try
                    {
                        Directory.CreateDirectory(fallback);
                        callback.Continue(GetUniqueDownloadPath(fallback,
                            SanitizeDownloadName(downloadItem.SuggestedFileName, downloadItem.Id)), showDialog: false);
                    }
                    catch (Exception fallbackException) when (fallbackException is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
                    {
                        Debug.WriteLine($"Unable to use fallback download destination: {fallbackException.Message}");
                        return false;
                    }
                }
            }
            return true;
        }

        protected override void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IDownloadItemCallback callback) => owner.UpdateDownload(downloadItem, callback);
    }

    private sealed class PopupToTabHandler(MainWindow owner) : CefSharp.Handler.LifeSpanHandler
    {
        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture,
            IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
            ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null!;
            var address = Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) &&
                          uri.Scheme is "http" or "https" or "about"
                ? targetUrl
                : "about:blank";
            if (!owner.Dispatcher.HasShutdownStarted && !owner.Dispatcher.HasShutdownFinished)
            {
                try { _ = owner.Dispatcher.BeginInvoke(() => owner.AddTab(address)); }
                catch (InvalidOperationException) { }
            }
            return true;
        }
    }

    private void CloseMainMenu() => MainMenuPopup.IsOpen = false;

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(1);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
        {
            _activeTab.Browser.ZoomLevel = 0;
        }
        UpdateZoomLabel();
    }

    private void ChangeZoom(double delta)
    {
        if (_activeTab is null)
        {
            return;
        }

        _activeTab.Browser.ZoomLevel = Math.Clamp(_activeTab.Browser.ZoomLevel + delta, -5, 6);
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        var zoom = _activeTab is null
            ? 100
            : Math.Round(Math.Pow(1.2, _activeTab.Browser.ZoomLevel) * 100);
        ZoomResetButton.Content = $"{zoom:0}%";
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateWindowButtons();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        CloseMainMenu();
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousWindowState = WindowState;

        _isFullscreen = true;
        var rows = ((Grid)Content).RowDefinitions;
        _previousRowHeights = rows.Take(3).Select(row => row.Height).ToArray();
        rows[0].Height = new GridLength(0);
        rows[1].Height = new GridLength(0);
        rows[2].Height = new GridLength(0);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        if (FindName("FullscreenButton") is Button fullscreenButton)
        {
            fullscreenButton.Content = "⛶";
            fullscreenButton.ToolTip = "Выйти из полноэкранного режима (F11 или Esc)";
        }
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        WindowState = WindowState.Normal;
        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        var rows = ((Grid)Content).RowDefinitions;
        if (_previousRowHeights is { Length: >= 3 } previousRows)
        {
            for (var index = 0; index < 3; index++) rows[index].Height = previousRows[index];
        }
        _previousRowHeights = null;
        WindowState = _previousWindowState;

        _isFullscreen = false;
        if (FindName("FullscreenButton") is Button fullscreenButton)
        {
            fullscreenButton.ToolTip = "Полноэкранный режим (F11)";
        }
        UpdateWindowButtons();
    }

    private void UpdateWindowButtons()
    {
        if (FindName("MaximizeButton") is not Button maximizeButton)
        {
            return;
        }

        maximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        maximizeButton.ToolTip = WindowState == WindowState.Maximized
            ? "Восстановить окно"
            : "Развернуть окно";
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(AddressBox.Text);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.L)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.T)
        {
            AddTab();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.W && _activeTab is not null)
        {
            CloseTab(_activeTab);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.R)
        {
            _activeTab?.Browser.Reload();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab && _tabs.Count > 1)
        {
            var currentIndex = _tabs.IndexOf(_activeTab!);
            var reverse = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var nextIndex = reverse
                ? (currentIndex - 1 + _tabs.Count) % _tabs.Count
                : (currentIndex + 1) % _tabs.Count;
            SelectTab(_tabs[nextIndex]);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _activeTab?.Browser.Reload();
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            if (_isFullscreen)
            {
                ExitFullscreen();
            }
            else
            {
                EnterFullscreen();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_isFullscreen)
            {
                ExitFullscreen();
                e.Handled = true;
            }
            else
            {
                _activeTab?.Browser.Stop();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _downloadRefreshTimer.Stop();
        SaveDownloadHistory();

        foreach (var tab in _tabs.ToArray())
        {
            tab.Browser.Dispose();
        }
        _tabs.Clear();
        base.OnClosed(e);
    }

    private sealed class BrowserTab(ChromiumWebBrowser browser, Border header, TextBlock title)
    {
        public ChromiumWebBrowser Browser { get; } = browser;
        public Border Header { get; } = header;
        public TextBlock Title { get; } = title;
        public bool IsDownloadsTab { get; set; }
    }

    private sealed record Bookmark(string Title, string Url);
    private sealed record BrowserSettings(string SearchEngine, string? Theme = null);
    private sealed record SearchEngineOption(string Name, ImageSource Icon);
}

