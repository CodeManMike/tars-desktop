using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TarsClient.Native;
using TarsClient.ViewModels;

namespace TarsClient.Views;

public partial class MainWindow : Window
{
    const double ExpandThreshold = 240;     // taller than this = expanded terminal

    const string BannerArt =
        " _____        _        ____      ____  \n" +
        "|_   _|      / \\      |  _ \\    / ___| \n" +
        "  | |       / _ \\     | |_) |   \\___ \\ \n" +
        "  | |   _  / ___ \\  _ |  _ <  _  ___) |\n" +
        "  |_|  (_)/_/   \\_\\(_)|_| \\_\\(_)|____/ ";

    readonly MainViewModel _vm;
    readonly SolidColorBrush _cursorBrush = new(Color.FromRgb(0xFF, 0xB3, 0x47));
    IntPtr _hwnd;
    bool _applyingBounds;

    public bool AllowClose { get; set; }

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
        vm.AppearanceChanged += ApplyWindowOptions;

        ApplyBounds(vm.IsExpanded);
        ApplyFont();
        ApplyGlow();

        vm.PropertyChanged += OnVmChanged;
        vm.Lines.CollectionChanged += OnLinesChanged;
        vm.Settings.LogLines.CollectionChanged += (_, _) => LogScroll.ScrollToEnd();
        vm.ShowRequested += alarm => ShowFromTray(alarm);
        vm.AlarmStopped += () => { if (_hwnd != IntPtr.Zero) Win32.Flash(_hwnd, false); };
        vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ConfirmVisible) && vm.Settings.ConfirmVisible)
                Dispatcher.BeginInvoke(() => ConfirmYes.Focus());
            if (e.PropertyName == nameof(SettingsViewModel.TabIndex)) FocusFirstInTab();
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PowerOnAnimation();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        StateChanged += (_, _) => UpdateVisibility();
        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) => { SaveBounds(); KeepReplyAtEnd(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewTextInput += OnPreviewTextInput;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += (_, _) => { if (_spacePtt) { _spacePtt = false; _ = _vm.PttEnd(); } };
    }

    // ================================================================== window options

    void ApplyWindowOptions()
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
    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.AlarmActive) { _vm.Dismiss(); return; }
        if (!_vm.S.NoActivate || IsActive) return;
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBox or PasswordBox or ListBox or Controls.BlockSlider or Controls.ChoiceRow || d == Scrollback || d == ReplyScroll)
            {
                Activate();
                return;
            }
        }
        if (_vm.SettingsOpen) Activate();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SettingsOpen) { _vm.SettingsOpen = false; return; }
        Activate();
        OpenSettings();
    }

    // ================================================================== native bits

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32.DisableRoundedCorners(_hwnd);
        Win32.SetNoActivate(_hwnd, _vm.S.NoActivate);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    const int WM_EXITSIZEMOVE = 0x0232;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
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

    void PowerOnAnimation()
    {
        // CRT power-on: a bright line opens vertically into the picture.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PowerOn.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.01, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        PowerOn.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        Screen.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320)));
    }

    // ================================================================== visibility → resource use

    void UpdateVisibility()
    {
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        _vm.SetVisible(visible);
        if (visible)
        {
            var blink = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1060) };
            blink.KeyFrames.Add(new DiscreteColorKeyFrame(Color.FromRgb(0xFF, 0xB3, 0x47), KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blink.KeyFrames.Add(new DiscreteColorKeyFrame(Colors.Transparent, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(530))));
            _cursorBrush.BeginAnimation(SolidColorBrush.ColorProperty, blink);
        }
        else _cursorBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);   // hidden: zero animation frames
    }

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

    public void ToggleVisible()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowFromTray();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;      // [×] and Alt+F4 hide to the tray; Quit lives in the tray menu
            Hide();
        }
        base.OnClosing(e);
    }

    // ================================================================== layout + bounds

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsExpanded):
                ApplyFont();
                if (_vm.IsExpanded) Dispatcher.BeginInvoke(() => { Scrollback.ScrollToEnd(); ExpandedPrompt.Focus(); });
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
            case nameof(MainViewModel.SettingsOpen):
                if (_vm.SettingsOpen)
                {
                    if (KeyBox.Password != _vm.Settings.AccessKey) KeyBox.Password = _vm.Settings.AccessKey;
                    FocusFirstInTab();
                }
                else Dispatcher.BeginInvoke(() => { Scrollback.ScrollToEnd(); ExpandedPrompt.Focus(); });
                break;
            case nameof(MainViewModel.Topmost):
                Topmost = _vm.Topmost;
                break;
        }
    }

    void ApplyFont() => FontSize = _vm.BaseFontSize + (_vm.IsExpanded ? 2 : 0);

    void ApplyGlow() => Screen.Effect = _vm.Glow ? (System.Windows.Media.Effects.Effect)FindResource("Glow") : null;

    void KeepReplyAtEnd() => Dispatcher.BeginInvoke(() => ReplyScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

    void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_vm.IsExpanded || !_vm.IsVisible) return;
        // Follow the tail unless the user scrolled up to read.
        if (Scrollback.VerticalOffset >= Scrollback.ScrollableHeight - 40)
            Dispatcher.BeginInvoke(() => Scrollback.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    void ToggleLayout()
    {
        SaveBounds();
        _vm.IsExpanded = !_vm.IsExpanded;
        _vm.S.Layout = _vm.IsExpanded ? "expanded" : "compact";
        ApplyBounds(_vm.IsExpanded);
        _vm.Store.Save();
    }

    public void OpenSettings()
    {
        if (!_vm.IsExpanded) ToggleLayout();
        _vm.SettingsOpen = true;
    }

    void AutoLayoutAfterResize()
    {
        // Dragging a compact strip taller turns it into the terminal (and back).
        bool wantExpanded = ActualHeight >= ExpandThreshold;
        if (wantExpanded == _vm.IsExpanded) return;
        _vm.IsExpanded = wantExpanded;
        _vm.S.Layout = wantExpanded ? "expanded" : "compact";
        SaveBounds();
        _vm.Store.Save();
    }

    void ApplyBounds(bool expanded)
    {
        var b = expanded ? _vm.S.Bounds.Expanded : _vm.S.Bounds.Compact;
        if (b is not { Length: 4 }) return;
        double w = Math.Max(MinWidth, b[2]), h = Math.Max(MinHeight, b[3]);
        if (expanded) h = Math.Max(h, ExpandThreshold);
        else h = Math.Min(h, ExpandThreshold - 1);
        double x = b[0], y = b[1];
        // Clamp to the visible desktop (a monitor may have been unplugged since last run).
        var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
        if (x + w < vl + 60 || x > vl + vw - 60 || y < vt - 10 || y > vt + vh - 40)
        {
            var wa = SystemParameters.WorkArea;
            x = wa.Right - w - 20;
            y = wa.Top + 20;
        }
        _applyingBounds = true;
        Left = x; Top = y; Width = w; Height = h;
        _applyingBounds = false;
    }

    void SaveBounds()
    {
        if (_applyingBounds || WindowState != WindowState.Normal || !IsLoaded) return;
        double[] b = [Math.Round(Left), Math.Round(Top), Math.Round(ActualWidth), Math.Round(ActualHeight)];
        if (_vm.IsExpanded) _vm.S.Bounds.Expanded = b; else _vm.S.Bounds.Compact = b;
        _vm.Store.Save();
    }

    // ================================================================== title bar buttons

    void Layout_Click(object sender, RoutedEventArgs e) => ToggleLayout();
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    // ================================================================== keyboard

    bool _spacePtt;

    bool TypingSomewhere => Keyboard.FocusedElement is TextBox or PasswordBox;

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Settings.ConfirmVisible) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (_vm.AlarmActive || _vm.Playback.IsPlaying) { _vm.Dismiss(); e.Handled = true; }
                else if (_vm.SettingsOpen) { _vm.SettingsOpen = false; e.Handled = true; }
                else if (_vm.PromptActive && !_vm.IsExpanded) { _vm.PromptText = ""; _vm.PromptActive = false; Focus(); e.Handled = true; }
                break;
            case Key.F2:
                if (_vm.SettingsOpen) _vm.SettingsOpen = false; else { Activate(); OpenSettings(); }
                e.Handled = true;
                break;
            case Key.Space when !TypingSomewhere && !_vm.SettingsOpen:
                if (!e.IsRepeat && !_spacePtt) { _spacePtt = true; _vm.PttStart(); }
                e.Handled = true;
                break;
        }
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spacePtt)
        {
            _spacePtt = false;
            _ = _vm.PttEnd();
            e.Handled = true;
        }
    }

    /// <summary>Typing any printable key opens the prompt (compact) or focuses it (expanded).</summary>
    void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (TypingSomewhere || _vm.SettingsOpen || string.IsNullOrEmpty(e.Text) || e.Text == " " || char.IsControl(e.Text[0])) return;
        var box = _vm.IsExpanded ? ExpandedPrompt : CompactPrompt;
        _vm.PromptActive = true;
        _vm.PromptText += e.Text;
        e.Handled = true;
        Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void Prompt_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SendPromptCommand.Execute(null);
            if (!_vm.IsExpanded) Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !_vm.AlarmActive && !_vm.Playback.IsPlaying)
        {
            _vm.PromptText = "";
            _vm.PromptActive = _vm.IsExpanded;
            Focus();
            e.Handled = true;
        }
    }

    void Timer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && sender is Button b)
        {
            b.Command?.Execute(b.CommandParameter);
            e.Handled = true;
        }
    }

    // ================================================================== settings navigation

    void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var s = _vm.Settings;
        if ((ctrl && e.Key == Key.Right) || e.Key == Key.PageDown || (ctrl && e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == 0))
        { s.NextTab(1); e.Handled = true; return; }
        if ((ctrl && e.Key == Key.Left) || e.Key == Key.PageUp || (ctrl && e.Key == Key.Tab))
        { s.NextTab(-1); e.Handled = true; return; }

        if (Keyboard.FocusedElement is RadioButton && e.Key is Key.Left or Key.Right)
        { s.NextTab(e.Key == Key.Right ? 1 : -1); e.Handled = true; return; }

        // Up/Down walk the rows, except inside the multi-line editor.
        if (e.Key is Key.Up or Key.Down && Keyboard.FocusedElement is UIElement el && el != Editor && el is not ListBoxItem)
        {
            el.MoveFocus(new TraversalRequest(e.Key == Key.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
            e.Handled = true;
        }
    }

    void FocusFirstInTab() => Dispatcher.BeginInvoke(() =>
    {
        var radio = TabStrip.Children.OfType<RadioButton>().ElementAtOrDefault(_vm.Settings.TabIndex);
        radio?.Focus();
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    void RowEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ConfigRow row }) return;
        if (e.Key == Key.Enter) { _vm.Settings.CommitRowCommand.Execute(row); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.Settings.CancelRowCommand.Execute(row); e.Handled = true; }
    }

    void RowEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible) Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); });
    }

    void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password != _vm.Settings.AccessKey) _vm.Settings.AccessKey = KeyBox.Password;
    }

    // ================================================================== FILES editor line numbers

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        int n = Math.Max(1, Editor.LineCount);
        var sb = new System.Text.StringBuilder(n * 4);
        for (int i = 1; i <= n; i++) sb.Append(i).Append('\n');
        LineNumbers.Text = sb.ToString(0, sb.Length - 1);
    }

    void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e) => LineNumberScroll.ScrollToVerticalOffset(e.VerticalOffset);

    // ================================================================== footer

    void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.NudgeVolume(Math.Sign(e.Delta));
        e.Handled = true;
    }

    void Ptt_Down(object sender, MouseButtonEventArgs e)
    {
        PttButton.CaptureMouse();
        _vm.PttStart();
        e.Handled = true;
    }

    void Ptt_Up(object sender, MouseButtonEventArgs e)
    {
        PttButton.ReleaseMouseCapture();
        e.Handled = true;
    }

    void Ptt_Lost(object sender, MouseEventArgs e) => _ = _vm.PttEnd();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        KeyBox.Password = _vm.Settings.AccessKey;
    }
}
