using System.Windows;
using System.Windows.Input;
using ApertureOS.Services;

namespace ApertureOS;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly GameLibraryService _libraryService;

    public SettingsWindow(SettingsService settingsService, GameLibraryService libraryService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _libraryService = libraryService;

        var settings = _settingsService.Load();
        ApiKeyTextBox.Text = settings.SteamGridDbApiKey;
        StartWithWindowsCheckBox.IsChecked = StartupService.IsEnabled();
        LaunchConsoleModeCheckBox.IsChecked = settings.LaunchInConsoleMode;
        AutoFetchMissingArtCheckBox.IsChecked = settings.AutoFetchMissingArt;
        IgdbClientIdTextBox.Text = settings.IgdbClientId;
        IgdbClientSecretBox.Password = settings.IgdbClientSecret;
        AutoFetchGameInfoCheckBox.IsChecked = settings.AutoFetchGameInfo;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();
        settings.SteamGridDbApiKey = ApiKeyTextBox.Text.Trim();
        settings.LaunchInConsoleMode = LaunchConsoleModeCheckBox.IsChecked == true;
        settings.AutoFetchMissingArt = AutoFetchMissingArtCheckBox.IsChecked == true;
        settings.IgdbClientId = IgdbClientIdTextBox.Text.Trim();
        settings.IgdbClientSecret = IgdbClientSecretBox.Password.Trim();
        settings.AutoFetchGameInfo = AutoFetchGameInfoCheckBox.IsChecked == true;
        _settingsService.Save(settings);

        if (!StartupService.SetEnabled(StartWithWindowsCheckBox.IsChecked == true))
        {
            MessageBox.Show(this,
                "Couldn't update the \"Start with Windows\" setting - see %APPDATA%\\ApertureOS\\error.log for details.",
                "Aperture Portal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// A plain reload from disk - the main window already does this (and re-checks install state)
    /// automatically the instant Settings closes, but that's invisible while this dialog is still
    /// open. This gives an explicit, immediate "yes, it's current" without needing to close first.
    /// </summary>
    private void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var games = _libraryService.LoadGames();
        RefreshStatusText.Text = $"Library refreshed - {games.Count} game{(games.Count == 1 ? "" : "s")} on file.";
    }

    private async void FetchMissingArtButton_Click(object sender, RoutedEventArgs e)
    {
        // Uses the key as typed, so it works before Save too.
        var apiKey = ApiKeyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            FetchMissingArtStatusText.Text = "Enter a SteamGridDB API key first.";
            return;
        }

        FetchMissingArtButton.IsEnabled = false;
        try
        {
            var games = _libraryService.LoadGames();
            var missing = games.Count(ArtFetchService.NeedsArt);
            if (missing == 0)
            {
                FetchMissingArtStatusText.Text = "Every game already has art.";
                return;
            }

            var progress = new Progress<string>(text => FetchMissingArtStatusText.Text = text);
            var updated = await ArtFetchService.FetchMissingArtAsync(apiKey, _libraryService, games, progress);
            _libraryService.SaveGames(games);
            FetchMissingArtStatusText.Text =
                $"Found art for {updated} of {missing} game{(missing == 1 ? "" : "s")}. The rest have no SteamGridDB match - use Find Art in Edit to pick one by hand.";
        }
        catch (Exception ex)
        {
            FetchMissingArtStatusText.Text = $"Fetching art failed: {ex.Message}";
        }
        finally
        {
            FetchMissingArtButton.IsEnabled = true;
        }
    }

    private async void FetchGameInfoButton_Click(object sender, RoutedEventArgs e)
    {
        // Uses the credentials as typed, so it works before Save too.
        var clientId = IgdbClientIdTextBox.Text.Trim();
        var clientSecret = IgdbClientSecretBox.Password.Trim();
        if (clientId.Length == 0 || clientSecret.Length == 0)
        {
            FetchGameInfoStatusText.Text = "Enter the Twitch Client ID and Secret first.";
            return;
        }

        FetchGameInfoButton.IsEnabled = false;
        try
        {
            var games = _libraryService.LoadGames();
            var progress = new Progress<string>(text => FetchGameInfoStatusText.Text = text);
            var updated = await GameInfoService.Shared.FetchMissingAsync(clientId, clientSecret, games, progress);
            FetchGameInfoStatusText.Text = updated == 0
                ? "Every game already has up-to-date info."
                : $"Fetched info for {updated} game{(updated == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            FetchGameInfoStatusText.Text = $"Fetching game info failed: {ex.Message}";
        }
        finally
        {
            FetchGameInfoButton.IsEnabled = true;
        }
    }
}
