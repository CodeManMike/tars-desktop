using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TarsClient.Controls;

namespace TarsClient.Views;

/// <summary>
/// The one window: a compact strip that becomes a full terminal when taller than <see cref="ExpandThreshold"/>. Owns
/// window chrome, focus behaviour, layout and bounds, and keyboard handling; everything else lives in the view model.
/// </summary>
public partial class MainWindow : Window
{
    #region Fields

    private const double ExpandThreshold = 240;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const string BannerArt =
        " _____        _        ____      ____  \n" +
        "|_   _|      / \\      |  _ \\    / ___| \n" +
        "  | |       / _ \\     | |_) |   \\___ \\ \n" +
        "  | |   _  / ___ \\  _ |  _ <  _  ___) |\n" +
        "  |_|  (_)/_/   \\_\\(_)|_| \\_\\(_)|____/ ";

    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB3, 0x47);

    private readonly MainViewModel _vm;
    private readonly SolidColorBrush _cursorBrush = new(Amber);
    private IntPtr _hwnd;
    private bool _applyingBounds;
    private bool _spacePtt;

    #endregion

    #region Constructor

    /// <summary>Creates the window for <paramref name="vm"/>.</summary>
    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        Resources["CursorBrush"] = _cursorBrush;
        // A layered (transparent) window costs a little GPU/CPU on every repaint: only when asked for.
        AllowsTransparency = vm.S.BackgroundOpacity < 0.999;
        InitializeComponent();
        Banner.Text = BannerArt;
        ApplyWindowOptions();
        ApplyBounds(vm.IsExpanded);
        ApplyFont();
        ApplyGlow();

        vm.AppearanceChanged += ApplyWindowOptions;
        vm.PropertyChanged += OnVmChanged;
        vm.Lines.CollectionChanged += OnLinesChanged;
        vm.Settings.LogLines.CollectionChanged += (_, _) => LogScroll.ScrollToEnd();
        vm.Settings.PropertyChanged += OnSettingsChanged;
        vm.ShowRequested += alarm => ShowFromTray(alarm);
        vm.AlarmStopped += () =>
        {
            if (_hwnd != IntPtr.Zero) Win32.Flash(_hwnd, false);
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PowerOnAnimation();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        StateChanged += (_, _) => UpdateVisibility();
        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) =>
        {
            SaveBounds();
            KeepReplyAtEnd();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewTextInput += OnPreviewTextInput;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += (_, _) => EndSpacePtt();
    }

    #endregion

    #region Properties

    /// <summary>Set by Quit: [×] and Alt+F4 otherwise hide to the tray.</summary>
    public bool AllowClose { get; set; }

    private static bool TypingSomewhere => Keyboard.FocusedElement is TextBox or PasswordBox;

    #endregion

    #region Public Methods

    /// <summary>Shows and activates the window; for an alarm, also topmost with a flashing taskbar button.</summary>
    public void ShowFromTray(bool alarm = false)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (alarm)
        {
            Topmost = true;
            Win32.Flash(_hwnd, true);
        }
        Activate();
        Win32.SetForegroundWindow(_hwnd);
    }

    /// <summary>Tray click: hide if showing, otherwise show.</summary>
    public void ToggleVisible()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowFromTray();
    }

    /// <summary>Opens settings, expanding the window first.</summary>
    public void OpenSettings()
    {
        if (!_vm.IsExpanded) ToggleLayout();
        _vm.SettingsOpen = true;
    }

    #endregion

    #region Protected Methods

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        KeyBox.Password = _vm.Settings.AccessKey;
    }

    #endregion

    #region Window chrome and focus

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32.DisableRoundedCorners(_hwnd);
        Win32.SetNoActivate(_hwnd, _vm.S.NoActivate);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_NCLBUTTONDBLCLK:
                // Double-clicking the title bar toggles compact/expanded instead of maximizing.
                ToggleLayout();
                handled = true;
                break;
            case WM_EXITSIZEMOVE:
                AutoLayoutAfterResize();
                break;
        }
        return IntPtr.Zero;
    }

    private void ApplyWindowOptions()
    {
        byte alpha = (byte)Math.Round(Math.Clamp(_vm.S.BackgroundOpacity, 0.2, 1) * 255);
        Background = AllowsTransparency ? new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)) : Brushes.Black;
        ShowInTaskbar = _vm.S.ShowInTaskbar;
        if (_hwnd != IntPtr.Zero) Win32.SetNoActivate(_hwnd, _vm.S.NoActivate);
    }

    /// <summary>
    /// With "don't steal focus" on, clicks on buttons, meters and the title bar leave the keyboard where it was;
    /// clicking something you type into (the prompt, settings fields) takes focus deliberately.
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.AlarmActive)
        {
            _vm.Dismiss();
            return;
        }
        if (!_vm.S.NoActivate || IsActive) return;
        if (WantsFocus(e.OriginalSource as DependencyObject) || _vm.SettingsOpen) Activate();
    }

    private bool WantsFocus(DependencyObject? d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBox or PasswordBox or ListBox or BlockSlider or ChoiceRow || d == Scrollback || d == ReplyScroll) return true;
        }
        return false;
    }

    /// <summary>CRT power-on: a bright line opens vertically into the picture.</summary>
    private void PowerOnAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PowerOn.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.01, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        PowerOn.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        Screen.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320)));
    }

    /// <summary>Hidden or minimized: no meters, no cursor blink, zero animation frames.</summary>
    private void UpdateVisibility()
    {
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        _vm.SetVisible(visible);
        if (!visible)
        {
            _cursorBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            return;
        }
        var blink = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1060) };
        blink.KeyFrames.Add(new DiscreteColorKeyFrame(Amber, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteColorKeyFrame(Colors.Transparent, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(530))));
        _cursorBrush.BeginAnimation(SolidColorBrush.ColorProperty, blink);
    }

    #endregion

    #region Layout and bounds

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsExpanded):
                ApplyFont();
                if (_vm.IsExpanded) FocusTerminal();
                else if (_vm.SettingsOpen) _vm.SettingsOpen = false;
                break;
            case nameof(MainViewModel.BaseFontSize):
                ApplyFont();
                break;
            case nameof(MainViewModel.Glow):
                ApplyGlow();
                break;
            case nameof(MainViewModel.ReplyText):
                KeepReplyAtEnd();
                break;
            case nameof(MainViewModel.SettingsOpen) when _vm.SettingsOpen:
                if (KeyBox.Password != _vm.Settings.AccessKey) KeyBox.Password = _vm.Settings.AccessKey;
                FocusFirstInTab();
                break;
            case nameof(MainViewModel.SettingsOpen):
                FocusTerminal();
                break;
            case nameof(MainViewModel.Topmost):
                Topmost = _vm.Topmost;
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.ConfirmVisible) when _vm.Settings.ConfirmVisible:
                Dispatcher.BeginInvoke(() => ConfirmYes.Focus());
                break;
            case nameof(SettingsViewModel.TabIndex):
                FocusFirstInTab();
                break;
        }
    }

    private void FocusTerminal() => Dispatcher.BeginInvoke(() =>
    {
        Scrollback.ScrollToEnd();
        ExpandedPrompt.Focus();
    });

    private void ApplyFont() => FontSize = _vm.BaseFontSize + (_vm.IsExpanded ? 2 : 0);

    private void ApplyGlow() => Screen.Effect = _vm.Glow ? (Effect)FindResource("Glow") : null;

    private void KeepReplyAtEnd() => Dispatcher.BeginInvoke(() => ReplyScroll.ScrollToEnd(), DispatcherPriority.Background);

    /// <summary>Follows the tail unless you scrolled up to read.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_vm.IsExpanded || !_vm.IsVisible) return;
        if (Scrollback.VerticalOffset < Scrollback.ScrollableHeight - 40) return;
        Dispatcher.BeginInvoke(() => Scrollback.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ToggleLayout()
    {
        SaveBounds();
        _vm.IsExpanded = !_vm.IsExpanded;
        _vm.S.Layout = _vm.IsExpanded ? "expanded" : "compact";
        ApplyBounds(_vm.IsExpanded);
        _vm.Store.Save();
    }

    /// <summary>Dragging a compact strip taller turns it into the terminal (and back).</summary>
    private void AutoLayoutAfterResize()
    {
        bool wantExpanded = ActualHeight >= ExpandThreshold;
        if (wantExpanded == _vm.IsExpanded) return;
        _vm.IsExpanded = wantExpanded;
        _vm.S.Layout = wantExpanded ? "expanded" : "compact";
        SaveBounds();
        _vm.Store.Save();
    }

    private void ApplyBounds(bool expanded)
    {
        var bounds = expanded ? _vm.S.Bounds.Expanded : _vm.S.Bounds.Compact;
        if (bounds is not { Length: 4 }) return;
        double width = Math.Max(MinWidth, bounds[2]);
        double height = Math.Max(MinHeight, bounds[3]);
        height = expanded ? Math.Max(height, ExpandThreshold) : Math.Min(height, ExpandThreshold - 1);
        double x = bounds[0], y = bounds[1];
        if (OffScreen(x, y, width))
        {
            // A monitor may have been unplugged since the last run.
            var work = SystemParameters.WorkArea;
            x = work.Right - width - 20;
            y = work.Top + 20;
        }
        _applyingBounds = true;
        Left = x;
        Top = y;
        Width = width;
        Height = height;
        _applyingBounds = false;
    }

    private static bool OffScreen(double x, double y, double width)
    {
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth, bottom = top + SystemParameters.VirtualScreenHeight;
        return x + width < left + 60 || x > right - 60 || y < top - 10 || y > bottom - 40;
    }

    private void SaveBounds()
    {
        if (_applyingBounds || WindowState != WindowState.Normal || !IsLoaded) return;
        double[] bounds = [Math.Round(Left), Math.Round(Top), Math.Round(ActualWidth), Math.Round(ActualHeight)];
        if (_vm.IsExpanded) _vm.S.Bounds.Expanded = bounds;
        else _vm.S.Bounds.Compact = bounds;
        _vm.Store.Save();
    }

    #endregion

    #region Title bar

    private void Layout_Click(object sender, RoutedEventArgs e) => ToggleLayout();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SettingsOpen)
        {
            _vm.SettingsOpen = false;
            return;
        }
        Activate();
        OpenSettings();
    }

    #endregion

    #region Keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Settings.ConfirmVisible) return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = Escape();
                break;
            case Key.F2:
                if (_vm.SettingsOpen) _vm.SettingsOpen = false;
                else
                {
                    Activate();
                    OpenSettings();
                }
                e.Handled = true;
                break;
            case Key.Space when !TypingSomewhere && !_vm.SettingsOpen:
                // Space is push-to-talk while the window has focus and nothing is being typed.
                if (!e.IsRepeat && !_spacePtt)
                {
                    _spacePtt = true;
                    _vm.PttStart();
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>Esc: silence, then close settings, then clear the compact prompt. Returns whether it did anything.</summary>
    private bool Escape()
    {
        if (_vm.AlarmActive || _vm.Playback.IsPlaying)
        {
            _vm.Dismiss();
            return true;
        }
        if (_vm.SettingsOpen)
        {
            _vm.SettingsOpen = false;
            return true;
        }
        if (!_vm.PromptActive || _vm.IsExpanded) return false;
        _vm.PromptText = "";
        _vm.PromptActive = false;
        Focus();
        return true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_spacePtt) return;
        EndSpacePtt();
        e.Handled = true;
    }

    private void EndSpacePtt()
    {
        if (!_spacePtt) return;
        _spacePtt = false;
        _ = _vm.PttEnd();
    }

    /// <summary>Typing any printable key opens the prompt (compact) or focuses it (expanded).</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (TypingSomewhere || _vm.SettingsOpen || string.IsNullOrEmpty(e.Text) || e.Text == " " || char.IsControl(e.Text[0])) return;
        var box = _vm.IsExpanded ? ExpandedPrompt : CompactPrompt;
        _vm.PromptActive = true;
        _vm.PromptText += e.Text;
        e.Handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void Prompt_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _vm.SendPromptCommand.Execute(null);
                if (!_vm.IsExpanded) Focus();
                e.Handled = true;
                break;
            case Key.Escape when !_vm.AlarmActive && !_vm.Playback.IsPlaying:
                _vm.PromptText = "";
                _vm.PromptActive = _vm.IsExpanded;
                Focus();
                e.Handled = true;
                break;
        }
    }

    private void Timer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not Button button) return;
        button.Command?.Execute(button.CommandParameter);
        e.Handled = true;
    }

    #endregion

    #region Settings navigation

    private void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        int tabStep = e.Key switch
        {
            Key.PageDown => 1,
            Key.PageUp => -1,
            Key.Right when ctrl => 1,
            Key.Left when ctrl => -1,
            Key.Tab when ctrl => shift ? -1 : 1,
            Key.Right when Keyboard.FocusedElement is RadioButton => 1,
            Key.Left when Keyboard.FocusedElement is RadioButton => -1,
            _ => 0,
        };
        if (tabStep != 0)
        {
            _vm.Settings.NextTab(tabStep);
            e.Handled = true;
            return;
        }
        // Up/Down walk the rows, except inside the multi-line editor.
        if (e.Key is not (Key.Up or Key.Down) || Keyboard.FocusedElement is not UIElement element || element == Editor || element is ListBoxItem) return;
        element.MoveFocus(new TraversalRequest(e.Key == Key.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
        e.Handled = true;
    }

    private void FocusFirstInTab() => Dispatcher.BeginInvoke(() =>
    {
        TabStrip.Children.OfType<RadioButton>().ElementAtOrDefault(_vm.Settings.TabIndex)?.Focus();
    }, DispatcherPriority.Loaded);

    private void RowEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ConfigRow row }) return;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.Settings.CommitRowCommand.Execute(row);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Settings.CancelRowCommand.Execute(row);
                e.Handled = true;
                break;
        }
    }

    private void RowEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password != _vm.Settings.AccessKey) _vm.Settings.AccessKey = KeyBox.Password;
    }

    #endregion

    #region FILES editor

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        int lines = Math.Max(1, Editor.LineCount);
        LineNumbers.Text = string.Join('\n', Enumerable.Range(1, lines));
    }

    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e) => LineNumberScroll.ScrollToVerticalOffset(e.VerticalOffset);

    #endregion

    #region Footer

    private void Mic_Click(object sender, MouseButtonEventArgs e) => _ = _vm.MicClicked();

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.NudgeVolume(Math.Sign(e.Delta));
        e.Handled = true;
    }

    private void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        PttButton.CaptureMouse();
        _vm.PttStart();
        e.Handled = true;
    }

    private void Ptt_Up(object sender, MouseButtonEventArgs e)
    {
        PttButton.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Ptt_Lost(object sender, MouseEventArgs e) => _ = _vm.PttEnd();

    #endregion
}
