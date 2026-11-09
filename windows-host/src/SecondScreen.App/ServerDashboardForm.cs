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
    private readonly SecondScreenServer _server = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();

    // UI Controls
    private Label _lblStatusBadge = null!;
    private Button _btnToggleServer = null!;
    private Button _btnExtendDesktop = null!;
    private Button _btnStopwatch = null!;
    private Label _lblUsbStatus = null!;
    private Label _lblWifiIp = null!;
    private Label _lblHotspotStatus = null!;
    private Label _lblVirtualDisplayStatus = null!;
    private Button _btnInstallDriver = null!;
    private Button _btnUninstallDriver = null!;
    private Label _lblSessionClient = null!;
    private Label _lblSessionStats = null!;
    private RichTextBox _rtbLogs = null!;
    private System.Windows.Forms.Timer _metricsTimer = null!;

    public ServerDashboardForm()
    {
        InitializeCustomForm();
        SetupSystemTray();
        HookServerEvents();
    }

    private void InitializeCustomForm()
    {
        Text = "Areas — SecondScreen Server";
        Size = new Size(920, 740);
        MinimumSize = new Size(880, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 26);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        if (!File.Exists(icoPath))
        {
            icoPath = @"D:\Areas\areas.ico";
        }
        if (File.Exists(icoPath))
        {
            Icon = new Icon(icoPath);
        }

        // Main Layout Container
        var pnlMain = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(20),
            BackColor = Color.Transparent
        };
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));  // Header
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // Controls Bar
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 210)); // Connectivity & Displays (Expanded to prevent clipping)
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));  // Live Session Telemetry
        pnlMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Logs

        // 1. Header (Logo, Titles, Status Badge)
        var pnlHeader = new Panel { Dock = DockStyle.Fill };
        
        string logoPath = @"D:\Areas\docs\assets\logo.png";
        if (File.Exists(logoPath))
        {
            var pbLogo = new PictureBox
            {
                Image = Image.FromFile(logoPath),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(54, 54),
                Location = new Point(0, 8)
            };
            pnlHeader.Controls.Add(pbLogo);
        }

        var lblTitle = new Label
        {
            Text = "Areas Server",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(64, 8),
            AutoSize = true
        };
        pnlHeader.Controls.Add(lblTitle);

        var lblSubtitle = new Label
        {
            Text = "Ultra-Low Latency Virtual Second Display for Windows & Android",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 150, 165),
            Location = new Point(66, 38),
            AutoSize = true
        };
        pnlHeader.Controls.Add(lblSubtitle);

        _lblStatusBadge = new Label
        {
            Text = "● Dinliyor (Port 27183)",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(34, 197, 94),
            BackColor = Color.FromArgb(30, 41, 59),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(220, 36),
            Dock = DockStyle.Right,
            Margin = new Padding(0, 12, 0, 12)
        };
        pnlHeader.Controls.Add(_lblStatusBadge);
        pnlMain.Controls.Add(pnlHeader, 0, 0);

        // 2. Control Bar (Start/Stop, Extend, Stopwatch)
        var pnlControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _btnToggleServer = CreateButton("⏹ Sunucuyu Durdur", Color.FromArgb(239, 68, 68), Color.White);
        _btnToggleServer.Click += async (_, _) =>
        {
            if (_server.IsRunning)
            {
                _server.Stop();
                _btnToggleServer.Text = "▶ Sunucuyu Başlat";
                _btnToggleServer.BackColor = Color.FromArgb(37, 99, 235);
            }
            else
            {
                await _server.StartAsync();
                _btnToggleServer.Text = "⏹ Sunucuyu Durdur";
                _btnToggleServer.BackColor = Color.FromArgb(239, 68, 68);
            }
        };
        pnlControls.Controls.Add(_btnToggleServer);

        _btnExtendDesktop = CreateButton("🖥️ Masaüstünü Genişlet (Extend)", Color.FromArgb(39, 39, 46), Color.White);
        _btnExtendDesktop.Click += async (_, _) =>
        {
            try
            {
                DisplayTopologyManager.ExtendDesktop();
                AddLog("[+] Masaüstü genişletildi (Extend).");
                await Task.Delay(500);
                UpdateDisplayInfo();
            }
            catch (Exception ex)
            {
                AddLog($"[!] Genişletme hatası: {ex.Message}");
            }
        };
        pnlControls.Controls.Add(_btnExtendDesktop);

        _btnStopwatch = CreateButton("⏱️ Kronometre (Gecikme Testi)", Color.FromArgb(39, 39, 46), Color.White);
        _btnStopwatch.Click += (_, _) =>
        {
            LatencyStopwatchForm.ShowInThread();
            AddLog("[+] Gecikme test kronometresi açıldı.");
        };
        pnlControls.Controls.Add(_btnStopwatch);

        pnlMain.Controls.Add(pnlControls, 0, 1);

        // 3. Grid: Connectivity Cards & Display Manager
        var pnlMiddle = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        pnlMiddle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pnlMiddle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // Left Card: Connectivity
        var pnlConnCard = CreateCardPanel("🌐 Bağlantı Kanalları");
        _lblUsbStatus = CreateInfoLabel("• USB-C Kablo (adb reverse): Kontrol ediliyor...");
        _lblWifiIp = CreateInfoLabel("• Wi-Fi LAN IP: 192.168.1.212:27183");
        _lblHotspotStatus = CreateInfoLabel("• Mobil Etkin Nokta (Hotspot): 192.168.137.1");

        var btnHotspotSettings = CreateSmallButton("Hotspot Ayarları", (_, _) => HotspotManager.OpenHotspotSettings());
        var btnRefreshUsb = CreateSmallButton("USB Tüneli Yenile", async (_, _) => await _server.RefreshUsbTunnelAsync());

        pnlConnCard.Controls.Add(_lblUsbStatus);
        pnlConnCard.Controls.Add(_lblWifiIp);
        pnlConnCard.Controls.Add(_lblHotspotStatus);

        var pnlConnButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        pnlConnButtons.Controls.Add(btnRefreshUsb);
        pnlConnButtons.Controls.Add(btnHotspotSettings);
        pnlConnCard.Controls.Add(pnlConnButtons);

        pnlMiddle.Controls.Add(pnlConnCard, 0, 0);

        // Right Card: Display & Driver
        var pnlDispCard = CreateCardPanel("🖥️ Sanal Ekran Sürücüsü (IddCx)");
        _lblVirtualDisplayStatus = CreateInfoLabel("• Sanal Ekran: Kontrol ediliyor...");
        _lblVirtualDisplayStatus.MaximumSize = new Size(380, 0); // Allow clean multi-line wrapping without overlapping buttons
        
        _btnInstallDriver = CreateSmallButton("Sürücüyü Kur (IddCx)", async (_, _) =>
        {
            AddLog("[+] Sanal ekran sürücüsü kuruluyor...");
            await SecondScreenServer.InstallVirtualDisplayDriverAsync();
            UpdateDisplayInfo();
        });

        _btnUninstallDriver = CreateSmallButton("Sürücüyü Kaldır", async (_, _) =>
        {
            AddLog("[+] Sanal ekran sürücüsü kaldırılıyor...");
            await SecondScreenServer.UninstallVirtualDisplayDriverAsync();
            UpdateDisplayInfo();
        });

        var btnSingleScreen = CreateSmallButton("Yalnızca Laptop Ekranı", async (_, _) =>
        {
            try
            {
                DisplayTopologyManager.RestoreInternalOnly();
                AddLog("[+] Yalnızca birincil ekran moduna dönüldü.");
                await Task.Delay(500);
                UpdateDisplayInfo();
            }
            catch (Exception ex)
            {
                AddLog($"[!] Hata: {ex.Message}");
            }
        });

        pnlDispCard.Controls.Add(_lblVirtualDisplayStatus);

        var pnlDispButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            Margin = new Padding(0, 8, 0, 0)
        };
        pnlDispButtons.Controls.Add(_btnInstallDriver);
        pnlDispButtons.Controls.Add(_btnUninstallDriver);
        pnlDispButtons.Controls.Add(btnSingleScreen);
        pnlDispCard.Controls.Add(pnlDispButtons);

        pnlMiddle.Controls.Add(pnlDispCard, 1, 0);
        pnlMain.Controls.Add(pnlMiddle, 0, 2);

        // 4. Live Session Telemetry Card
        var pnlTelemetryCard = CreateCardPanel("📊 Aktif Oturum ve Canlı Telemetri");
        _lblSessionClient = CreateInfoLabel("• Bağlı Cihaz: Bekleniyor (Tablet bağlı değil)");
        _lblSessionStats = CreateInfoLabel("• Akış Özellikleri: 2304x1440 @ 60 FPS | Gecikme (RTT): -- ms | Bitrate: -- Mbps");

        pnlTelemetryCard.Controls.Add(_lblSessionClient);
        pnlTelemetryCard.Controls.Add(_lblSessionStats);
        pnlMain.Controls.Add(pnlTelemetryCard, 0, 3);

        // 5. Live Logs Box
        _rtbLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(14, 14, 18),
            ForeColor = Color.FromArgb(200, 200, 215),
            Font = new Font("Consolas", 9f),
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };
        pnlMain.Controls.Add(_rtbLogs, 0, 4);

        Controls.Add(pnlMain);

        // Update initial statuses
        UpdateNetworkInfo();
        UpdateDisplayInfo();

        _metricsTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _metricsTimer.Tick += (_, _) => UpdateNetworkInfo();
        _metricsTimer.Start();
    }

    private void SetupSystemTray()
    {
        string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
        if (!File.Exists(icoPath)) icoPath = @"D:\Areas\areas.ico";
        if (File.Exists(icoPath)) _trayIcon.Icon = new Icon(icoPath);

        _trayIcon.Text = "Areas Server — İkinci Ekran";
        _trayIcon.Visible = true;

        var itemTitle = new ToolStripMenuItem("Areas Server (v1.0)") { Enabled = false };
        var itemShow = new ToolStripMenuItem("Arayüzü Aç", null, (_, _) => RestoreFromTray());
        var itemExtend = new ToolStripMenuItem("Masaüstünü Genişlet", null, (_, _) => DisplayTopologyManager.ExtendDesktop());
        var itemExit = new ToolStripMenuItem("Çıkış", null, (_, _) => ExitApp());

        _trayMenu.Items.AddRange(new ToolStripItem[] { itemTitle, new ToolStripSeparator(), itemShow, itemExtend, new ToolStripSeparator(), itemExit });
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2000, "Areas Arka Planda Çalışıyor", "Sunucu çalışmaya devam ediyor. Açmak için simgeye çift tıklayın.", ToolTipIcon.Info);
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    private void ExitApp()
    {
        _server.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    private void HookServerEvents()
    {
        _server.OnLog += msg =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() => AddLog(msg)));
        };

        _server.OnStatusChanged += status =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                _lblStatusBadge.Text = $"● {status}";
                _lblStatusBadge.ForeColor = _server.IsClientConnected ? Color.FromArgb(59, 130, 246) :
                                            _server.IsRunning ? Color.FromArgb(34, 197, 94) : Color.FromArgb(156, 163, 175);
            }));
        };

        _server.OnClientSessionChanged += details =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (details != null)
                {
                    _lblSessionClient.Text = $"• Bağlı Cihaz: {details}";
                    _lblSessionClient.ForeColor = Color.FromArgb(96, 165, 250);
                }
                else
                {
                    _lblSessionClient.Text = "• Bağlı Cihaz: Bekleniyor (Tablet bağlı değil)";
                    _lblSessionClient.ForeColor = Color.FromArgb(240, 240, 245);
                    _lblSessionStats.Text = "• Akış Özellikleri: 2304x1440 @ 60 FPS | Gecikme (RTT): -- ms | Bitrate: -- Mbps";
                }
            }));
        };

        _server.OnMetricsUpdated += (rttMs, fps, bitrateMbps) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                _lblSessionStats.Text = $"• Akış: 2304x1440 @ {fps:F0} FPS | Ağ Gecikmesi: {rttMs:F1} ms (Tek Yön: {rttMs / 2:F1} ms) | Bitrate: {bitrateMbps:F1} Mbps";
            }));
        };

        // Start server automatically
        _ = _server.StartAsync();
    }

    private void UpdateNetworkInfo()
    {
        var localIps = NetworkHelper.GetLocalIPv4Addresses();
        var mainWifi = localIps.FirstOrDefault(ip => ip != HotspotManager.DefaultWindowsHotspotIp) ?? "Bağlı Değil";
        _lblWifiIp.Text = $"• Wi-Fi LAN IP: {mainWifi}:{_server.Port}";

        bool hotspotActive = HotspotManager.IsHotspotActive();
        _lblHotspotStatus.Text = hotspotActive
            ? "• Mobil Etkin Nokta (Hotspot): 192.168.137.1 (Aktif)"
            : "• Mobil Etkin Nokta: Kapalı (Yurt/Okul için açın)";

        _lblUsbStatus.Text = "• USB-C Kablo (adb reverse): Port 27183 yönlendirildi";
    }

    private void UpdateDisplayInfo()
    {
        var displays = DisplayEnumerator.GetDisplays();
        var secondDisp = displays.FirstOrDefault(d => !d.IsPrimary);

        if (secondDisp != null)
        {
            _lblVirtualDisplayStatus.Text = $"• İkinci Ekran: {secondDisp.FriendlyName} ({secondDisp.Bounds.Width}x{secondDisp.Bounds.Height} at {secondDisp.Bounds.X},{secondDisp.Bounds.Y})";
            _lblVirtualDisplayStatus.ForeColor = Color.FromArgb(34, 197, 94); // Green
        }
        else if (SecondScreenServer.IsVirtualDisplayInstalled())
        {
            _lblVirtualDisplayStatus.Text = "• Sürücü: Kurulu (Aktif) | Masaüstü: Tek Ekran ('Genişlet'e basın)";
            _lblVirtualDisplayStatus.ForeColor = Color.FromArgb(245, 158, 11); // Amber
        }
        else
        {
            _lblVirtualDisplayStatus.Text = "• Sanal Ekran Sürücüsü: Kurulu Değil (Sürücüyü Kur butonuna basın)";
            _lblVirtualDisplayStatus.ForeColor = Color.FromArgb(239, 68, 68); // Red
        }
    }

    private void AddLog(string msg)
    {
        if (_rtbLogs.IsDisposed) return;
        _rtbLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        _rtbLogs.SelectionStart = _rtbLogs.Text.Length;
        _rtbLogs.ScrollToCaret();
    }

    private static FlowLayoutPanel CreateCardPanel(string title)
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

        var lbl = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(167, 139, 250), // Violet accent
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        pnl.Controls.Add(lbl);
        return pnl;
    }

    private static Label CreateInfoLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Color.FromArgb(220, 220, 230),
            Margin = new Padding(0, 2, 0, 2)
        };
    }

    private static Button CreateButton(string text, Color bg, Color fg)
    {
        return new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = fg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Height = 38,
            AutoSize = true,
            Margin = new Padding(0, 0, 10, 0),
            Cursor = Cursors.Hand
        };
    }

    private static Button CreateSmallButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = Color.FromArgb(44, 44, 56),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Height = 28,
            AutoSize = true,
            Margin = new Padding(0, 4, 6, 0),
            Cursor = Cursors.Hand
        };
        btn.Click += onClick;
        return btn;
    }
}

