using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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

    [STAThread] static void Main()
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
        Application.EnableVisualStyles(); Application.Run(new App());
    }

    App()
    {
        Text = "GSBWIFI Otomatik Bağlantı"; ClientSize = new Size(404, 330);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        lbl = new Label { Location = new Point(12, 10), Size = new Size(380, 28), Font = new Font("Segoe UI", 11, FontStyle.Bold), Text = "Başlıyor..." };
        box = new TextBox { Location = new Point(12, 45), Size = new Size(380, 185), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
        btn = new Button { Location = new Point(12, 242), Size = new Size(185, 34), Text = "Durdur" };
        btnHide = new Button { Location = new Point(207, 242), Size = new Size(185, 34), Text = "Arka plana al" };
        var btnCred = new Button { Location = new Point(12, 284), Size = new Size(380, 32), Text = "Giriş bilgilerini değiştir" };
        btnCred.Click += (s, e) => { if (AskCred(this)) Log("Giriş bilgileri kaydedildi."); };
        Controls.AddRange(new Control[] { lbl, box, btn, btnHide, btnCred });
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
        Shown += (s, e) => {
            string u, p;
            if (!LoadCred(out u, out p)) {
                MessageBox.Show(this, "Giriş sayfasında senin yerine 'Giriş'e basabilmem için kullanıcı adı ve şifreni bir kez girmen gerekiyor.", "GSBWIFI Otomatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (AskCred(this)) Log("Giriş bilgileri kaydedildi."); else Log("Giriş bilgisi girilmedi, giriş sayfası otomatik geçilemez.");
            }
            Log("Başladı. Hedef: " + Ssid); timer.Start();
        };
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
    // ---- Portal girişi (wifi.gsb.gov.tr) ----
    const string PortalUrl = "https://wifi.gsb.gov.tr/login.html";
    const string PortalPost = "https://wifi.gsb.gov.tr/j_spring_security_check";
    static string CredPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSBWifiOtomatik", "kimlik.dat"); } }

    static bool LoadCred(out string user, out string pass)
    {
        user = pass = null;
        try {
            if (!File.Exists(CredPath)) return false;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(CredPath), null, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(raw).Split(new[] { '\n' }, 2);
            if (parts.Length != 2 || parts[0] == "") return false;
            user = parts[0]; pass = parts[1]; return true;
        } catch { return false; }
    }
    static void SaveCred(string user, string pass)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredPath));
        File.WriteAllBytes(CredPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(user + "\n" + pass), null, DataProtectionScope.CurrentUser));
    }
    static bool AskCred(IWin32Window owner)
    {
        using (var f = new Form { Text = "GSBWIFI giriş bilgileri", ClientSize = new Size(330, 170), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
        {
            var tu = new TextBox { Location = new Point(110, 18), Size = new Size(200, 24) };
            var tp = new TextBox { Location = new Point(110, 54), Size = new Size(200, 24), UseSystemPasswordChar = true };
            string u, p; if (LoadCred(out u, out p)) tu.Text = u;
            var ok = new Button { Text = "Kaydet", Location = new Point(110, 125), Size = new Size(95, 30), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Vazgeç", Location = new Point(215, 125), Size = new Size(95, 30), DialogResult = DialogResult.Cancel };
            var note = new Label { Text = "Bilgiler bu bilgisayarda, sadece senin Windows hesabınla açılabilecek şekilde şifreli saklanır.", Location = new Point(12, 88), Size = new Size(305, 34), ForeColor = Color.DimGray };
            f.Controls.AddRange(new Control[] { new Label { Text = "Kullanıcı adı:", Location = new Point(12, 21), AutoSize = true }, new Label { Text = "Şifre:", Location = new Point(12, 57), AutoSize = true }, tu, tp, note, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog(owner) != DialogResult.OK || tu.Text.Trim() == "" || tp.Text == "") return false;
            SaveCred(tu.Text.Trim(), tp.Text); return true;
        }
    }

    static HttpWebRequest Req(string url, CookieContainer jar)
    {
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.Timeout = 6000; r.CookieContainer = jar; r.Proxy = null; r.AllowAutoRedirect = true;
        r.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        return r;
    }
    // Portal giriş sayfasına ulaşılabiliyor mu?
    static bool PortalReachable(CookieContainer jar)
    {
        try {
            using (var resp = Req(PortalUrl, jar).GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd().Contains("j_spring_security_check");
        } catch { return false; }
    }
    static void PortalLogin(CookieContainer jar, string user, string pass)
    {
        var body = Encoding.UTF8.GetBytes("j_username=" + Uri.EscapeDataString(user) + "&j_password=" + Uri.EscapeDataString(pass) + "&submit=Login");
        var r = Req(PortalPost, jar);
        r.Method = "POST"; r.ContentType = "application/x-www-form-urlencoded"; r.Referer = PortalUrl; r.ContentLength = body.Length;
        using (var st = r.GetRequestStream()) st.Write(body, 0, body.Length);
        using (var resp = r.GetResponse()) { }
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
            string cur = null; bool ok = false, portal = false, loginTried = false;
            string lerr = null;
            await Task.Run(() => {
                cur = CurrentSsid();
                if (cur != Ssid) return;
                ok = Internet();
                if (ok) return;
                var jar = new CookieContainer();
                portal = PortalReachable(jar);
                if (!portal) return;
                string u, p;
                if (!LoadCred(out u, out p)) return;
                loginTried = true;
                try { PortalLogin(jar, u, p); } catch (Exception ex) { lerr = ex.Message; }
                ok = Internet();
            });
            if (portal && !loginTried && !ok) {
                SetStatus("Giriş sayfası var, bilgi gerekli"); Log("Giriş sayfası algılandı ama kayıtlı bilgi yok. 'Bilgileri değiştir' ile gir.");
            } else if (loginTried) {
                Log(ok ? "Giriş formu gönderildi." : "Giriş formu gönderildi, internet henüz yok." + (lerr != null ? " (" + lerr + ")" : ""));
            }
            if (cur == Ssid) {
                if (ok) {
                    done = true; timer.Stop(); SetStatus("Bağlandı, internet var!"); Log("İnternet var. Kapanıyor.");
                    tray.BalloonTipTitle = "GSBWIFI"; tray.BalloonTipText = "Bağlandı, internet var."; tray.ShowBalloonTip(3000);
                    await Task.Delay(3000); Close(); return;
                }
                if (portal) Log("Giriş sayfası algılandı.");
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
