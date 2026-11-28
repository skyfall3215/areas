using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SecondScreen.App;

public sealed class LatencyStopwatchForm : Form
{
    private static readonly Color BgCanvas = Color.FromArgb(10, 20, 34);       // #0A1422
    private static readonly Color SkyPrimary = Color.FromArgb(109, 182, 255);  // #6DB6FF
    private static readonly Color TextPrimary = Color.FromArgb(234, 243, 252); // #EAF3FC
    private static readonly Color TextMuted = Color.FromArgb(140, 161, 186);   // #8CA1BA

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _lblTitle;
    private readonly Label _lblTimer;
    private readonly Label _lblHint;

    public LatencyStopwatchForm()
    {
        DoubleBuffered = true;
        Text = "İkinci Ekran — Gecikme Test Kronometresi";
        Size = new Size(620, 260);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgCanvas;
        ForeColor = TextPrimary;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        TopMost = true;

        string? icoPath = ResolveAssetPath("app.ico", "areas.ico");
        if (icoPath != null) Icon = new Icon(icoPath);

        _lblTitle = new Label
        {
            Text = "UÇTAN UCA GECİKME ÖLÇÜMÜ (KAMERA TESTİ)",
            ForeColor = TextPrimary,
            BackColor = BgCanvas,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _lblTimer = new Label
        {
            Text = "00:00.000",
            ForeColor = SkyPrimary,
            BackColor = BgCanvas,
            Font = new Font("Consolas", 48, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _lblHint = new Label
        {
            Text = "Tableti bu ekranın yanına koyun ve telefon kamerasıyla iki ekranı aynı karede çekin.\n(Gecikme = PC Sayısı - Tablet Sayısı)",
            ForeColor = TextMuted,
            BackColor = BgCanvas,
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int useDarkMode = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
            int captionColor = (BgCanvas.B << 16) | (BgCanvas.G << 8) | BgCanvas.R;
            _ = DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        }
        catch
        {
            // Ignore on unsupported Windows builds.
        }
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
