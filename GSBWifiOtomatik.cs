using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

class App : Form
{
    const string Ssid = "GSBWIFI";
    const int TickMs = 4000, FailsToKick = 3, ResetEvery = 1;
    Label lbl; TextBox box; Button btn, btnHide; NotifyIcon tray; System.Windows.Forms.Timer timer;
    int fails = 0, kicks = 0; bool running = true, busy = false, done = false;

    [STAThread] static void Main() { Application.EnableVisualStyles(); Application.Run(new App()); }

    App()
    {
        Text = "GSBWIFI Otomatik Bağlantı"; ClientSize = new Size(404, 290);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        lbl = new Label { Location = new Point(12, 10), Size = new Size(380, 28), Font = new Font("Segoe UI", 11, FontStyle.Bold), Text = "Başlıyor..." };
        box = new TextBox { Location = new Point(12, 45), Size = new Size(380, 185), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
        btn = new Button { Location = new Point(12, 242), Size = new Size(185, 34), Text = "Durdur" };
        btnHide = new Button { Location = new Point(207, 242), Size = new Size(185, 34), Text = "Arka plana al" };
        Controls.AddRange(new Control[] { lbl, box, btn, btnHide });
        Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Icon = appIcon;
        tray = new NotifyIcon { Icon = appIcon, Text = "GSBWIFI Otomatik", Visible = true };
        timer = new System.Windows.Forms.Timer { Interval = TickMs };
        timer.Tick += async (s, e) => await Tick();
        btn.Click += (s, e) => {
            if (running) { running = false; timer.Stop(); btn.Text = "Başlat"; SetStatus("Durduruldu"); Log("Durduruldu."); }
            else { running = true; fails = 0; btn.Text = "Durdur"; Log("Başladı."); timer.Start(); }
        };
        btnHide.Click += (s, e) => Hide();
        tray.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; };
        FormClosed += (s, e) => { tray.Visible = false; tray.Dispose(); };
        Shown += (s, e) => { Log("Başladı. Hedef: " + Ssid); timer.Start(); };
    }

    void Log(string m) { box.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m + Environment.NewLine); }
    void SetStatus(string m) { lbl.Text = m; tray.Text = m.Length > 60 ? m.Substring(0, 60) : m; }

    static string Netsh(string args)
    {
        try {
            var p = Process.Start(new ProcessStartInfo("netsh", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 });
            string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o;
        } catch { return ""; }
    }
    static string CurrentSsid()
    {
        foreach (var l in Netsh("wlan show interfaces").Split('\n')) {
            var m = Regex.Match(l, @"^\s*SSID\s*:\s*(.+?)\s*$");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
    static bool Internet()
    {
        try {
            var r = (HttpWebRequest)WebRequest.Create("http://www.msftconnecttest.com/connecttest.txt");
            r.Timeout = 3000; r.AllowAutoRedirect = false; r.Proxy = null;
            using (var resp = r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd().Trim() == "Microsoft Connect Test";
        } catch { return false; }
    }
    static string AdapterName()
    {
        foreach (var l in Netsh("wlan show interfaces").Split('\n')) {
            var m = Regex.Match(l, @"^\s+(\S[^:]*?)\s*:\s*(\S.*?)\s*$");
            if (m.Success) return m.Groups[2].Value;
        }
        return "Wi-Fi";
    }
    static void ResetAdapter()
    {
        string n = AdapterName();
        Netsh("interface set interface name=\"" + n + "\" admin=disabled");
        Thread.Sleep(3000);
        Netsh("interface set interface name=\"" + n + "\" admin=enabled");
        Thread.Sleep(5000);
        Connect();
    }
    static void Connect() { Netsh("wlan connect name=\"" + Ssid + "\" ssid=\"" + Ssid + "\""); }

    async Task Tick()
    {
        if (busy || !running || done) return;
        busy = true;
        try {
            string cur = null; bool ok = false;
            await Task.Run(() => { cur = CurrentSsid(); if (cur == Ssid) ok = Internet(); });
            if (cur == Ssid) {
                if (ok) {
                    done = true; timer.Stop(); SetStatus("Bağlandı, internet var!"); Log("İnternet var. Kapanıyor.");
                    tray.BalloonTipTitle = "GSBWIFI"; tray.BalloonTipText = "Bağlandı, internet var."; tray.ShowBalloonTip(3000);
                    await Task.Delay(3000); Close(); return;
                }
                fails++; SetStatus(Ssid + ": internet yok (" + fails + "/" + FailsToKick + ")");
                if (fails >= FailsToKick) {
                    fails = 0; kicks++;
                    if (kicks % ResetEvery == 0) {
                        Log("Wi-Fi kapat/aç #" + kicks);
                        await Task.Run(() => ResetAdapter());
                    } else {
                        Log("Kopar/bağlan #" + kicks);
                        await Task.Run(() => { Netsh("wlan disconnect"); Thread.Sleep(1500); Connect(); });
                    }
                }
            } else {
                fails = 0; SetStatus(Ssid + " bağlı değil, bağlanılıyor...");
                Log("Şu an: " + (cur ?? "bağlı değil") + " -> " + Ssid + "'e bağlanılıyor");
                await Task.Run(() => Connect());
            }
        } catch (Exception ex) { Log("Hata: " + ex.Message); }
        finally { busy = false; }
    }
}
