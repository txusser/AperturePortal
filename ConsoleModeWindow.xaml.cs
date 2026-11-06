using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApertureOS.Models;
using ApertureOS.Services;

namespace ApertureOS;

public partial class ConsoleModeWindow : Window
{
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _inputTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };

    private readonly GamepadRepeater _upRepeater = new();
    private readonly GamepadRepeater _downRepeater = new();
    private readonly GamepadRepeater _leftRepeater = new();
    private readonly GamepadRepeater _rightRepeater = new();
    private readonly GamepadEdge _aEdge = new();
    private readonly GamepadEdge _bEdge = new();
    private readonly GamepadEdge _lbEdge = new();
    private readonly GamepadEdge _rbEdge = new();
    private readonly GamepadEdge _xEdge = new();
    private readonly GamepadEdge _yEdge = new();

    // Same order the filter tabs appear in, so LB/RB can step through them as a simple ring.
    private static readonly LibraryFilter[] FilterOrder =
    [
        LibraryFilter.Installed, LibraryFilter.Backlog, LibraryFilter.Finished, LibraryFilter.Hidden
    ];

    private int _columns = 5;
    private int _selectedIndex;
    private bool _dialogOpen;

    public ConsoleModeWindow(MainWindow owner)
    {
        // Assigned before InitializeComponent() - the XAML no longer sets IsChecked="True" on a
        // filter tab for exactly this reason (see the comment on that markup), but this ordering
        // is cheap extra safety against the same class of bug if that ever changes again.
        _owner = owner;

        InitializeComponent();

        // Shares MainWindow's filtered/sorted view rather than the raw list, so a filter picked in
        // either window - desktop tabs or these LB/RB-driven ones - applies to both consistently.
        GameListBox.ItemsSource = _owner.GamesView;

        _inputTimer.Tick += InputTimer_Tick;
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    // Top-bar clock; minute precision is all it shows.
    private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
    private string? _heroImagePath;

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("ddd d MMM");
    }

    /// <summary>Count line next to the category chips, and the empty state when the current tab has no games.</summary>
    private void UpdateCountAndEmptyState()
    {
        var count = GameListBox.Items.Count;
        CountText.Text = count == 1 ? "1 game" : $"{count:N0} games";
        EmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateTitle.Text = _owner.Games.Count == 0 ? "No games yet" : "Nothing here yet";
        EmptyStateText.Text = _owner.Games.Count == 0
            ? "Add games from the desktop view, then come back in here."
            : _owner.CurrentFilter switch
            {
                LibraryFilter.Finished => "Games you mark as finished show up here.",
                LibraryFilter.Hidden => "Games you hide show up here.",
                LibraryFilter.Backlog => "Every game you have is installed.",
                _ => "No installed games in this category."
            };
        if (count == 0)
            UpdateHero(null);
    }

    /// <summary>
    /// Fills the hero area with the selected game: its header art (else its cover) faded into the
    /// background, platform/family badges, title and the game-info line.
    /// </summary>
    private void UpdateHero(Game? game)
    {
        HeroPanel.Visibility = game is null ? Visibility.Hidden : Visibility.Visible;
        if (game is null)
        {
            HeroImage.Source = null;
            _heroImagePath = null;
            return;
        }

        HeroTitle.Text = game.Name;
        HeroPlatformText.Text = CategoryService.GetPlatformDisplayName(game.Platform).ToUpperInvariant();
        HeroFamilyBadge.Visibility = game.IsFamilyShared ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();
        if (GameInfoService.Shared.Get(game)?.Summary() is { Length: > 0 } summary)
            parts.Add(summary);
        if (game.LastPlayedAt is { } lastPlayed)
            parts.Add($"Played {DescribeAgo(lastPlayed)}");
        if (!game.IsInstalled)
            parts.Add("Not installed");
        HeroInfoText.Text = string.Join("  ·  ", parts);

        var artPath = File.Exists(game.HeaderImagePath) ? game.HeaderImagePath
                    : File.Exists(game.CoverImagePath) ? game.CoverImagePath
                    : null;
        if (artPath == _heroImagePath)
            return;

        _heroImagePath = artPath;
        HeroImage.Source = artPath is null ? null : LoadHeroImage(artPath);
    }

    private static BitmapImage? LoadHeroImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
            image.DecodePixelWidth = 1920;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribeAgo(DateTime utc)
    {
        var days = (DateTime.Now.Date - utc.ToLocalTime().Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 30 => $"{days} days ago",
            _ => utc.ToLocalTime().ToString("d MMM yyyy")
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _owner.GameSessionEnded += Owner_GameSessionEnded;

        SyncFilterTabToOwner();
        SyncCategoryChipsToOwner();
        UpdateColumns();
        UpdateClock();
        _clockTimer.Start();
        if (GameListBox.Items.Count > 0)
        {
            SelectIndex(0);
        }

        UpdateCountAndEmptyState();

        ResetInputTrackers();
        _inputTimer.Start();
    }

    /// <summary>Mirrors the desktop's category chips (and which one is active) as of whenever Console Mode was entered - a fresh snapshot each time, not live-synced while both are open, since desktop-only surfaces like category management can't be reached without leaving Console Mode first anyway.</summary>
    private void SyncCategoryChipsToOwner()
    {
        var chips = _owner.BuildCategoryChips();
        var selected = chips.FirstOrDefault(c => c.CategoryId == _owner.CurrentCategoryFilter) ?? chips[0];
        selected.IsSelected = true;
        CategoryChipsItemsControl.ItemsSource = chips;
    }

    /// <summary>Reflects whichever filter was already active (e.g. picked from the desktop tabs before entering Console Mode) in this window's own tab row, without re-triggering a redundant filter change.</summary>
    private void SyncFilterTabToOwner()
    {
        var button = _owner.CurrentFilter switch
        {
            LibraryFilter.Backlog => FilterBacklogButton,
            LibraryFilter.Finished => FilterFinishedButton,
            LibraryFilter.Hidden => FilterHiddenButton,
            _ => FilterInstalledButton
        };

        if (!button.IsChecked!.Value)
        {
            button.IsChecked = true;
        }
    }

    private void FilterTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !Enum.TryParse<LibraryFilter>(tag, out var filter))
            return;

        _owner.SetLibraryFilter(filter);
        _selectedIndex = 0;
        if (GameListBox.Items.Count > 0)
        {
            SelectIndex(0);
        }

        UpdateCountAndEmptyState();
    }

    private void CategoryChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: CategoryChipViewModel chip })
            return;

        _owner.SetCategoryFilter(chip.CategoryId);
        _selectedIndex = 0;
        if (GameListBox.Items.Count > 0)
        {
            SelectIndex(0);
        }

        UpdateCountAndEmptyState();
    }

    private void CycleFilter(int direction)
    {
        int currentIndex = Array.IndexOf(FilterOrder, _owner.CurrentFilter);
        int newIndex = (currentIndex + direction + FilterOrder.Length) % FilterOrder.Length;
        var button = FilterOrder[newIndex] switch
        {
            LibraryFilter.Backlog => FilterBacklogButton,
            LibraryFilter.Finished => FilterFinishedButton,
            LibraryFilter.Hidden => FilterHiddenButton,
            _ => FilterInstalledButton
        };

        // Setting IsChecked fires FilterTab_Checked, which does the actual filtering/reselection.
        button.IsChecked = true;
    }

    /// <summary>Steps the category chip row as a ring, X/Y-bound - LB/RB were already taken by
    /// CycleFilter above, and XInput's triggers aren't decoded anywhere in this app (GamepadService
    /// only exposes digital buttons/D-pad/stick), so X/Y are the next free, always-present buttons
    /// on a standard controller.</summary>
    private void CycleCategory(int direction)
    {
        if (CategoryChipsItemsControl.ItemsSource is not List<CategoryChipViewModel> chips || chips.Count == 0)
            return;

        int currentIndex = chips.FindIndex(c => c.CategoryId == _owner.CurrentCategoryFilter);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int newIndex = (currentIndex + direction + chips.Count) % chips.Count;

        // Setting IsSelected (rather than clicking) still fires CategoryChip_Checked: it's TwoWay-bound
        // to the RadioButton's IsChecked, and WPF's GroupName exclusivity applies to bound updates the
        // same as real clicks, unchecking the previously-selected chip (and flowing its own IsSelected
        // back to false) as a side effect.
        chips[newIndex].IsSelected = true;
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _inputTimer.Stop();
        _clockTimer.Stop();
        _inputTimer.Tick -= InputTimer_Tick;
        _owner.GameSessionEnded -= Owner_GameSessionEnded;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        // Mirrors the tile width + margin used in the ItemTemplate/ItemContainerStyle below, so
        // Up/Down navigation math lines up with how WrapPanel actually wraps rows: 210 content +
        // 8 padding (4x2) + 8 ring (4x2) + 20 margin (10x2) + 200 tile = 236 (redesign; was 248). Previously 230,
        // which overestimated the column count and made Up/Down overshoot past the tile directly
        // above/below. Uses the list's own ActualWidth rather than the window's, since that
        // already excludes the surrounding Grid's margin - the actual space WrapPanel wraps within.
        const double slotWidth = 236;
        _columns = Math.Max(1, (int)(GameListBox.ActualWidth / slotWidth));
    }

    private void Owner_GameSessionEnded(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            Show();

            // Not just Activate(): that silently fails when Steam grabs the foreground as the
            // game closes, leaving this window hidden behind it - see WindowFocus.
            WindowFocus.ReturnToFront(this);

            // Resyncs to whatever's actually physically held right now rather than blindly assuming
            // nothing is - a normal full play session ending naturally, nothing usually is, so this
            // behaves the same as a plain reset would have. But the overlay's "Exit Game" path is a
            // synchronous chain (A press -> ActivateFocusedButton -> ExitGameRequested -> kill
            // process -> EndGameSession -> this handler, all within the same input-timer tick, no
            // real time elapsed) - the very same A press used to confirm the exit is still
            // physically down here more often than not. A blind reset used to read that as a
            // brand-new press the instant this grid reappeared and fire on whatever tile happened
            // to be focused - see GamepadEdge.Sync for why resyncing instead fixes it.
            ResetInputTrackers();
        });
    }

    private void ResetInputTrackers()
    {
        var pad = GamepadService.Poll();
        _upRepeater.Sync(pad.Up);
        _downRepeater.Sync(pad.Down);
        _leftRepeater.Sync(pad.Left);
        _rightRepeater.Sync(pad.Right);
        _aEdge.Sync(pad.A);
        _bEdge.Sync(pad.B);
        _lbEdge.Sync(pad.LeftShoulder);
        _rbEdge.Sync(pad.RightShoulder);
        _xEdge.Sync(pad.X);
        _yEdge.Sync(pad.Y);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                Move(0, -1);
                e.Handled = true;
                break;
            case Key.Down:
                Move(0, 1);
                e.Handled = true;
                break;
            case Key.Left:
                Move(-1, 0);
                e.Handled = true;
                break;
            case Key.Right:
                Move(1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                GoBack();
                e.Handled = true;
                break;
        }
    }

    private void InputTimer_Tick(object? sender, EventArgs e)
    {
        var pad = GamepadService.Poll();

        // LaunchSelected() calls Hide() rather than Close(), so this window (and its timer)
        // stays alive for the whole game session to catch Owner_GameSessionEnded. Without this
        // guard, XInput state would keep reaching LaunchSelected()/GoBack() while hidden and
        // the same controller the player is using in-game would launch more games underneath it.
        //
        // _dialogOpen additionally covers the Install/Launch confirmation popup: it polls the
        // same raw XInput state through its own input loop, so without this an A press meant for
        // its Confirm button would also reach this grid and move/relaunch selection underneath it.
        //
        // IsThisAppForeground() is a second, independent guard on top of IsVisible - belt and
        // braces rather than a fix for a specific observed gap, matching the same check MainWindow's
        // own grid already requires (see the comment on gridInputAllowed there). IsVisible alone
        // relies on Hide()/Show() always being called at exactly the right moments; this doesn't
        // depend on that being perfectly true.
        bool inputAllowed = IsVisible && !_dialogOpen && MainWindow.IsThisAppForeground();

        // Feed the repeaters/edges the real button state and gate the action instead of
        // ANDing it into "pressed" - see the equivalent comment in MainWindow.GamepadTimer_Tick
        // for why: it stops a button held through a hidden period from firing the instant
        // this window becomes visible again.
        _upRepeater.Update(pad.Up, () => { if (inputAllowed) Move(0, -1); });
        _downRepeater.Update(pad.Down, () => { if (inputAllowed) Move(0, 1); });
        _leftRepeater.Update(pad.Left, () => { if (inputAllowed) Move(-1, 0); });
        _rightRepeater.Update(pad.Right, () => { if (inputAllowed) Move(1, 0); });
        _aEdge.Update(pad.A, () => { if (inputAllowed) LaunchSelected(); });
        _bEdge.Update(pad.B, () => { if (inputAllowed) GoBack(); });
        _lbEdge.Update(pad.LeftShoulder, () => { if (inputAllowed) CycleFilter(-1); });
        _rbEdge.Update(pad.RightShoulder, () => { if (inputAllowed) CycleFilter(1); });
        _xEdge.Update(pad.X, () => { if (inputAllowed) CycleCategory(-1); });
        _yEdge.Update(pad.Y, () => { if (inputAllowed) CycleCategory(1); });
    }

    private void Move(int columnDelta, int rowDelta)
    {
        // GameListBox.Items.Count (the current filtered view) rather than _owner.Games.Count (the
        // full library) - see the equivalent fix/comment in MainWindow.MoveTileFocus.
        if (GameListBox.Items.Count == 0)
            return;

        int newIndex = _selectedIndex + columnDelta + rowDelta * _columns;
        if (newIndex < 0 || newIndex >= GameListBox.Items.Count)
            return;

        SelectIndex(newIndex);
    }

    private void SelectIndex(int index)
    {
        _selectedIndex = index;
        GameListBox.SelectedIndex = index;
        GameListBox.ScrollIntoView(GameListBox.SelectedItem);
        UpdateHero(GameListBox.SelectedItem as Game);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // The grid is virtualized, so the tile only exists once ScrollIntoView's layout pass has
            // run; force that pass if it hasn't happened yet.
            if (GameListBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
            {
                GameListBox.UpdateLayout();
                container = GameListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            }

            container?.Focus();
        }), DispatcherPriority.Input);
    }

    private void LaunchSelected()
    {
        // Read the game back out of the filtered view (GameListBox.Items), not _owner.Games by
        // raw index - _selectedIndex is a position within whatever's currently filtered/sorted,
        // which very rarely lines up with the same index in the full, unfiltered library.
        if (_selectedIndex < 0 || _selectedIndex >= GameListBox.Items.Count ||
            GameListBox.Items[_selectedIndex] is not Game game)
        {
            return;
        }

        _dialogOpen = true;
        var launched = _owner.ConfirmAndActOnGame(game, this);
        _dialogOpen = false;

        // Hide/Finished in the dialog moves the game to another tab: keep selection and counts valid.
        if (!launched)
        {
            if (_selectedIndex >= GameListBox.Items.Count)
                _selectedIndex = Math.Max(0, GameListBox.Items.Count - 1);
            if (GameListBox.Items.Count > 0)
                SelectIndex(_selectedIndex);
            UpdateCountAndEmptyState();
        }

        // Only a real launch should hide this grid for the game session - installing just kicks
        // off a background download and the user should stay right here to keep browsing/watch
        // the tile's progress badge, same as in the normal window.
        if (launched)
        {
            Hide();
        }
    }

    private void GoBack()
    {
        Close();
    }
}
