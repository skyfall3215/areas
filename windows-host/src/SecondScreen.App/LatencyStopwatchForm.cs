using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SecondScreen.App;

public sealed class LatencyStopwatchForm : Form
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _lblTitle;
    private readonly Label _lblTimer;
    private readonly Label _lblHint;

    public LatencyStopwatchForm()
    {
        Text = "İkinci Ekran — Gecikme Test Kronometresi";
        Size = new Size(620, 260);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 18);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        TopMost = true;

        _lblTitle = new Label
        {
            Text = "UÇTAN UCA GECİKME ÖLÇÜMÜ (KAMERA TESTİ)",
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _lblTimer = new Label
        {
            Text = "00:00.000",
            ForeColor = Color.FromArgb(0, 255, 128),
            Font = new Font("Consolas", 48, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _lblHint = new Label
        {
            Text = "Tableti bu ekranın yanına koyun ve telefon kamerasıyla iki ekranı aynı karede çekin.\n(Gecikme = PC Sayısı - Tablet Sayısı)",
            ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Segoe UI", 9),
            Dock = DockStyle.Bottom,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter
        };

        Controls.Add(_lblTimer);
        Controls.Add(_lblTitle);
        Controls.Add(_lblHint);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 15 // ~66 FPS refresh
        };
        _timer.Tick += (_, _) =>
        {
            var ts = _stopwatch.Elapsed;
            _lblTimer.Text = $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
        };
        _timer.Start();
    }

    public static void ShowInThread()
    {
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.Run(new LatencyStopwatchForm());
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}

