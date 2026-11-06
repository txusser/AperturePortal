using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApertureOS.Models;
using ApertureOS.Services;

namespace ApertureOS;

/// <summary>
/// Small "Install this game?" / "Launch this game?" confirmation, used identically from the normal
/// mouse-driven window and gamepad-driven Console Mode. Left/Right switches focus between the two
/// buttons and A activates whichever is focused, mirroring OverlayWindow's pause-menu pattern -
/// necessary because gamepad state is polled directly from XInput rather than routed through WPF's
/// normal focus system, so it needs its own input loop instead of just relying on IsDefault/IsCancel.
/// </summary>
public partial class GameActionConfirmWindow : Window
{
    private readonly DispatcherTimer _inputTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly GamepadEdge _leftEdge = new();
    private readonly GamepadEdge _rightEdge = new();
    private readonly GamepadEdge _upEdge = new();
    private readonly GamepadEdge _downEdge = new();
    private readonly GamepadEdge _confirmEdge = new();
    private readonly GamepadEdge _cancelEdge = new();

    // Only the buttons actually shown - info-only mode (see below) collapses CancelButton, and a
    // collapsed button should never be a valid Left/Right landing spot for the gamepad loop.
    private Button[] Buttons => new[] { ConfirmButton, FinishButton, HideButton, CancelButton }.Where(b => b.Visibility == Visibility.Visible).ToArray();

    /// <summary>True when the user picked Hide/Unhide instead of Launch/Install - the caller toggles Game.IsHidden.</summary>
    public bool HideToggleRequested { get; private set; }

    /// <summary>True when the user picked Finished/Not finished - the caller toggles Game.IsFinished.</summary>
    public bool FinishToggleRequested { get; private set; }

    /// <summary>
    /// Normal "Install/Launch this game?" confirmation with both buttons. The gamepad button-legend
    /// hint only makes sense in Console Mode - the desktop window is a mouse/keyboard surface (it
    /// still quietly accepts gamepad input underneath, since the desktop grid itself does too, but
    /// doesn't advertise it) - so isConsoleMode controls whether that hint line shows at all.
    /// </summary>
    public GameActionConfirmWindow(Game game, string actionVerb, bool isConsoleMode)
        : this(game, $"{actionVerb} \"{game.Name}\"?", actionVerb, infoOnly: false, isConsoleMode)
    {
    }

    /// <summary>
    /// Info-only variant with just a single dismiss button - used when a game is already mid-download,
    /// so pressing its tile again explains that instead of re-offering to install it from scratch.
    /// </summary>
    public static GameActionConfirmWindow CreateInfoOnly(Game game, string message, bool isConsoleMode) =>
        new(game, message, "OK", infoOnly: true, isConsoleMode);

    private GameActionConfirmWindow(Game game, string message, string confirmButtonText, bool infoOnly, bool isConsoleMode)
    {
        InitializeComponent();

        // Side-panel layout (redesign): the game is the title; the old question line only remains
        // for the info-only variant ("... is currently installing").
        TitleText.Text = game.Name;
        MessageText.Text = infoOnly ? message : game.IsInstalled ? string.Empty : "Not installed yet - Install hands it to Steam.";
        MessageText.Visibility = MessageText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirmButtonText == "Launch" ? "Play" : confirmButtonText;
        PlatformText.Text = CategoryService.GetPlatformDisplayName(game.Platform).ToUpperInvariant();
        FamilyBadge.Visibility = game.IsFamilyShared ? Visibility.Visible : Visibility.Collapsed;
        FillStats(game);
        if (infoOnly)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            HideButton.Visibility = Visibility.Collapsed;
            FinishButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            HideButton.Content = game.IsHidden ? "Unhide" : "Hide";
            FinishButton.Content = game.IsFinished ? "Not finished" : "Finished";
        }

        HintText.Visibility = isConsoleMode ? Visibility.Visible : Visibility.Collapsed;
        if (isConsoleMode && infoOnly)
        {
            HintText.Text = "A or B to close";
        }

        var artPath = File.Exists(game.HeaderImagePath) ? game.HeaderImagePath
                    : File.Exists(game.CoverImagePath) ? game.CoverImagePath
                    : null;
        if (artPath is not null)
        {
            var art = new BitmapImage();
            art.BeginInit();
            art.UriSource = new Uri(artPath);
            art.CacheOption = BitmapCacheOption.OnLoad;
            art.DecodePixelWidth = 1000;
            art.EndInit();
            HeroArt.Source = art;
        }

