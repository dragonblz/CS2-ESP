using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FoxSense.Core;
using FoxSense.Features;
using FoxSense.Game;
using FoxSense.Overlay;

namespace FoxSense.UI;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly Memory _mem = new();
    private readonly GameState _state;
    private readonly SoftAim _softAim = new();
    private readonly EspSettings _espSettings = new();
    private readonly AimSettings _aimSettings = new();
    private readonly SkinChanger _skinChanger = new();
    private readonly SkinDatabase _skinDb = new();

    private OverlayWindow? _overlay;
    private Thread? _gameThread;
    private Thread? _aimThread;
    private volatile bool _running = true;
    private bool _isBindingAimKey;
    private bool _isBindingGuiKey;
    private int _guiToggleKey = 0xA4; // VK_LMENU
    private bool _guiVisible = true;
    private string _activeTab = "visuals";

    // FPS counter
    private int _frameCount;
    private DateTime _lastFpsTime = DateTime.UtcNow;
    private int _currentFps;

    public MainWindow()
    {
        InitializeComponent();
        _state = new GameState(_mem);
        Loaded += OnLoaded;
    }

    // ═══════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetActiveTab("visuals");
        SetupSliders();
        StartOverlay();
        StartGameThread();
        StartAimThread();
        StartRenderLoop();
        StartGuiToggleLoop();
        ConnectToGame();
        InitSkinTab();
        DarkenAllComboBoxes();
    }

    /// <summary>
    /// Force all ComboBox popups to have dark backgrounds.
    /// WPF's default ComboBox template uses SystemDropShadowChrome with a white border
    /// that cannot be styled via implicit styles alone.
    /// </summary>
    private void DarkenAllComboBoxes()
    {
        foreach (var cb in FindVisualChildren<ComboBox>(this))
        {
            cb.DropDownOpened += (s, _) =>
            {
                if (s is ComboBox combo)
                {
                    var popup = combo.Template?.FindName("PART_Popup", combo) as Popup;
                    if (popup?.Child is FrameworkElement fe)
                    {
                        // Try to find the border inside the popup
                        foreach (var border in FindVisualChildren<System.Windows.Controls.Border>(fe))
                        {
                            border.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E));
                            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x50));
                        }
                    }
                }
            };
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var grandchild in FindVisualChildren<T>(child))
                yield return grandchild;
        }
    }

    private void SetupSliders()
    {
        sliderFov.ValueChanged += (_, ev) => txtFovValue.Text = ((int)ev.NewValue).ToString();
        sliderSmooth.ValueChanged += (_, ev) => txtSmoothValue.Text = ((int)ev.NewValue).ToString();
    }

    private void StartOverlay()
    {
        _overlay = new OverlayWindow();
        _overlay.Show();
    }

    private async void ConnectToGame()
    {
        while (_running)
        {
            if (!_mem.IsAttached)
            {
                bool ok = _mem.Attach();
                Dispatcher.Invoke(() =>
                    txtStatus.Text = ok ? "🟡 CS2 found — waiting for match..." : "⏳ Waiting for CS2...");
            }
            await Task.Delay(1500);
        }
    }

    // ═══════════════════════════════════════════════════
    //  GAME READ THREAD (~120 Hz)
    // ═══════════════════════════════════════════════════

    private void StartGameThread()
    {
        _gameThread = new Thread(GameLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _gameThread.Start();
    }

    private void GameLoop()
    {
        int screenW = (int)SystemParameters.PrimaryScreenWidth;
        int screenH = (int)SystemParameters.PrimaryScreenHeight;

        while (_running)
        {
            Thread.Sleep(1); // Maximum refresh — matches game tick
            if (!_mem.IsAttached) continue;

            _state.Update(screenW, screenH);

            // Skin changer tick (only writes when needed)
            try { _skinChanger.Tick(_mem, _state); }
            catch { /* Swallow write errors during transitions */ }
        }
    }

    // ═══════════════════════════════════════════════════
    //  AIMBOT THREAD (~500 Hz)
    // ═══════════════════════════════════════════════════

    private void StartAimThread()
    {
        _aimThread = new Thread(AimLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _aimThread.Start();
    }

    private void AimLoop()
    {
        int screenW = (int)SystemParameters.PrimaryScreenWidth;
        int screenH = (int)SystemParameters.PrimaryScreenHeight;

        while (_running)
        {
            Thread.Sleep(2); // ~500 Hz
            if (!_mem.IsAttached || !_state.InGame) continue;

            _softAim.Tick(_state, _aimSettings, screenW, screenH);
        }
    }

    // ═══════════════════════════════════════════════════
    //  RENDER LOOP (CompositionTarget — vsync locked)
    // ═══════════════════════════════════════════════════

    private void StartRenderLoop()
    {
        CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (_overlay == null || !_mem.IsAttached) return;

        // Sync settings from UI
        SyncSettings();

        int screenW = (int)SystemParameters.PrimaryScreenWidth;
        int screenH = (int)SystemParameters.PrimaryScreenHeight;

        using var dc = _overlay.BeginFrame();

        if (_espSettings.Enabled && _state.InGame)
        {
            var players = _state.GetPlayers();
            EspRenderer.Draw(dc, players, _espSettings, screenW, screenH, _state.LocalPosition, _state.LocalTeam);

            if (_aimSettings.Enabled && btnShowFov.IsChecked == true)
                EspRenderer.DrawFovCircle(dc, _aimSettings.Fov, screenW, screenH, _espSettings.Color);

            UpdateFps();
            int enemies = players.Count(p => p.Team != _state.LocalTeam);
            Dispatcher.BeginInvoke(() =>
                txtStatus.Text = $"🟢 Players: {players.Count} | Enemies: {enemies} | FPS: {_currentFps}");
        }
        else if (_mem.IsAttached && !_state.InGame)
        {
            Dispatcher.BeginInvoke(() => txtStatus.Text = "🟡 CS2 attached — not in game");
        }
    }

    private void SyncSettings()
    {
        // ESP
        _espSettings.Enabled = btnEspToggle.IsChecked == true;
        _espSettings.Box = btnBox.IsChecked == true;
        _espSettings.Skeleton = btnSkeleton.IsChecked == true;
        _espSettings.HealthBar = btnHealthBar.IsChecked == true;
        _espSettings.Names = btnNames.IsChecked == true;
        _espSettings.Distance = btnDistance.IsChecked == true;
        _espSettings.SnapLines = btnSnapLines.IsChecked == true;
        _espSettings.EnemyOnly = btnEnemyOnly.IsChecked == true;
        _espSettings.Color = Color.FromRgb(
            (byte)sliderR.Value, (byte)sliderG.Value, (byte)sliderB.Value);

        // Aimbot
        _aimSettings.Enabled = btnAimToggle.IsChecked == true;
        _aimSettings.Fov = (float)sliderFov.Value;
        _aimSettings.Smooth = (float)sliderSmooth.Value;
        _aimSettings.EnemyOnly = btnAimEnemyOnly.IsChecked == true;
        _aimSettings.BoneTarget = comboBone.SelectedIndex switch
        {
            1 => BoneTarget.Neck,
            2 => BoneTarget.Chest,
            _ => BoneTarget.Head,
        };
    }

    private void UpdateFps()
    {
        _frameCount++;
        var now = DateTime.UtcNow;
        if ((now - _lastFpsTime).TotalSeconds >= 1.0)
        {
            _currentFps = _frameCount;
            _frameCount = 0;
            _lastFpsTime = now;
        }
    }

    // ═══════════════════════════════════════════════════
    //  TAB SYSTEM
    // ═══════════════════════════════════════════════════

    private void SetActiveTab(string tab)
    {
        _activeTab = tab;
        panelVisuals.Visibility = tab == "visuals" ? Visibility.Visible : Visibility.Collapsed;
        panelAimbot.Visibility = tab == "aimbot" ? Visibility.Visible : Visibility.Collapsed;
        panelSkins.Visibility = tab == "skins" ? Visibility.Visible : Visibility.Collapsed;
        panelSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;

        var white = Brushes.White;
        var gray = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

        btnNavEsp.Foreground = tab == "visuals" ? white : gray;
        btnNavAimbot.Foreground = tab == "aimbot" ? white : gray;
        btnNavSkins.Foreground = tab == "skins" ? white : gray;
        btnNavSettings.Foreground = tab == "settings" ? white : gray;
    }

    private void BtnTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t) SetActiveTab(t);
    }

    // ═══════════════════════════════════════════════════
    //  EVENT HANDLERS
    // ═══════════════════════════════════════════════════

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isBindingAimKey)
        {
            int vk = e.ChangedButton switch
            {
                MouseButton.Left => 0x01,
                MouseButton.Right => 0x02,
                MouseButton.Middle => 0x04,
                MouseButton.XButton1 => 0x05,
                MouseButton.XButton2 => 0x06,
                _ => 0
            };
            if (vk != 0)
            {
                _aimSettings.AimKey = vk;
                btnKeyBind.Content = $"🎯 {e.ChangedButton}";
                _isBindingAimKey = false;
                e.Handled = true;
                return;
            }
        }
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnEsp_Checked(object sender, RoutedEventArgs e) => btnEspToggle.Content = "ESP: ON";
    private void BtnEsp_Unchecked(object sender, RoutedEventArgs e) => btnEspToggle.Content = "ESP: OFF";
    private void BtnAim_Checked(object sender, RoutedEventArgs e) => btnAimToggle.Content = "Aimbot: ON";
    private void BtnAim_Unchecked(object sender, RoutedEventArgs e) => btnAimToggle.Content = "Aimbot: OFF";
    private void BtnEnemyOnly_Checked(object sender, RoutedEventArgs e) => btnEnemyOnly.Content = "Enemies Only";
    private void BtnEnemyOnly_Unchecked(object sender, RoutedEventArgs e) => btnEnemyOnly.Content = "All Players";
    private void BtnSkin_Checked(object sender, RoutedEventArgs e) { btnSkinToggle.Content = "Skin Changer: ON"; _skinChanger.Enabled = true; }
    private void BtnSkin_Unchecked(object sender, RoutedEventArgs e) { btnSkinToggle.Content = "Skin Changer: OFF"; _skinChanger.Enabled = false; }

    // ═══════════════════════════════════════════════════
    //  SKIN TAB LOGIC
    // ═══════════════════════════════════════════════════

    private List<SkinDatabase.SkinInfo> _currentWeaponSkins = new();

    private async void InitSkinTab()
    {
        // Auto-update offsets first
        try { await OffsetUpdater.UpdateAsync(); }
        catch { /* Non-critical */ }

        // Load skin database
        await _skinDb.LoadAsync();

        if (!_skinDb.IsLoaded) return;

        Dispatcher.Invoke(() =>
        {
            // Populate weapon dropdown
            comboWeapon.Items.Clear();
            comboWeapon.Items.Add(new ComboBoxItem { Content = "-- Select Weapon --" });
            foreach (string name in _skinDb.GetAvailableWeapons())
                comboWeapon.Items.Add(new ComboBoxItem { Content = name });
            comboWeapon.SelectedIndex = 0;
        });
    }

    private void ComboWeapon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (comboWeapon.SelectedIndex <= 0) { listSkins.ItemsSource = null; return; }
        var item = comboWeapon.SelectedItem as ComboBoxItem;
        string weaponName = item?.Content?.ToString() ?? "";
        if (!SkinDatabase.WeaponNameToDefIndex.TryGetValue(weaponName, out ushort defIdx)) return;

        _currentWeaponSkins = _skinDb.GetSkinsForWeapon(defIdx);
        listSkins.ItemsSource = _currentWeaponSkins.Select(s => new SkinDisplayItem
        {
            Name = s.Name,
            ImageUrl = s.ImageUrl,
            Rarity = s.Rarity,
            Skin = s
        }).ToList();
    }

    private void ListSkins_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (listSkins.SelectedItem is not SkinDisplayItem display) return;
        var skin = display.Skin;
        _skinChanger.SetWeaponSkin(skin.WeaponDefIndex, skin);
    }

    private void ComboKnife_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ComboKnifeSkin_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ComboGloveType_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ComboGloveSkin_Changed(object sender, SelectionChangedEventArgs e) { }

    private void BtnForceRefresh_Click(object sender, RoutedEventArgs e)
    {
        _skinChanger.ForceUpdate = true;
    }

    private void BtnKeyBind_Click(object sender, RoutedEventArgs e)
    {
        _isBindingAimKey = true;
        btnKeyBind.Content = "⌛ Press any key/button...";
    }

    private void BtnGuiKey_Click(object sender, RoutedEventArgs e)
    {
        _isBindingGuiKey = true;
        btnGuiKey.Content = "⌛ Press any key...";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_isBindingAimKey || _isBindingGuiKey)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);

            if (_isBindingAimKey)
            {
                _aimSettings.AimKey = vk;
                btnKeyBind.Content = $"🎯 {key}";
                _isBindingAimKey = false;
            }
            else if (_isBindingGuiKey)
            {
                _guiToggleKey = vk;
                btnGuiKey.Content = $"🎮 {key}";
                _isBindingGuiKey = false;
            }
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ═══════════════════════════════════════════════════
    //  GUI TOGGLE (background polling)
    // ═══════════════════════════════════════════════════

    private async void StartGuiToggleLoop()
    {
        bool wasPressed = false;
        while (_running)
        {
            await Task.Delay(50);
            bool pressed = (GetAsyncKeyState(_guiToggleKey) & 0x8000) != 0;
            if (pressed && !wasPressed)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_guiVisible) { WindowState = WindowState.Minimized; _guiVisible = false; }
                    else { WindowState = WindowState.Normal; Activate(); _guiVisible = true; }
                });
            }
            wasPressed = pressed;
        }
    }

    // ═══════════════════════════════════════════════════
    //  SHUTDOWN
    // ═══════════════════════════════════════════════════

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _running = false;
        _overlay?.Close();
        _mem.Dispose();
        base.OnClosing(e);
    }
}

/// <summary>
/// Display model for the skin preview list.
/// WPF binds to these properties for image + name + rarity.
/// </summary>
public class SkinDisplayItem
{
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int Rarity { get; set; }
    public SkinDatabase.SkinInfo Skin { get; set; } = null!;

    public string RarityText => Rarity switch
    {
        7 => "★★★★★★★ Contraband",
        6 => "★★★★★★ Covert",
        5 => "★★★★★ Classified",
        4 => "★★★★ Restricted",
        3 => "★★★ Mil-Spec",
        2 => "★★ Industrial",
        _ => "★ Consumer"
    };

    public string RarityColor => Rarity switch
    {
        7 => "#FFD700",  // Gold
        6 => "#EB4B4B",  // Red
        5 => "#D32CE6",  // Purple
        4 => "#4B69FF",  // Blue
        3 => "#5E98D9",  // Light blue
        2 => "#B0C3D9",  // Silver
        _ => "#666666"
    };
}
