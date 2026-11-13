using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SecondScreen.Core.Diagnostics;
using SecondScreen.Display;
using SecondScreen.Network;

namespace SecondScreen.App;

public sealed class ServerDashboardForm : Form
{
    public enum AppLanguage
    {
        English,
        Turkish
    }

    private readonly SecondScreenServer _server = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private AppLanguage _currentLanguage = AppLanguage.English;

    // UI Controls
    private Button _btnTabEn = null!;
    private Button _btnTabTr = null!;
    private Label _lblSubtitle = null!;
    private Label _lblStatusBadge = null!;
    private Button _btnToggleServer = null!;
    private Button _btnExtendDesktop = null!;
    private Button _btnStopwatch = null!;
    
    // Cards
    private Label _lblConnCardTitle = null!;
    private Label _lblDispCardTitle = null!;
    private Label _lblTelemetryTitle = null!;
    private Label _lblLogsTitle = null!;

    // Info Labels & Action Buttons
    private Label _lblUsbStatus = null!;
    private Label _lblWifiIp = null!;
    private Label _lblHotspotStatus = null!;
    private Label _lblPinCode = null!;
    private CheckBox _chkRequirePin = null!;
    private Button _btnRegeneratePin = null!;
    private Button _btnRefreshUsb = null!;
    private Button _btnHotspotSettings = null!;

    private Label _lblVirtualDisplayStatus = null!;
    private Button _btnInstallDriver = null!;
    private Button _btnUninstallDriver = null!;
    private Button _btnSingleScreen = null!;

    private Label _lblSessionClient = null!;
    private Label _lblSessionStats = null!;
    private RichTextBox _rtbLogs = null!;
    private System.Windows.Forms.Timer _metricsTimer = null!;

    // Tray Menu Items
    private ToolStripMenuItem _itemTitle = null!;
    private ToolStripMenuItem _itemShow = null!;
    private ToolStripMenuItem _itemToggleServer = null!;
    private ToolStripMenuItem _itemExtend = null!;
    private ToolStripMenuItem _itemExit = null!;

    public ServerDashboardForm()
    {
        InitializeCustomForm();
        SetupSystemTray();
        HookServerEvents();
        SetLanguage(AppLanguage.English); // English by default
    }

    private void InitializeCustomForm()
    {
        Text = "Areas — SecondScreen Server (v1.0.4)";
        Size = new Size(950, 790);
        MinimumSize = new Size(920, 750);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 26);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        string? icoPath = ResolveAssetPath("app.ico", "areas.ico");
        if (icoPath != null) Icon = new Icon(icoPath);