        Loaded += (_, _) =>
        {
            PlaceOverOwner();
            ConfirmButton.Focus();

            // Prime the trackers to the *actual* current physical state rather than assuming
            // nothing's held: this dialog is opened by an A press on a tile (see
            // ConsoleModeWindow.LaunchSelected/MainWindow.ActivateFocusedElement), and that same
            // press is very often still physically down for this window's first tick or two.
            // Without this, that residual hold reads as a brand-new edge the moment polling starts
            // and instantly activates Confirm on a press the user never intended as "confirm this" -
            // or doesn't, depending on exactly how fast they release, which is what made the number
            // of presses actually needed to launch a game feel inconsistent. Syncing first makes it
            // deterministic: always a real release of the opening press, then a deliberate second
            // press to confirm.
            var pad = GamepadService.Poll();
            _leftEdge.Sync(pad.Left);
            _rightEdge.Sync(pad.Right);
            _upEdge.Sync(pad.Up);
            _downEdge.Sync(pad.Down);
            _confirmEdge.Sync(pad.A);
            _cancelEdge.Sync(pad.B);

            _inputTimer.Start();
        };
        Closed += (_, _) => _inputTimer.Stop();
        _inputTimer.Tick += InputTimer_Tick;
    }

    private void InputTimer_Tick(object? sender, EventArgs e)
    {
        var pad = GamepadService.Poll();
        _leftEdge.Update(pad.Left, () => MoveSelection(-1));
        _rightEdge.Update(pad.Right, () => MoveSelection(1));
        // Vertical layout: Down from Play goes to the row below it, Up goes back to Play.
        _upEdge.Update(pad.Up, () => ConfirmButton.Focus());
        _downEdge.Update(pad.Down, () => { if (Keyboard.FocusedElement == ConfirmButton) MoveSelection(1); });
        _confirmEdge.Update(pad.A, ActivateFocusedButton);
        _cancelEdge.Update(pad.B, () =>
        {
            DialogResult = false;
            Close();
        });
    }

    /// <summary>Covers the owner window exactly (dimmed backdrop), so the panel docks to its right edge.</summary>
    private void PlaceOverOwner()
    {
        if (Owner is null)
            return;

        if (Owner.WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            Left = Owner.Left;
            Top = Owner.Top;
            Width = Owner.ActualWidth;
            Height = Owner.ActualHeight;
        }

        SidePanel.Width = Math.Min(760, Math.Max(480, (Owner.WindowState == WindowState.Maximized ? SystemParameters.PrimaryScreenWidth : Owner.ActualWidth) * 0.55));
    }

    private void FillStats(Game game)
    {
        var info = GameInfoService.Shared.Get(game);
        MainHoursText.Text = info?.MainHours is { } main ? $"{main:0.#} h" : "—";
        ExtrasHoursText.Text = info?.ExtrasHours is { } extras ? $"{extras:0.#} h with extras" : " ";
        CriticText.Text = info?.CriticScore is { } critic ? $"{critic:0}" : "—";
        CriticCountText.Text = info?.CriticCount is { } count ? $"{count} critic reviews" : " ";
        SteamText.Text = info?.SteamPositivePercent is { } positive ? $"{positive}%" : "—";
        SteamLabelText.Text = info?.SteamReviewSummary ?? " ";

        var meta = new List<string>();
        if (game.LastPlayedAt is { } played)
            meta.Add($"Last played {played.ToLocalTime():d MMM yyyy}");
        if (info?.Genres is { Count: > 0 } genres)
            meta.Add(string.Join(", ", genres.Take(3)));
        if (info?.ReleaseYear is { } year)
            meta.Add(year.ToString());
        if (info is null)
            meta.Add("Length and scores appear after Settings → Fetch Game Info");
        MetaText.Text = string.Join("  ·  ", meta);
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void MoveSelection(int direction)
    {
        var buttons = Buttons;
        int currentIndex = Array.IndexOf(buttons, Keyboard.FocusedElement as Button);
        int newIndex = Math.Clamp((currentIndex < 0 ? 0 : currentIndex) + direction, 0, buttons.Length - 1);
        buttons[newIndex].Focus();
    }

    private static void ActivateFocusedButton()
    {
        if (Keyboard.FocusedElement is Button button)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        FinishToggleRequested = true;
        DialogResult = false;
        Close();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        HideToggleRequested = true;
        DialogResult = false;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