        // Main Layout Container
        var pnlMain = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Color.Transparent
        };
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));  // 0: Header
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // 1: Language Tabs Bar
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // 2: Controls Bar
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 245)); // 3: Middle Cards (Connectivity & Driver)
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 125)); // 4: Live Session Telemetry (Protected height)
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 5: Event Logs (Fills remaining height)

        // 0. Header (Logo, Titles, Status Badge)
        var pnlHeader = new Panel { Dock = DockStyle.Fill };
        
        string? logoPath = ResolveAssetPath(Path.Combine("assets", "logo.png"), "logo.png");
        if (logoPath != null)
        {
            var pbLogo = new PictureBox
            {
                Image = Image.FromFile(logoPath),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(50, 50),
                Location = new Point(0, 6)
            };
            pnlHeader.Controls.Add(pbLogo);
        }

        var lblTitle = new Label
        {
            Text = "Areas Server",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(58, 6),
            AutoSize = true
        };
        pnlHeader.Controls.Add(lblTitle);

        _lblSubtitle = new Label
        {
            Text = "Second monitor solution for Windows using Android tablets.",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 150, 165),
            Location = new Point(60, 36),
            AutoSize = true,
            UseMnemonic = false
        };
        pnlHeader.Controls.Add(_lblSubtitle);

        _lblStatusBadge = new Label
        {
            Text = "● Listening (Port 27183)",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(34, 197, 94),
            BackColor = Color.FromArgb(30, 41, 59),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            MinimumSize = new Size(210, 34),
            Padding = new Padding(12, 6, 12, 6),
            Dock = DockStyle.Right,
            Margin = new Padding(0, 10, 0, 10),
            UseMnemonic = false
        };
        pnlHeader.Controls.Add(_lblStatusBadge);
        pnlMain.Controls.Add(pnlHeader, 0, 0);

        // 1. Language Tabs Bar
        var pnlTabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 4)
        };

        _btnTabEn = CreateTabButton("English", true, (_, _) => SetLanguage(AppLanguage.English));
        _btnTabTr = CreateTabButton("Türkçe", false, (_, _) => SetLanguage(AppLanguage.Turkish));
        pnlTabs.Controls.Add(_btnTabEn);
        pnlTabs.Controls.Add(_btnTabTr);
        pnlMain.Controls.Add(pnlTabs, 0, 1);

        // 2. Control Bar (Start/Stop, Extend, Stopwatch)
        var pnlControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 6)
        };

        _btnToggleServer = CreateButton("⏹ Stop Server", Color.FromArgb(239, 68, 68), Color.White);
        _btnToggleServer.Click += async (_, _) => await ToggleServerAsync();
        pnlControls.Controls.Add(_btnToggleServer);

        _btnExtendDesktop = CreateButton("🖥️ Extend Desktop", Color.FromArgb(39, 39, 46), Color.White);
        _btnExtendDesktop.Click += async (_, _) =>
        {
            try
            {
                DisplayTopologyManager.ExtendDesktop();
                AddLog(_currentLanguage == AppLanguage.English ? "[+] Desktop extended (Extend mode)." : "[+] Masaüstü genişletildi (Extend).");
                await Task.Delay(500);
                UpdateDisplayInfo();
            }
            catch (Exception ex)
            {
                AddLog($"[!] Error: {ex.Message}");
            }
        };
        pnlControls.Controls.Add(_btnExtendDesktop);

        _btnStopwatch = CreateButton("⏱️ Stopwatch (Latency Test)", Color.FromArgb(39, 39, 46), Color.White);
        _btnStopwatch.Click += (_, _) =>
        {
            LatencyStopwatchForm.ShowInThread();
            AddLog(_currentLanguage == AppLanguage.English ? "[+] Latency test stopwatch opened." : "[+] Gecikme test kronometresi açıldı.");
        };
        pnlControls.Controls.Add(_btnStopwatch);

        pnlMain.Controls.Add(pnlControls, 0, 2);

        // 3. Middle Cards (Connectivity & Display Driver)
        var pnlMiddle = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        pnlMiddle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pnlMiddle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // Left Card: Connectivity
        var pnlConnCard = CreateCardPanel("🌐 Connection Channels", out _lblConnCardTitle);
        _lblUsbStatus = CreateInfoLabel("• USB-C Cable (adb reverse): Checking...");
        _lblWifiIp = CreateInfoLabel("• Wi-Fi LAN IP: 192.168.1.212:27183");
        _lblHotspotStatus = CreateInfoLabel("• Mobile Hotspot: Checking...");
        _lblPinCode = new Label
        {
            Text = $"• Security PIN: {_server.CurrentPin} (TLS Encrypted)",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(34, 197, 94),
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2)
        };
        _chkRequirePin = new CheckBox
        {
            Text = "Require PIN",
            Checked = _server.RequirePin,
            ForeColor = Color.FromArgb(220, 220, 235),
            AutoSize = true,
            Margin = new Padding(4, 6, 8, 0)
        };
        _chkRequirePin.CheckedChanged += (_, _) => _server.RequirePin = _chkRequirePin.Checked;
        _btnRegeneratePin = CreateSmallButton("New PIN", (_, _) =>
        {
            string newPin = _server.RegeneratePin();
            bool isEn = _currentLanguage == AppLanguage.English;
            _lblPinCode.Text = isEn
                ? $"• Security PIN: {newPin} (TLS Encrypted)"
                : $"• Güvenlik PIN: {newPin} (TLS Şifreli)";
            AddLog(isEn ? $"[+] Security PIN regenerated: {newPin}" : $"[+] Yeni Güvenlik PIN oluşturuldu: {newPin}");
        });

        _btnHotspotSettings = CreateSmallButton("Hotspot Settings", (_, _) => HotspotManager.OpenHotspotSettings());
        _btnRefreshUsb = CreateSmallButton("Refresh USB", async (_, _) => await _server.RefreshUsbTunnelAsync());

        pnlConnCard.Controls.Add(_lblUsbStatus);
        pnlConnCard.Controls.Add(_lblWifiIp);
        pnlConnCard.Controls.Add(_lblHotspotStatus);
        pnlConnCard.Controls.Add(_lblPinCode);

        var pnlConnButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Margin = new Padding(0, 6, 0, 0)
        };
        pnlConnButtons.Controls.Add(_btnRefreshUsb);
        pnlConnButtons.Controls.Add(_btnHotspotSettings);
        pnlConnButtons.Controls.Add(_btnRegeneratePin);
        pnlConnButtons.Controls.Add(_chkRequirePin);
        pnlConnCard.Controls.Add(pnlConnButtons);
        pnlMiddle.Controls.Add(pnlConnCard, 0, 0);

        // Right Card: Display & Driver
        var pnlDispCard = CreateCardPanel("🖥️ Virtual Display Driver (IddCx)", out _lblDispCardTitle);
        _lblVirtualDisplayStatus = CreateInfoLabel("• Virtual Display: Checking...");
        _lblVirtualDisplayStatus.MaximumSize = new Size(390, 0);
        
        _btnInstallDriver = CreateSmallButton("Install Driver", async (_, _) =>
        {
            AddLog(_currentLanguage == AppLanguage.English ? "[+] Installing virtual display driver..." : "[+] Sanal ekran sürücüsü kuruluyor...");
            await SecondScreenServer.InstallVirtualDisplayDriverAsync();
            UpdateDisplayInfo();
        });

        _btnUninstallDriver = CreateSmallButton("Uninstall Driver", async (_, _) =>
        {
            AddLog(_currentLanguage == AppLanguage.English ? "[+] Uninstalling virtual display driver..." : "[+] Sanal ekran sürücüsü kaldırılıyor...");
            await SecondScreenServer.UninstallVirtualDisplayDriverAsync();
            UpdateDisplayInfo();
        });

        _btnSingleScreen = CreateSmallButton("Laptop Screen Only", async (_, _) =>
        {
            try
            {
                DisplayTopologyManager.RestoreInternalOnly();
                AddLog(_currentLanguage == AppLanguage.English ? "[+] Restored single monitor mode." : "[+] Yalnızca birincil ekran moduna dönüldü.");
                await Task.Delay(500);
                UpdateDisplayInfo();
            }
            catch (Exception ex)
            {
                AddLog($"[!] Error: {ex.Message}");
            }
        });

        pnlDispCard.Controls.Add(_lblVirtualDisplayStatus);

        var pnlDispButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Margin = new Padding(0, 8, 0, 0)
        };
        pnlDispButtons.Controls.Add(_btnInstallDriver);
        pnlDispButtons.Controls.Add(_btnUninstallDriver);
        pnlDispButtons.Controls.Add(_btnSingleScreen);
        pnlDispCard.Controls.Add(pnlDispButtons);

        pnlMiddle.Controls.Add(pnlDispCard, 1, 0);
        pnlMain.Controls.Add(pnlMiddle, 0, 3);

        // 4. Live Session Telemetry Card (Dedicated non-clipping Panel)
        var pnlTelemetryCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 36),
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(4)
        };

        _lblTelemetryTitle = new Label
        {
            Text = "📊 Active Session & Live Telemetry",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(167, 139, 250),
            Dock = DockStyle.Top,
            Height = 26,
            UseMnemonic = false
        };

        _lblSessionClient = CreateInfoLabel("• Connected Device: Waiting (No tablet connected)");
        _lblSessionClient.Dock = DockStyle.Top;
        _lblSessionClient.Height = 24;

        _lblSessionStats = CreateInfoLabel("• Stream Specs: 2304x1440 @ 60 FPS | Latency (RTT): -- ms | Bitrate: -- Mbps");
        _lblSessionStats.Dock = DockStyle.Top;
        _lblSessionStats.Height = 38;

        pnlTelemetryCard.Controls.Add(_lblSessionStats);
        pnlTelemetryCard.Controls.Add(_lblSessionClient);
        pnlTelemetryCard.Controls.Add(_lblTelemetryTitle);
        pnlMain.Controls.Add(pnlTelemetryCard, 0, 4);

        // 5. Live Logs Card (Panel container with header and clean inner padding)
        var pnlLogsCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 36),
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(4)
        };

        _lblLogsTitle = new Label
        {
            Text = "📜 Event Logs",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(167, 139, 250),
            Dock = DockStyle.Top,
            Height = 26
        };

        _rtbLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(14, 14, 18),
            ForeColor = Color.FromArgb(200, 200, 215),
            Font = new Font("Consolas", 9f),
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };

        pnlLogsCard.Controls.Add(_rtbLogs);
        pnlLogsCard.Controls.Add(_lblLogsTitle);
        pnlMain.Controls.Add(pnlLogsCard, 0, 5);

        Controls.Add(pnlMain);

        _metricsTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _metricsTimer.Tick += (_, _) => UpdateNetworkInfo();
        _metricsTimer.Start();
    }

    public void SetLanguage(AppLanguage lang)
    {
        _currentLanguage = lang;
        bool isEn = lang == AppLanguage.English;

        // Tabs styling
        _btnTabEn.BackColor = isEn ? Color.FromArgb(99, 102, 241) : Color.FromArgb(32, 32, 42);
        _btnTabEn.ForeColor = isEn ? Color.White : Color.FromArgb(160, 160, 175);
        _btnTabTr.BackColor = !isEn ? Color.FromArgb(99, 102, 241) : Color.FromArgb(32, 32, 42);
        _btnTabTr.ForeColor = !isEn ? Color.White : Color.FromArgb(160, 160, 175);

        // Subtitle
        _lblSubtitle.Text = isEn
            ? "Second monitor solution for Windows using Android tablets."
            : "Android tabletler kullanarak Windows için ikinci monitör çözümü.";

        // Controls
        _btnToggleServer.Text = _server.IsRunning
            ? (isEn ? "⏹ Stop Server" : "⏹ Sunucuyu Durdur")
            : (isEn ? "▶ Start Server" : "▶ Sunucuyu Başlat");
        _btnExtendDesktop.Text = isEn ? "🖥️ Extend Desktop" : "🖥️ Masaüstünü Genişlet (Extend)";
        _btnStopwatch.Text = isEn ? "⏱️ Stopwatch (Latency Test)" : "⏱️ Kronometre (Gecikme Testi)";

        // Card Titles
        _lblConnCardTitle.Text = isEn ? "🌐 Connection Channels" : "🌐 Bağlantı Kanalları";
        _lblDispCardTitle.Text = isEn ? "🖥️ Virtual Display Driver (IddCx)" : "🖥️ Sanal Ekran Sürücüsü (IddCx)";
        _lblTelemetryTitle.Text = isEn ? "📊 Active Session & Live Telemetry" : "📊 Aktif Oturum ve Canlı Telemetri";
        _lblLogsTitle.Text = isEn ? "📜 Event Logs" : "📜 Canlı Olay Günlüğü";

        // Card Buttons
        _btnRefreshUsb.Text = isEn ? "Refresh USB" : "USB Tüneli Yenile";
        _btnHotspotSettings.Text = isEn ? "Hotspot Settings" : "Hotspot Ayarları";
        _btnRegeneratePin.Text = isEn ? "New PIN" : "Yeni PIN";
        _chkRequirePin.Text = isEn ? "Require PIN" : "PIN Zorunlu";
        _lblPinCode.Text = isEn
            ? $"• Security PIN: {_server.CurrentPin} (TLS Encrypted)"
            : $"• Güvenlik PIN: {_server.CurrentPin} (TLS Şifreli)";
        _btnInstallDriver.Text = isEn ? "Install Driver" : "Sürücüyü Kur (IddCx)";
        _btnUninstallDriver.Text = isEn ? "Uninstall Driver" : "Sürücüyü Kaldır";
        _btnSingleScreen.Text = isEn ? "Laptop Screen Only" : "Yalnızca Laptop Ekranı";

        // Tray Menu Items
        if (_itemTitle != null)
        {
            _itemTitle.Text = $"Areas Server (v1.0.4) — {(isEn ? "English" : "Türkçe")}";
            _itemShow.Text = isEn ? "Open Dashboard" : "Arayüzü Aç";
            _itemExtend.Text = isEn ? "Extend Desktop" : "Masaüstünü Genişlet";
            _itemExit.Text = isEn ? "Exit" : "Çıkış";
        }

        // Refresh dynamic info
        UpdateStatusBadge();
        UpdateNetworkInfo();
        UpdateDisplayInfo();
        UpdateSessionDetails();
    }

    private void UpdateStatusBadge()
    {
        bool isEn = _currentLanguage == AppLanguage.English;
        bool isRunning = _server.IsRunning;

        string toggleText = isRunning
            ? (isEn ? "⏹ Stop Server" : "⏹ Sunucuyu Durdur")
            : (isEn ? "▶ Start Server" : "▶ Sunucuyu Başlat");
        _btnToggleServer.Text = toggleText;
        _btnToggleServer.BackColor = isRunning ? Color.FromArgb(239, 68, 68) : Color.FromArgb(37, 99, 235);
        if (_itemToggleServer != null) _itemToggleServer.Text = toggleText;

        if (isRunning)
        {
            if (_server.ConnectedClientName != null)
            {
                _lblStatusBadge.Text = isEn
                    ? $"● Connected: {_server.ConnectedClientName}"
                    : $"● Bağlı: {_server.ConnectedClientName}";
                _lblStatusBadge.ForeColor = Color.FromArgb(59, 130, 246);
            }
            else
            {
                _lblStatusBadge.Text = isEn
                    ? $"● Listening (Port {_server.Port})"
                    : $"● Dinliyor (Port {_server.Port})";
                _lblStatusBadge.ForeColor = Color.FromArgb(34, 197, 94);
            }
        }
        else
        {
            _lblStatusBadge.Text = isEn ? "● Stopped" : "● Durduruldu";
            _lblStatusBadge.ForeColor = Color.FromArgb(239, 68, 68);
        }
    }

    private async Task ToggleServerAsync()
    {
        if (_server.IsRunning)
            _server.Stop();
        else
            await _server.StartAsync();

        UpdateStatusBadge();
    }

    private void UpdateNetworkInfo()
    {
        bool isEn = _currentLanguage == AppLanguage.English;
        var localIps = NetworkHelper.GetLocalIPv4Addresses();
        var mainWifi = localIps.FirstOrDefault(ip => ip != HotspotManager.DefaultWindowsHotspotIp) ?? (isEn ? "Not Connected" : "Bağlı Değil");
        _lblWifiIp.Text = $"• Wi-Fi LAN IP: {mainWifi}:{_server.Port}";

        bool hotspotActive = HotspotManager.IsHotspotActive();
        _lblHotspotStatus.Text = hotspotActive
            ? (isEn ? "• Mobile Hotspot: 192.168.137.1 (Active)" : "• Mobil Etkin Nokta (Hotspot): 192.168.137.1 (Aktif)")
            : (isEn ? "• Mobile Hotspot: Disabled (Enable for Dorm/School)" : "• Mobil Etkin Nokta: Kapalı (Yurt/Okul için açın)");

        _lblUsbStatus.Text = isEn
            ? $"• USB-C Cable (adb reverse): Port {_server.Port} forwarded"
            : $"• USB-C Kablo (adb reverse): Port {_server.Port} yönlendirildi";
    }

    private void UpdateDisplayInfo()
    {
        bool isEn = _currentLanguage == AppLanguage.English;
        var displays = DisplayEnumerator.GetDisplays();
        var secondDisp = displays.FirstOrDefault(d => !d.IsPrimary);

        if (secondDisp != null)
        {
            string dispWord = isEn ? "Second Display" : "İkinci Ekran";
            _lblVirtualDisplayStatus.Text = $"• {dispWord}: {secondDisp.FriendlyName} ({secondDisp.Bounds.Width}x{secondDisp.Bounds.Height} at {secondDisp.Bounds.X},{secondDisp.Bounds.Y})";
            _lblVirtualDisplayStatus.ForeColor = Color.FromArgb(34, 197, 94);
        }
        else if (SecondScreenServer.IsVirtualDisplayInstalled())
        {
            _lblVirtualDisplayStatus.Text = isEn
                ? "• Driver: Installed (Active) | Desktop: Single Screen (Click 'Extend Desktop')"
                : "• Sürücü: Kurulu (Aktif) | Masaüstü: Tek Ekran ('Genişlet'e basın)";
            _lblVirtualDisplayStatus.ForeColor = Color.FromArgb(245, 158, 11);
        }
        else
        {
            _lblVirtualDisplayStatus.Text = isEn
                ? "• Virtual Display Driver: Not Installed (Click 'Install Driver')"
                : "• Sanal Ekran Sürücüsü: Kurulu Değil (Sürücüyü Kur butonuna basın)";
            _lblVirtualDisplayStatus.ForeColor = Color.FromArgb(239, 68, 68);
        }
    }

    private void UpdateSessionDetails()
    {
        bool isEn = _currentLanguage == AppLanguage.English;
        if (_server.ConnectedClientDetails != null)
        {
            _lblSessionClient.Text = isEn
                ? $"• Connected Device: {_server.ConnectedClientDetails}"
                : $"• Bağlı Cihaz: {_server.ConnectedClientDetails}";
        }
        else
        {
            _lblSessionClient.Text = isEn
                ? "• Connected Device: Waiting (No tablet connected)"
                : "• Bağlı Cihaz: Bekleniyor (Tablet bağlı değil)";
            _lblSessionStats.Text = isEn
                ? "• Stream Specs: 2304x1440 @ 60 FPS | Latency (RTT): -- ms | Bitrate: -- Mbps"
                : "• Akış Özellikleri: 2304x1440 @ 60 FPS | Gecikme (RTT): -- ms | Bitrate: -- Mbps";
        }
    }

    private void SetupSystemTray()
    {
        string? icoPath = ResolveAssetPath("app.ico", "areas.ico");
        if (icoPath != null) _trayIcon.Icon = new Icon(icoPath);

        _trayIcon.Text = "Areas Server — Second Screen";
        _trayIcon.Visible = true;

        _itemTitle = new ToolStripMenuItem("Areas Server (v1.0.4)") { Enabled = false };
        _itemShow = new ToolStripMenuItem("Open Dashboard", null, (_, _) => RestoreFromTray());
        _itemToggleServer = new ToolStripMenuItem("⏹ Stop Server", null, async (_, _) => await ToggleServerAsync());
        _itemExtend = new ToolStripMenuItem("Extend Desktop", null, (_, _) => DisplayTopologyManager.ExtendDesktop());
        _itemExit = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        _trayMenu.Items.AddRange(new ToolStripItem[] { _itemTitle, new ToolStripSeparator(), _itemShow, _itemToggleServer, _itemExtend, new ToolStripSeparator(), _itemExit });
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _server.Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2000, "Areas Server", 
                _currentLanguage == AppLanguage.English ? "Running in background (System Tray)." : "Arka planda çalışmaya devam ediyor.", 
                ToolTipIcon.Info);
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    private void HookServerEvents()
    {
        _server.OnLog += msg =>
        {
            if (InvokeRequired) BeginInvoke(() => AddLog(msg));
            else AddLog(msg);
        };

        _server.OnStatusChanged += _ =>
        {
            if (InvokeRequired) BeginInvoke(UpdateStatusBadge);
            else UpdateStatusBadge();
        };

        _server.OnClientSessionChanged += _ =>
        {
            void RefreshUi()
            {
                UpdateStatusBadge();
                UpdateSessionDetails();
                UpdateDisplayInfo();
            }

            if (InvokeRequired)
            {
                BeginInvoke(RefreshUi);
            }
            else
            {
                RefreshUi();
            }
        };

        _server.OnMetricsUpdated += (rttMs, fps, bitrateMbps) =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateMetricsUi(rttMs, fps, bitrateMbps));
            }
            else
            {
                UpdateMetricsUi(rttMs, fps, bitrateMbps);
            }
        };

        _server.StartAsync();
    }

    private void UpdateMetricsUi(double rttMs, double fps, double bitrateMbps)
    {
        bool isEn = _currentLanguage == AppLanguage.English;
        string streamWord = isEn ? "Stream" : "Akış";
        string rttWord = isEn ? "Network Latency" : "Ağ Gecikmesi";
        string oneWayWord = isEn ? "One-way" : "Tek Yön";
        _lblSessionStats.Text = $"• {streamWord}: 2304x1440 @ {fps:F0} FPS | {rttWord}: {rttMs:F1} ms ({oneWayWord}: {rttMs/2.0:F1} ms) | Bitrate: {bitrateMbps:F1} Mbps | Encoder: {_server.ActiveEncoderLabel}";
        _lblSessionStats.ForeColor = _server.IsHardwareEncoderActive
            ? Color.FromArgb(34, 197, 94)
            : Color.FromArgb(245, 158, 11);
    }

    private void AddLog(string msg)
    {
        if (_rtbLogs.IsDisposed) return;
        _rtbLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        _rtbLogs.SelectionStart = _rtbLogs.Text.Length;
        _rtbLogs.ScrollToCaret();
    }

    private static FlowLayoutPanel CreateCardPanel(string title, out Label titleLabel)
    {
        var pnl = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(28, 28, 36),
            Padding = new Padding(12),
            Margin = new Padding(4)
        };

        titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(167, 139, 250),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            UseMnemonic = false
        };
        pnl.Controls.Add(titleLabel);
        return pnl;
    }

    private static Label CreateInfoLabel(string text)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(220, 220, 230),
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
            UseMnemonic = false
        };
    }

    private static Button CreateButton(string text, Color bg, Color fg)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = fg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(200, 38),
            MaximumSize = new Size(0, 38),
            Padding = new Padding(14, 0, 14, 0),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 0)
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private static Button CreateSmallButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = Color.FromArgb(45, 45, 58),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 4),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 4)
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 90);
        btn.Click += onClick;
        return btn;
    }

    private static Button CreateTabButton(string text, bool isActive, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = isActive ? Color.FromArgb(99, 102, 241) : Color.FromArgb(32, 32, 42),
            ForeColor = isActive ? Color.White : Color.FromArgb(160, 160, 175),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(110, 30),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 0)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += onClick;
        return btn;
    }

    private static string? ResolveAssetPath(params string[] relativePaths)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int depth = 0; depth < 6 && dir != null; depth++, dir = dir.Parent)
        {
            foreach (string rel in relativePaths)
            {
                string candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
