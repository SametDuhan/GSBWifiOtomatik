using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ============================================================================
//  GSBWIFI Otomatik Bağlantı
//  - Ağa bağlanır, "internet yok" olursa Wi-Fi kartını kapatıp açar
//  - Giriş sayfasını Chrome'da açar (başsız), bilgileri yazar, "Giriş"e tıklar
//  - İstenirse sağ altta canlı görünüm: ikinci bir sanal imleç + ekran görüntüsü
//    (gerçek fare imleci kullanılmaz, senin farene dokunulmaz)
// ============================================================================

enum Ph { Idle, Stopped, Connect, Reset, Page, Login, Done, Warn }

static class T
{
    public static readonly Color Bg = Color.FromArgb(13, 17, 27), Side = Color.FromArgb(19, 25, 39), Card = Color.FromArgb(23, 30, 47),
        Line = Color.FromArgb(40, 50, 74), Text = Color.FromArgb(233, 237, 246), Muted = Color.FromArgb(135, 147, 170),
        Accent = Color.FromArgb(59, 130, 246), Green = Color.FromArgb(34, 197, 94), Amber = Color.FromArgb(245, 158, 11),
        Red = Color.FromArgb(239, 68, 68), Cursor = Color.FromArgb(255, 94, 196);
    public static float S = 1f;
    public static void Tx(Graphics g, string s, float pt, bool semi, Color c, float x, float y, float w, float h, int align, bool vcenter, bool wrap)
    {
        if (string.IsNullOrEmpty(s)) return;
        using (var f = new Font(semi ? "Segoe UI Semibold" : "Segoe UI", pt * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var b = new SolidBrush(c))
        using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = align == 1 ? StringAlignment.Center : StringAlignment.Near, LineAlignment = vcenter ? StringAlignment.Center : StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter })
        {
            if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
            g.DrawString(s, f, b, new RectangleF(x, y, w, h), sf);
        }
    }
    public static float Measure(Graphics g, string s, float pt, bool semi)
    {
        using (var f = new Font(semi ? "Segoe UI Semibold" : "Segoe UI", pt * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel))
            return g.MeasureString(s, f, 2000, StringFormat.GenericTypographic).Width;
    }
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    public static Color A(Color c, int a) { return Color.FromArgb(a, c); }
    public static Color Lighten(Color c, int n) { return Color.FromArgb(Math.Min(255, c.R + n), Math.Min(255, c.G + n), Math.Min(255, c.B + n)); }
}

// ---------------------------------------------------------------------------
//  Chrome DevTools Protocol istemcisi (başsız Chrome/Edge'i uzaktan sürer)
// ---------------------------------------------------------------------------
class Cdp : IDisposable
{
    public const int VW = 1024, VH = 640;
    Process proc; ClientWebSocket ws; string profileDir; int nextId = 0; readonly object gate = new object();
    readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    public volatile float CurX = VW * 0.82f, CurY = VH * 0.92f;
    public long ClickTicks = 0;
    public volatile string Action = "";
    public Action<string> Seed;
    public Func<bool> Cancelled = () => false;

    public bool Alive { get { try { return proc != null && !proc.HasExited && ws != null && ws.State == WebSocketState.Open; } catch { return false; } } }

    public static string FindBrowser()
    {
        string pf = Environment.GetEnvironmentVariable("ProgramFiles"), pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
               la = Environment.GetEnvironmentVariable("LocalAppData");
        var list = new List<string>();
        foreach (var b in new[] { pf, pf86, la }) if (!string.IsNullOrEmpty(b)) list.Add(Path.Combine(b, @"Google\Chrome\Application\chrome.exe"));
        foreach (var b in new[] { pf86, pf }) if (!string.IsNullOrEmpty(b)) list.Add(Path.Combine(b, @"Microsoft\Edge\Application\msedge.exe"));
        foreach (var p in list) if (File.Exists(p)) return p;
        return null;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public bool Start(string exe, string baseDir)
    {
        // her çalıştırmaya ayrı geçici profil: eski/yarım kalmış bir Chrome profili kilitleyemez
        try { foreach (var d in Directory.GetDirectories(baseDir, "chrome*")) try { Directory.Delete(d, true); } catch { } } catch { }
        string profile = Path.Combine(baseDir, "chrome-" + Process.GetCurrentProcess().Id + "-" + Environment.TickCount);
        profileDir = profile;
        if (Seed != null) { try { Directory.CreateDirectory(profile); Seed(profile); } catch { } }
        foreach (bool noSandbox in new[] { false, true })
        {
            int port = FreePort();
            string args = "--headless=new --remote-debugging-port=" + port + " --remote-allow-origins=* --user-data-dir=\"" + profile + "\"" +
                " --window-size=" + VW + "," + VH + " --no-first-run --no-default-browser-check --disable-gpu --disable-extensions --mute-audio" +
                " --disable-features=Translate --user-agent=\"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36\"" +
                (noSandbox ? " --no-sandbox" : "") + " about:blank";
            try { proc = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); } catch { return false; }
            string url = WaitWs(port, 9000);
            if (url != null)
            {
                try
                {
                    ws = new ClientWebSocket();
                    if (!ws.ConnectAsync(new Uri(url), CancellationToken.None).Wait(6000)) throw new IOException("ws");
                    Call("Page.enable");
                    Call("Emulation.setDeviceMetricsOverride", new Dictionary<string, object> { { "width", VW }, { "height", VH }, { "deviceScaleFactor", 1 }, { "mobile", false } });
                    // Canlı görünümde kullanıcı adı ve şifre okunmasın: giriş alanlarındaki yazı noktaya çevrilir (gönderilen değer değişmez)
                    Call("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { { "source",
                        "(function(){function m(){var s=document.createElement('style');s.textContent='input:not([type=submit]):not([type=button]):not([type=checkbox]):not([type=radio]):not([type=hidden]){-webkit-text-security:disc !important;}';(document.head||document.documentElement).appendChild(s);}" +
                        "if(document.documentElement)m();else document.addEventListener('DOMContentLoaded',m);})();" } });
                    return true;
                }
                catch { }
            }
            Dispose();
        }
        return false;
    }

    string WaitWs(int port, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            if (Cancelled()) return null;
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list"); r.Timeout = 1500; r.Proxy = null;
                using (var resp = r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    var arr = js.DeserializeObject(sr.ReadToEnd()) as System.Collections.IEnumerable;
                    if (arr != null) foreach (var o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null && d.ContainsKey("type") && (string)d["type"] == "page" && d.ContainsKey("webSocketDebuggerUrl")) return (string)d["webSocketDebuggerUrl"];
                    }
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        return null;
    }

    public Dictionary<string, object> Call(string method, object prm = null, int timeout = 10000)
    {
        lock (gate)
        {
            int id = ++nextId;
            var msg = new Dictionary<string, object> { { "id", id }, { "method", method } };
            if (prm != null) msg["params"] = prm;
            var data = Encoding.UTF8.GetBytes(js.Serialize(msg));
            var cts = new CancellationTokenSource(timeout);
            try
            {
                ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, cts.Token).Wait();
                var buf = new byte[65536];
                while (true)
                {
                    var ms = new MemoryStream(); WebSocketReceiveResult r;
                    do { r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).Result; ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) throw new IOException("tarayıcı bağlantısı kapandı");
                    if (r.MessageType != WebSocketMessageType.Text) continue;
                    var o = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(ms.ToArray()));
                    if (o.ContainsKey("id") && Convert.ToInt32(o["id"]) == id)
                    {
                        if (o.ContainsKey("error")) throw new IOException("CDP " + method + " hatası");
                        return o.ContainsKey("result") ? (o["result"] as Dictionary<string, object>) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                    }
                }
            }
            finally { cts.Dispose(); }
        }
    }

    public object Eval(string expr)
    {
        var r = Call("Runtime.evaluate", new Dictionary<string, object> { { "expression", expr }, { "returnByValue", true } });
        var res = r.ContainsKey("result") ? r["result"] as Dictionary<string, object> : null;
        return res != null && res.ContainsKey("value") ? res["value"] : null;
    }

    public byte[] Screenshot()
    {
        var r = Call("Page.captureScreenshot", new Dictionary<string, object> { { "format", "jpeg" }, { "quality", 60 } }, 4000);
        return r.ContainsKey("data") ? Convert.FromBase64String((string)r["data"]) : null;
    }

    // Hata metni döndürür, sorun yoksa null
    public string Navigate(string url)
    {
        Action = "Giriş sayfası açılıyor";
        var r = Call("Page.navigate", new Dictionary<string, object> { { "url", url } }, 20000);
        return r.ContainsKey("errorText") ? (string)r["errorText"] : null;
    }

    bool Mouse(string type, double x, double y, string button, int clicks)
    {
        Call("Input.dispatchMouseEvent", new Dictionary<string, object> { { "type", type }, { "x", x }, { "y", y }, { "button", button }, { "clickCount", clicks } });
        return true;
    }

    public void MoveTo(double x, double y)
    {
        double sx = CurX, sy = CurY; const int steps = 26;
        for (int i = 1; i <= steps; i++)
        {
            if (Cancelled()) return;
            double t = i / (double)steps, e = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            CurX = (float)(sx + (x - sx) * e); CurY = (float)(sy + (y - sy) * e);
            Mouse("mouseMoved", CurX, CurY, "none", 0);
            Thread.Sleep(16);
        }
    }

    public void Click()
    {
        Mouse("mousePressed", CurX, CurY, "left", 1); ClickTicks = Environment.TickCount;
        Thread.Sleep(90);
        Mouse("mouseReleased", CurX, CurY, "left", 1);
        Thread.Sleep(150);
    }

    // Seçiciyle bulunan öğenin merkezine gidip tıklar
    public bool ClickSel(string sel, string what)
    {
        string pos = Eval("(function(){var e=document.querySelector(" + js.Serialize(sel) + ");if(!e)return '';e.scrollIntoView({block:'center'});var r=e.getBoundingClientRect();return (r.left+r.width/2)+','+(r.top+r.height/2);})()") as string;
        return ClickPos(pos, what);
    }

    // "Giriş" düğmesini bulup tıklar
    public bool ClickSubmit()
    {
        string pos = Eval("(function(){var c=document.querySelectorAll('input[type=submit],button,input[type=button],a.btn,a[role=button]');var pick=null;" +
            "for(var i=0;i<c.length;i++){var t=(c[i].value||c[i].textContent||'');var r=c[i].getBoundingClientRect();if(r.width<2||r.height<2)continue;if(/giri|login|oturum|connect|bağlan/i.test(t)){pick=c[i];break;}if(!pick)pick=c[i];}" +
            "if(!pick)return '';pick.scrollIntoView({block:'center'});var r=pick.getBoundingClientRect();return (r.left+r.width/2)+','+(r.top+r.height/2);})()") as string;
        return ClickPos(pos, "Giriş düğmesi");
    }

    bool ClickPos(string pos, string what)
    {
        if (string.IsNullOrEmpty(pos)) return false;
        var p = pos.Split(',');
        double x = double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), y = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
        Action = what + " için gidiliyor";
        MoveTo(x, y);
        Action = "Tıklandı: " + what;
        Click();
        return true;
    }

    public void Key(string key, int vk)
    {
        foreach (var t in new[] { "keyDown", "keyUp" })
            Call("Input.dispatchKeyEvent", new Dictionary<string, object> { { "type", t }, { "key", key }, { "code", key }, { "windowsVirtualKeyCode", vk }, { "nativeVirtualKeyCode", vk } });
        Thread.Sleep(120);
    }

    public void TypeText(string text, string what)
    {
        Action = what + " yazılıyor";
        Eval("(function(){var e=document.activeElement;if(e&&'value' in e){e.value='';}})()");
        foreach (char ch in text)
        {
            if (Cancelled()) return;
            Call("Input.insertText", new Dictionary<string, object> { { "text", ch.ToString() } });
            Thread.Sleep(45);
        }
        Thread.Sleep(150);
    }

    public void Dispose()
    {
        try { if (ws != null) ws.Dispose(); } catch { }
        ws = null;
        try
        {
            if (proc != null && !proc.HasExited)
            {
                // yalnızca kendi başlattığımız Chrome ağacı kapatılır
                var k = Process.Start(new ProcessStartInfo("taskkill", "/PID " + proc.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true });
                if (k != null) k.WaitForExit(4000);
            }
        }
        catch { }
        proc = null;
        if (profileDir != null) { var d = profileDir; profileDir = null; Task.Run(() => { Thread.Sleep(1500); try { Directory.Delete(d, true); } catch { } }); }
    }
}

// ---------------------------------------------------------------------------
//  Çizimli denetimler
// ---------------------------------------------------------------------------
class FlatBtn : Control
{
    public Color Fill = T.Card, Fill2 = Color.Empty; public bool Ghost = false; bool hover, down;
    public FlatBtn()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand; Font = new Font("Segoe UI Semibold", 10.5f);
    }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.No; hover = false; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Parent != null ? Parent.BackColor : T.Bg); g.ScaleTransform(T.S, T.S);
        float w = Width / T.S, h = Height / T.S;
        var r = new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f);
        int lift = hover ? 22 : 0; if (down) lift = -14;
        using (var p = T.Round(r, 11))
        {
            if (Ghost)
            {
                using (var b = new SolidBrush(hover ? T.Lighten(T.Card, 10) : T.Card)) g.FillPath(b, p);
                using (var pen = new Pen(hover ? T.Accent : T.Line, 1.2f)) g.DrawPath(pen, p);
            }
            else if (!Enabled)
            {
                using (var lg = new LinearGradientBrush(r, Color.FromArgb(70, 78, 98), Color.FromArgb(56, 63, 82), 20f)) g.FillPath(lg, p);
                using (var pen = new Pen(T.A(Color.White, 18), 1f)) g.DrawPath(pen, p);
            }
            else
            {
                Color c1 = T.Lighten(Fill, lift), c2 = T.Lighten(Fill2.IsEmpty ? T.Lighten(Fill, -30) : Fill2, lift);
                using (var lg = new LinearGradientBrush(r, c1, c2, 20f)) g.FillPath(lg, p);
                using (var pen = new Pen(T.A(Color.White, hover ? 70 : 35), 1f)) g.DrawPath(pen, p);
            }
        }
        T.Tx(g, Text, Font.SizeInPoints, true, !Ghost && !Enabled ? Color.FromArgb(150, 158, 178) : Ghost ? T.Text : Color.White, 0, 0, w, h, 1, true, false);
    }
}

class Switch : Control
{
    public string Sub = ""; bool on; bool hover;
    public event EventHandler Changed;
    public bool Checked { get { return on; } set { on = value; Invalidate(); } }
    public Switch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand; Font = new Font("Segoe UI Semibold", 10.5f);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { on = !on; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Parent != null ? Parent.BackColor : T.Bg); g.ScaleTransform(T.S, T.S);
        float w = Width / T.S, h = Height / T.S;
        using (var p = T.Round(new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f), 11)) using (var b = new SolidBrush(hover ? T.Lighten(T.Card, 8) : T.Card)) using (var pen = new Pen(on ? T.A(T.Accent, 170) : T.Line, 1.2f))
        { g.FillPath(b, p); g.DrawPath(pen, p); }
        T.Tx(g, Text, 10.5f, true, T.Text, 12, 8, w - 70, 20, 0, false, false);
        T.Tx(g, Sub, 8.25f, false, T.Muted, 12, 28, w - 70, 16, 0, false, false);
        var pill = new RectangleF(w - 52, (h - 22) / 2f, 40, 22);
        using (var p = T.Round(pill, 11))
        {
            if (on) using (var lg = new LinearGradientBrush(pill, T.Accent, Color.FromArgb(34, 211, 238), 0f)) g.FillPath(lg, p);
            else using (var b = new SolidBrush(Color.FromArgb(58, 68, 92))) g.FillPath(b, p);
        }
        float kx = on ? pill.Right - 20 : pill.X + 2;
        using (var b = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillEllipse(b, kx, pill.Y + 3, 18, 18);
        g.FillEllipse(Brushes.White, kx, pill.Y + 2, 18, 18);
    }
}

class Card : Control
{
    readonly App a;
    public Card(App app) { a = app; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e) { a.PaintCard(e.Graphics, ClientSize); }
}

// ---------------------------------------------------------------------------
//  Sağ alt köşedeki canlı görünüm (tıklanamaz, odak almaz)
// ---------------------------------------------------------------------------
class Overlay : Form
{
    public volatile string Title = "", Sub = ""; public volatile Cdp Cdp; public volatile Ph Phase = Ph.Idle;
    Image frame; readonly object fl = new object();
    System.Windows.Forms.Timer tm; Thread cap; volatile bool stop; float anim;
    const int W = 448, H = 328, BX = 16, BY = 52, BW = 416, BH = 260;

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20; return cp; } // araç penceresi, odaksız, fareyi geçirir
    }

    public Overlay()
    {
        float S = T.S; int pw = (int)Math.Round(W * S), ph = (int)Math.Round(H * S);
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(pw, ph); BackColor = T.Bg; Opacity = 0.98; DoubleBuffered = true;
        var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - pw - (int)(16 * S), wa.Bottom - ph - (int)(16 * S));
        using (var p = T.Round(new RectangleF(0, 0, pw, ph), 16 * S)) Region = new Region(p);
        tm = new System.Windows.Forms.Timer { Interval = 16 }; tm.Tick += (s, e) => { anim += 0.012f; if (anim > 1) anim -= 1; if (Visible) Invalidate(); };
        tm.Start();
        cap = new Thread(CaptureLoop) { IsBackground = true }; cap.Start();
    }

    void CaptureLoop()
    {
        while (!stop)
        {
            int wait = 330;
            try
            {
                var c = Cdp;
                if (Visible && c != null && c.Alive)
                {
                    var b = c.Screenshot();
                    if (b != null)
                    {
                        var img = Image.FromStream(new MemoryStream(b));
                        lock (fl) { if (frame != null) frame.Dispose(); frame = img; }
                    }
                    wait = 100;
                }
                else if (c == null) lock (fl) { if (frame != null) { frame.Dispose(); frame = null; } }
            }
            catch { }
            Thread.Sleep(wait);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { stop = true; tm.Stop(); base.OnFormClosed(e); }

    Color Tone()
    {
        switch (Phase) { case Ph.Done: return T.Green; case Ph.Warn: case Ph.Reset: return T.Amber; case Ph.Stopped: case Ph.Idle: return T.Muted; default: return T.Accent; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(T.Bg); g.ScaleTransform(T.S, T.S);
        Color tone = Tone();
        float pulse = 0.5f + 0.5f * (float)Math.Sin(anim * Math.PI * 2);
        var all = new RectangleF(0.5f, 0.5f, W - 1.5f, H - 1.5f);
        using (var p = T.Round(all, 16))
        {
            using (var lg = new LinearGradientBrush(all, Color.FromArgb(24, 32, 52), Color.FromArgb(13, 17, 27), 70f)) g.FillPath(lg, p);
            using (var pen = new Pen(T.A(tone, (int)(70 + 120 * pulse)), 1.6f)) g.DrawPath(pen, p);
        }
        // başlık
        using (var b = new SolidBrush(T.A(tone, (int)(50 + 100 * pulse)))) g.FillEllipse(b, 14 - 3 * pulse, 15 - 3 * pulse, 20 + 6 * pulse, 20 + 6 * pulse);
        using (var b = new SolidBrush(tone)) g.FillEllipse(b, 19, 20, 10, 10);
        T.Tx(g, Title, 10.5f, true, T.Text, 40, 9, W - 56, 20, 0, false, false);
        T.Tx(g, Sub, 8.25f, false, T.Muted, 40, 28, W - 56, 16, 0, false, false);

        var body = new RectangleF(BX, BY, BW, BH);
        var oldClip = g.Clip;
        using (var p = T.Round(body, 10)) { using (var b = new SolidBrush(Color.FromArgb(10, 13, 21))) g.FillPath(b, p); g.SetClip(p); }
        Image img; lock (fl) { img = frame; if (img != null) try { g.DrawImage(img, body); } catch { } }
        var c = Cdp;
        if (img == null)
        {
            float cx = BX + BW / 2f, cy = BY + BH / 2f - 14;
            for (int k = 0; k < 2; k++)
            {
                float t = (anim + k * 0.5f) % 1f, rr = 22 + 34 * t;
                using (var pen = new Pen(T.A(tone, (int)(110 * (1 - t))), 2f)) g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            }
            using (var pen = new Pen(T.A(tone, 60), 4)) g.DrawEllipse(pen, cx - 22, cy - 22, 44, 44);
            using (var pen = new Pen(tone, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, cx - 22, cy - 22, 44, 44, anim * 720, 100);
            T.Tx(g, Sub.Length > 0 ? Sub : Title, 9.5f, false, T.Muted, BX + 10, cy + 36, BW - 20, 40, 1, false, true);
        }
        if (c != null && c.Alive && img != null)
        {
            float sc = BW / (float)Cdp.VW, x = BX + c.CurX * sc, y = BY + c.CurY * sc;
            long dt = unchecked(Environment.TickCount - c.ClickTicks);
            if (c.ClickTicks != 0 && dt >= 0 && dt < 700)
            {
                float t = dt / 700f, r = 6 + 36 * t;
                using (var pen = new Pen(T.A(T.Cursor, (int)(230 * (1 - t))), 3)) g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
                using (var b = new SolidBrush(T.A(T.Cursor, (int)(80 * (1 - t))))) g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
            }
            using (var b = new SolidBrush(T.A(T.Cursor, (int)(40 + 30 * pulse)))) g.FillEllipse(b, x - 14, y - 14, 28, 28);
            var pts = new[] { new PointF(0, 0), new PointF(0, 19), new PointF(5, 14.5f), new PointF(8.5f, 22), new PointF(12, 20.5f), new PointF(8.5f, 13.5f), new PointF(15, 13.5f) };
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(x + pts[i].X * 1.15f + 1.5f, y + pts[i].Y * 1.15f + 1.5f);
            using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillPolygon(b, pts);
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(pts[i].X - 1.5f, pts[i].Y - 1.5f);
            using (var b = new SolidBrush(T.Cursor)) g.FillPolygon(b, pts);
            using (var pen = new Pen(Color.White, 1.6f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, pts);
            string act = c.Action;
            if (!string.IsNullOrEmpty(act))
            {
                float tw = T.Measure(g, act, 8.5f, true);
                var chip = new RectangleF(BX + 8, BY + BH - 30, tw + 24, 22);
                using (var p = T.Round(chip, 11)) using (var b = new SolidBrush(Color.FromArgb(215, 13, 17, 27))) g.FillPath(b, p);
                using (var b = new SolidBrush(T.Cursor)) g.FillEllipse(b, chip.X + 8, chip.Y + 8, 6, 6);
                T.Tx(g, act, 8.5f, true, Color.White, chip.X + 18, chip.Y, tw + 8, 22, 0, true, false);
            }
        }
        g.Clip = oldClip;
        using (var p = T.Round(body, 10)) using (var pen = new Pen(T.Line)) g.DrawPath(pen, p);
    }
}

// ---------------------------------------------------------------------------
//  Ana pencere
// ---------------------------------------------------------------------------
class App : Form
{
    const string Ssid = "GSBWIFI";
    const int TickMs = 4000;
    static readonly string PortalUrl = Environment.GetEnvironmentVariable("GSB_PORTAL") ?? "https://wifi.gsb.gov.tr/login.html";
    const string PortalPost = "https://wifi.gsb.gov.tr/j_spring_security_check";

    RichTextBox box; FlatBtn btn, btnHide, btnCred; Switch swView; Card card; Label logHead;
    NotifyIcon tray; System.Windows.Forms.Timer timer, animTimer, watch;
    int attempts = 0, resets = 0, loginFails = 0, missCount = 0; float anim = 0;
    bool running = false, busy = false, done = false, viewOn = false;
    volatile bool cancel = false;
    Ph ph = Ph.Idle; string title = "Hazır", detail = "Başlat'a basınca GSBWIFI'ye bağlanmayı dener."; int step = -1;
    Stopwatch sw = new Stopwatch();
    Overlay ov; Cdp cdp;

    static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSBWifiOtomatik"); } }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread] static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        using (var gg = Graphics.FromHwnd(IntPtr.Zero)) T.S = gg.DpiX / 96f;
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
        Application.EnableVisualStyles(); Application.Run(new App());
    }

    App()
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = "GSBWIFI Otomatik Bağlantı"; ClientSize = new Size(800, 560); BackColor = T.Bg; ForeColor = T.Text; DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = appIcon;

        var side = new Panel { Left = 0, Top = 0, Width = 220, Height = 560, BackColor = T.Side };
        var brand = new PictureBox { Left = 20, Top = 22, Width = 44, Height = 44, SizeMode = PictureBoxSizeMode.Zoom, BackColor = T.Side };
        try { brand.Image = new Icon(appIcon, 48, 48).ToBitmap(); } catch { }
        var brandT = new Label { Left = 72, Top = 24, AutoSize = true, Text = "GSB WiFi", Font = new Font("Segoe UI Semibold", 13.5f), ForeColor = T.Text, BackColor = T.Side };
        var brandS = new Label { Left = 74, Top = 48, AutoSize = true, Text = "Otomatik bağlantı", Font = new Font("Segoe UI", 8.5f), ForeColor = T.Muted, BackColor = T.Side };
        btn = new FlatBtn { Left = 20, Top = 100, Width = 180, Height = 50, Text = "Başlat", Fill = T.Green, Fill2 = Color.FromArgb(14, 165, 160), BackColor = T.Side, Font = new Font("Segoe UI Semibold", 12f) };
        var secHead = new Label { Left = 22, Top = 176, AutoSize = true, Text = "SEÇENEKLER", Font = new Font("Segoe UI Semibold", 8f), ForeColor = T.Muted, BackColor = T.Side };
        swView = new Switch { Left = 20, Top = 198, Width = 180, Height = 54, Text = "Görüntü", Sub = "Sağ altta canlı göster", BackColor = T.Side };
        btnCred = new FlatBtn { Left = 20, Top = 440, Width = 180, Height = 40, Text = "Giriş bilgileri", Ghost = true, BackColor = T.Side };
        btnHide = new FlatBtn { Left = 20, Top = 492, Width = 180, Height = 40, Text = "Arka plana al", Ghost = true, BackColor = T.Side };
        side.Controls.AddRange(new Control[] { brand, brandT, brandS, btn, secHead, swView, btnCred, btnHide });

        card = new Card(this) { Left = 244, Top = 24, Width = 532, Height = 252, BackColor = T.Bg };
        logHead = new Label { Left = 246, Top = 292, AutoSize = true, Text = "KAYIT", Font = new Font("Segoe UI Semibold", 8f), ForeColor = T.Muted, BackColor = T.Bg };
        var logHost = new Panel { Left = 244, Top = 314, Width = 532, Height = 222, BackColor = T.Card, Padding = new Padding(14, 12, 10, 10) };
        logHost.Resize += (s, e) => { using (var p = T.Round(new RectangleF(0, 0, logHost.Width, logHost.Height), 14)) logHost.Region = new Region(p); };
        box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = T.Card, ForeColor = Color.FromArgb(190, 200, 220), Font = new Font("Consolas", 9.5f) };
        logHost.Controls.Add(box);
        Controls.AddRange(new Control[] { side, card, logHead, logHost });

        tray = new NotifyIcon { Icon = appIcon, Text = "GSBWIFI Otomatik", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Aç", null, (s, e) => ShowMain());
        menu.Items.Add("Çıkış", null, (s, e) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowMain();

        timer = new System.Windows.Forms.Timer { Interval = TickMs };
        timer.Tick += async (s, e) => await Tick();
        watch = new System.Windows.Forms.Timer { Interval = 4000 };
        watch.Tick += async (s, e) => await WatchTick();
        animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        animTimer.Tick += (s, e) => { anim += 0.011f; if (anim > 1) anim -= 1; if (Visible && WindowState != FormWindowState.Minimized) card.Invalidate(); };
        animTimer.Start();

        btn.Click += (s, e) => { if (running) StopRun(); else { var _t = TryStart(); } };
        btnHide.Click += (s, e) => Hide();
        btnCred.Click += (s, e) => FirstSetup();
        swView.Changed += (s, e) => { viewOn = swView.Checked; SaveSettings(); RefreshOverlay(); };
        FormClosing += (s, e) => { cancel = true; running = false; watch.Stop(); };
        FormClosed += (s, e) => { tray.Visible = false; tray.Dispose(); if (ov != null) ov.Close(); CloseBrowser(); };

        if (T.S != 1f) Scale(new SizeF(T.S, T.S));
        LoadSettings(); swView.Checked = viewOn;
        Shown += (s, e) => {
#if SELFTEST
            viewOn = true; swView.Checked = true; running = true; sw.Start(); RefreshOverlay(); UpdateButton();
            Task.Run(() => { string r = BrowserLogin("deneme.kullanici", "gizli123"); Log("SELFTEST sonuç: " + r); });
            return;
#else
            string u0, p0; bool has = LoadCred(out u0, out p0);
            if (credMode == "" && has) { credMode = "manual"; SaveSettings(); }
            if (credMode == "" || (credMode == "manual" && !has)) FirstSetup();
            var _t = TryStart();
#endif
        };
    }

    // ---- ayarlar ----
    string credMode = ""; // "" = sorulmadı, "chrome" = Chrome'da kayıtlı, "manual" = uygulamaya girildi
    void LoadSettings()
    {
        try {
            string t = File.ReadAllText(Path.Combine(DataDir, "ayar.txt"));
            viewOn = t.Contains("view=1");
            var m = Regex.Match(t, @"mode=(\w+)"); if (m.Success) credMode = m.Groups[1].Value;
        } catch { }
    }
    void SaveSettings() { try { Directory.CreateDirectory(DataDir); File.WriteAllText(Path.Combine(DataDir, "ayar.txt"), "view=" + (viewOn ? "1" : "0") + "\nmode=" + credMode); } catch { } }

    // İlk kurulum / "Giriş bilgileri" düğmesi: bilgiler Chrome'da mı kayıtlı, yoksa elle mi girilecek?
    void FirstSetup()
    {
        int m = AskMode(this);
        if (m == 1) { credMode = "chrome"; SaveSettings(); Log("Chrome'da kayıtlı bilgiler kullanılacak."); }
        else if (m == 2) {
            if (AskCred(this)) { credMode = "manual"; SaveSettings(); Log("Giriş bilgileri kaydedildi."); }
            else Log("Giriş bilgisi girilmedi.");
        }
    }

    void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    // ---- durum ----
    void Log(string m)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(m))); return; }
        Color c = Color.FromArgb(190, 200, 220);
        if (Regex.IsMatch(m, "internet var|açıldı|tıklandı|kaydedildi|Başladı", RegexOptions.IgnoreCase)) c = Color.FromArgb(110, 231, 160);
        else if (Regex.IsMatch(m, "hata|açılmadı|yanlış|bulunamadı|kapat/aç|gelmedi", RegexOptions.IgnoreCase)) c = Color.FromArgb(251, 191, 100);
        box.SelectionStart = box.TextLength; box.SelectionColor = T.Muted; box.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] ");
        box.SelectionColor = c; box.AppendText(m + Environment.NewLine); box.SelectionStart = box.TextLength; box.ScrollToCaret();
    }

    void SetState(Ph p, string t, string d)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetState(p, t, d))); return; }
        ph = p; title = t; detail = d;
        switch (p) { case Ph.Connect: case Ph.Reset: step = 0; break; case Ph.Page: step = 1; break; case Ph.Login: step = 2; break; case Ph.Done: step = 4; break; }
        string tt = t + " - " + d; tray.Text = tt.Length > 60 ? tt.Substring(0, 60) : tt;
        if (ov != null) { ov.Title = t; ov.Sub = d; ov.Phase = p; }
        card.Invalidate();
    }

    void UpdateButton()
    {
        btn.Text = running ? "Durdur" : "Başlat"; btn.Fill = running ? T.Red : T.Green; btn.Fill2 = running ? Color.FromArgb(249, 115, 22) : Color.FromArgb(14, 165, 160); btn.Invalidate();
    }

    void RefreshOverlay()
    {
        if (viewOn && running)
        {
            if (ov == null || ov.IsDisposed) ov = new Overlay();
            ov.Title = title; ov.Sub = detail; ov.Phase = ph; ov.Cdp = cdp;
            if (!ov.Visible) ov.Show();
        }
        else if (ov != null && ov.Visible) ov.Hide();
    }

    async Task TryStart()
    {
        btn.Enabled = false; watch.Stop();
        SetState(Ph.Connect, "Wi-Fi kontrol ediliyor", "Kapalıysa açılır, sonra " + Ssid + " aranır");
        bool vis = false, changed = false;
        await Task.Run(() => {
            changed = EnsureWifiOn();
            for (int i = 0; i < (changed ? 6 : 1) && !vis; i++) { vis = NetworkVisible(); if (!vis && changed) Thread.Sleep(2000); }
        });
        if (changed) Log("Wi-Fi kapalıydı, otomatik açıldı.");
        if (cancel && !running && ph == Ph.Stopped) { btn.Enabled = true; return; }
        if (vis) { btn.Enabled = true; StartRun(); } else EnterWait();
    }

    // Ağ görünmüyor: Başlat gri, hata ekranda; ağ çıkınca otomatik açılır
    void EnterWait()
    {
        running = false; step = -1; sw.Stop(); btn.Enabled = false; UpdateButton();
        SetState(Ph.Warn, Ssid + " ağı bulunamadı", "Wi-Fi listesinde " + Ssid + " görünmüyor. Ağın menzilinde olduğundan emin ol. Görününce Başlat açılacak.");
        Log(Ssid + " Wi-Fi listesinde yok. Bekleniyor...");
        RefreshOverlay(); watch.Start();
    }

    async Task WatchTick()
    {
        if (running) { watch.Stop(); return; }
        bool vis = false;
        await Task.Run(() => { vis = NetworkVisible(); });
        if (!vis || running) return;
        watch.Stop(); btn.Enabled = true;
        SetState(Ph.Idle, Ssid + " bulundu", "Başlat'a basarak bağlanmayı başlatabilirsin.");
        Log(Ssid + " ağı göründü. Başlat'a basabilirsin.");
    }

    void StartRun()
    {
        running = true; cancel = false; done = false; loginFails = 0;
        sw.Start(); UpdateButton(); Log("Başladı. Hedef: " + Ssid);
        SetState(Ph.Connect, "Başlıyor", Ssid + " kontrol ediliyor");
        RefreshOverlay();
        timer.Start(); var _ = Tick();
    }

    void StopRun()
    {
        running = false; cancel = true; timer.Stop(); sw.Stop(); UpdateButton();
        SetState(Ph.Stopped, "Durduruldu", "Yeniden başlatmak için Başlat'a bas.");
        Log("Durduruldu.");
        RefreshOverlay();
        Task.Run(() => CloseBrowser());
    }

    void CloseBrowser()
    {
        var c = cdp; cdp = null;
        if (ov != null) ov.Cdp = null;
        if (c != null) c.Dispose();
    }

    // ---- çizim ----
    public void PaintCard(Graphics g, Size px)
    {
        float S = T.S;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.Clear(T.Bg); g.ScaleTransform(S, S);
        var sz = new SizeF(px.Width / S, px.Height / S);
        bool active = ph == Ph.Connect || ph == Ph.Reset || ph == Ph.Page || ph == Ph.Login;
        Color tone = ph == Ph.Done ? T.Green : (ph == Ph.Warn || ph == Ph.Reset) ? T.Amber : (ph == Ph.Stopped || ph == Ph.Idle) ? T.Muted : T.Accent;
        double ang = anim * Math.PI * 2;
        var rect = new RectangleF(0.5f, 0.5f, sz.Width - 1.5f, sz.Height - 1.5f);
        using (var p = T.Round(rect, 18))
        {
            using (var lg = new LinearGradientBrush(rect, Color.FromArgb(29, 39, 64), Color.FromArgb(19, 25, 41), 55f)) g.FillPath(lg, p);
            var old = g.Clip; g.SetClip(p);
            // arkada kayan renkli ışıklar
            DrawGlow(g, 70 + (float)Math.Sin(ang) * 14, 76 + (float)Math.Cos(ang) * 8, 190, tone, active ? 78 : 44);
            DrawGlow(g, sz.Width - 70 + (float)Math.Cos(ang) * 30, sz.Height - 40 + (float)Math.Sin(ang) * 14, 170, Color.FromArgb(139, 92, 246), 34);
            g.Clip = old;
            using (var pen = new Pen(T.A(tone, active ? 90 : 50), 1.3f)) g.DrawPath(pen, p);
        }

        float cx = 72, cy = 76, r = 32;
        if (active)
            for (int k = 0; k < 2; k++)
            {
                float t = (anim + k * 0.5f) % 1f, pr = r + 2 + 16 * t;
                using (var pen = new Pen(T.A(tone, (int)(140 * (1 - t))), 2.5f)) g.DrawEllipse(pen, cx - pr, cy - pr, pr * 2, pr * 2);
            }
        var orb = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var lg = new LinearGradientBrush(orb, T.A(tone, 110), T.A(tone, 25), 60f)) g.FillEllipse(lg, orb);
        using (var pen = new Pen(tone, 3)) g.DrawEllipse(pen, orb);
        if (active)
        {
            using (var pen = new Pen(T.A(tone, 70), 4)) g.DrawEllipse(pen, cx - 15, cy - 15, 30, 30);
            using (var pen = new Pen(tone, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, cx - 15, cy - 15, 30, 30, anim * 720, 110);
        }
        else if (ph == Ph.Done)
            using (var pen = new Pen(Color.White, 4.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) g.DrawLines(pen, new[] { new PointF(cx - 12, cy + 1), new PointF(cx - 3, cy + 10), new PointF(cx + 13, cy - 9) });
        else if (ph == Ph.Warn)
            T.Tx(g, "!", 24, true, tone, cx - r, cy - r, r * 2, r * 2, 1, true, false);
        else
            using (var b = new SolidBrush(tone)) { g.FillRectangle(b, cx - 10, cy - 11, 7, 22); g.FillRectangle(b, cx + 3, cy - 11, 7, 22); }

        T.Tx(g, title, 18f, true, T.Text, 128, 44, sz.Width - 148, 34, 0, false, false);
        T.Tx(g, detail, 10.5f, false, T.Muted, 128, 82, sz.Width - 148, 46, 0, false, true);

        // adımlar
        string[] names = { "Wi-Fi", "Giriş sayfası", "Giriş", "İnternet" };
        float x0 = 56, x1 = sz.Width - 56, y = 164;
        for (int i = 0; i < 3; i++)
        {
            float xa = x0 + (x1 - x0) * i / 3f + 12, xb = x0 + (x1 - x0) * (i + 1) / 3f - 12;
            using (var pen = new Pen(step > i ? T.Green : T.Line, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, xa, y, xb, y);
            if (active && step == i)
            {
                // ilerleyen ışık
                float len = 34, px0 = xa + (xb - xa - len) * anim;
                var seg = new RectangleF(px0, y - 3, len, 6);
                using (var lg = new LinearGradientBrush(seg, T.A(tone, 0), tone, 0f) { WrapMode = WrapMode.TileFlipX }) using (var pen = new Pen(lg, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, px0, y, px0 + len, y);
            }
        }
        for (int i = 0; i < 4; i++)
        {
            float x = x0 + (x1 - x0) * i / 3f;
            bool complete = step > i, current = step == i && active;
            Color nc = complete ? T.Green : current ? tone : T.Line;
            if (current) using (var b = new SolidBrush(T.A(tone, (int)(40 + 40 * Math.Sin(ang))))) g.FillEllipse(b, x - 18, y - 18, 36, 36);
            using (var b = new SolidBrush(complete ? nc : T.Card)) g.FillEllipse(b, x - 12, y - 12, 24, 24);
            using (var pen = new Pen(nc, 2.5f)) g.DrawEllipse(pen, x - 12, y - 12, 24, 24);
            if (complete) using (var pen = new Pen(Color.White, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, new[] { new PointF(x - 5, y), new PointF(x - 1.5f, y + 4), new PointF(x + 5, y - 4) });
            else if (current) using (var b = new SolidBrush(nc)) { float k = 4 + 2 * (float)Math.Sin(ang); g.FillEllipse(b, x - k, y - k, k * 2, k * 2); }
            T.Tx(g, names[i], 9f, complete || current, (complete || current) ? T.Text : T.Muted, x - 60, y + 17, 120, 20, 1, false, false);
        }

        // sayaçlar
        var el = sw.Elapsed; string time = ((int)el.TotalMinutes).ToString("00") + ":" + el.Seconds.ToString("00");
        string[] lab = { "DENEME", "WI-FI YENİLEME", "SÜRE" }, val = { attempts.ToString(), resets.ToString(), time };
        float cw = (sz.Width - 40) / 3f;
        for (int i = 0; i < 3; i++)
        {
            float x = 24 + cw * i;
            T.Tx(g, lab[i], 7.5f, true, T.Muted, x, 204, cw, 14, 0, false, false);
            T.Tx(g, val[i], 14f, true, T.Text, x, 218, cw, 26, 0, false, false);
        }
    }

    static void DrawGlow(Graphics g, float cx, float cy, float rad, Color c, int alpha)
    {
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
            using (var pb = new PathGradientBrush(gp) { CenterColor = T.A(c, alpha), SurroundColors = new[] { T.A(c, 0) } }) g.FillPath(pb, gp);
        }
    }

    // ---- netsh / internet ----
    static string Netsh(string args)
    {
        try {
            var p = Process.Start(new ProcessStartInfo("netsh", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 });
            string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o;
        } catch { return ""; }
    }
    // Hedef ağ Wi-Fi listesinde görünüyor mu?
    static bool NetworkVisible()
    {
        foreach (var l in Netsh("wlan show networks").Split('\n')) {
            var m = Regex.Match(l, @"^\s*SSID\s+\d+\s*:\s*(.+?)\s*$");
            if (m.Success && string.Equals(m.Groups[1].Value, Ssid, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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

    // ---- kimlik bilgisi (DPAPI) ----
    static string CredPath { get { return Path.Combine(DataDir, "kimlik.dat"); } }
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
    // 1 = Chrome'da kayıtlı, 2 = elle girilecek, 0 = vazgeçti
    static int AskMode(IWin32Window owner)
    {
        int result = 0;
        using (var f = new Form { Text = "GSBWIFI giriş bilgileri", ClientSize = new Size(470, 300), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = T.Bg, ForeColor = T.Text })
        {
            var head = new Label { Text = "Giriş sayfasındaki bilgilerin nerede?", Location = new Point(20, 18), AutoSize = true, Font = new Font("Segoe UI Semibold", 12.5f), ForeColor = T.Text };
            var b1 = new FlatBtn { Text = "Chrome'da bilgilerim kayıtlı", Location = new Point(20, 62), Size = new Size(430, 46), Fill = T.Accent, Fill2 = Color.FromArgb(34, 211, 238), BackColor = T.Bg };
            var h1 = new Label { Text = "wifi.gsb.gov.tr'de kullanıcı adı ve şifre Chrome'dan kendiliğinden doluyor. Bir şey girmene gerek kalmaz.", Location = new Point(22, 112), Size = new Size(426, 38), ForeColor = T.Muted };
            var b2 = new FlatBtn { Text = "Bilgilerimi gireyim", Location = new Point(20, 164), Size = new Size(430, 46), Ghost = true, BackColor = T.Bg };
            var h2 = new Label { Text = "Bir kez sorulur. Bilgiler bu bilgisayarda, sadece senin Windows hesabınla açılabilecek şekilde şifreli saklanır.", Location = new Point(22, 214), Size = new Size(426, 38), ForeColor = T.Muted };
            b1.Click += (s, e) => { result = 1; f.Close(); };
            b2.Click += (s, e) => { result = 2; f.Close(); };
            f.Controls.AddRange(new Control[] { head, b1, h1, b2, h2 });
            if (T.S != 1f) f.Scale(new SizeF(T.S, T.S));
            f.ShowDialog(owner);
        }
        return result;
    }

    static bool AskCred(IWin32Window owner)
    {
        using (var f = new Form { Text = "GSBWIFI giriş bilgileri", ClientSize = new Size(360, 214), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = T.Bg, ForeColor = T.Text })
        {
            var tu = new TextBox { Location = new Point(124, 22), Size = new Size(210, 26), BackColor = T.Card, ForeColor = T.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10) };
            var tp = new TextBox { Location = new Point(124, 62), Size = new Size(210, 26), UseSystemPasswordChar = true, BackColor = T.Card, ForeColor = T.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10) };
            string u, p; if (LoadCred(out u, out p)) tu.Text = u;
            var ok = new FlatBtn { Text = "Kaydet", Location = new Point(124, 156), Size = new Size(100, 38), Fill = T.Accent, BackColor = T.Bg };
            var cancel = new FlatBtn { Text = "Vazgeç", Location = new Point(234, 156), Size = new Size(100, 38), Ghost = true, BackColor = T.Bg };
            ok.Click += (s, e) => f.DialogResult = DialogResult.OK; cancel.Click += (s, e) => f.DialogResult = DialogResult.Cancel;
            var note = new Label { Text = "Bilgiler bu bilgisayarda, sadece senin Windows hesabınla açılabilecek şekilde şifreli saklanır.", Location = new Point(14, 102), Size = new Size(330, 40), ForeColor = T.Muted };
            f.Controls.AddRange(new Control[] { new Label { Text = "Kullanıcı adı", Location = new Point(14, 26), AutoSize = true }, new Label { Text = "Şifre", Location = new Point(14, 66), AutoSize = true }, tu, tp, note, ok, cancel });
            f.AcceptButton = null; f.CancelButton = null;
            if (T.S != 1f) f.Scale(new SizeF(T.S, T.S));
            if (f.ShowDialog(owner) != DialogResult.OK || tu.Text.Trim() == "" || tp.Text == "") return false;
            SaveCred(tu.Text.Trim(), tp.Text); return true;
        }
    }

    // Chrome'da kayıtlı şifreler için: kullanıcının Chrome profilinden gerekli dosyalar geçici profile kopyalanır (silinir)
    static void CopyShared(string a, string b)
    {
        try {
            if (!File.Exists(a)) return;
            using (var i = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var o = File.Create(b)) i.CopyTo(o);
        } catch { }
    }
    static void SeedChromeProfile(string dest)
    {
        string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data"), prof = "Default";
        try {
            var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(src, "Local State"))) as Dictionary<string, object>;
            var pr = d != null && d.ContainsKey("profile") ? d["profile"] as Dictionary<string, object> : null;
            if (pr != null && pr.ContainsKey("last_used")) prof = (string)pr["last_used"];
        } catch { }
        CopyShared(Path.Combine(src, "Local State"), Path.Combine(dest, "Local State"));
        Directory.CreateDirectory(Path.Combine(dest, "Default"));
        foreach (var f in new[] { "Login Data", "Login Data For Account", "Preferences", "Web Data" }) CopyShared(Path.Combine(src, prof, f), Path.Combine(dest, "Default", f));
    }

    // Wi-Fi radyosu ya da kartı kapalıysa açar. Kapalıydıysa true döner.
    static bool EnsureWifiOn()
    {
        const string ps = @"
$ErrorActionPreference='SilentlyContinue'
$was=$false
try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $asTask=([System.WindowsRuntimeSystemExtensions].GetMethods() | ? { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  function Await($op,$t){ $n=$asTask.MakeGenericMethod($t).Invoke($null,@($op)); $n.Wait(-1)|Out-Null; $n.Result }
  [Windows.Devices.Radios.Radio,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null
  [Windows.Devices.Radios.RadioAccessStatus,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null
  Await ([Windows.Devices.Radios.Radio]::RequestAccessAsync()) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null
  $radios=Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])
  foreach($r in $radios){ if($r.Kind -eq 'WiFi' -and $r.State -ne 'On'){ $was=$true; Await ($r.SetStateAsync('On')) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null } }
} catch {}
try { Get-NetAdapter -Physical | ? { $_.Status -eq 'Disabled' -and ($_.PhysicalMediaType -match '802.11' -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless') } | % { $was=$true; Enable-NetAdapter -Name $_.Name -Confirm:$false } } catch {}
if($was){'WAS_OFF'}
";
        try {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(ps))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }
            return o.Contains("WAS_OFF");
        } catch { return false; }
    }

    // ---- Chrome ile giriş ----
    // Dönüş: ok | nopage | failed | nochrome | cancel
    string BrowserLogin(string user, string pass)
    {
        string exe = Cdp.FindBrowser();
        if (exe == null) return "nochrome";
        bool chromeMode = user == null;
        if (chromeMode && !exe.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase)) return "nochrome";
        try
        {
            if (cdp == null || !cdp.Alive)
            {
                CloseBrowser();
                SetState(Ph.Page, "Tarayıcı hazırlanıyor", "Chrome arka planda açılıyor");
                var c = new Cdp(); c.Cancelled = () => cancel;
                if (user == null) c.Seed = SeedChromeProfile;
                Directory.CreateDirectory(DataDir);
                if (!c.Start(exe, DataDir)) { c.Dispose(); return cancel ? "cancel" : "nochrome"; }
                cdp = c; var o = ov; if (o != null) o.Cdp = c;
            }
            var b = cdp;
            SetState(Ph.Page, "Giriş sayfası açılıyor", PortalUrl.Replace("https://", ""));
            Log("Giriş sayfası Chrome'da açılıyor...");
            string err = b.Navigate(PortalUrl);
            if (err != null) { Log("Sayfa açılamadı (" + err + ")."); return "nopage"; }
            bool form = false;
            for (int i = 0; i < 40 && !cancel; i++)
            {
                object v = b.Eval("!!document.querySelector('input[type=password],input[name=j_password]')");
                if (v is bool && (bool)v) { form = true; break; }
                Thread.Sleep(300);
            }
            if (cancel) return "cancel";
            if (!form) { Log("Giriş formu bulunamadı."); return "nopage"; }
            Log("Giriş sayfası açıldı.");
            SetState(Ph.Login, "Giriş yapılıyor", "Alanlar dolduruluyor, Giriş'e tıklanacak");
            Thread.Sleep(500);
            const string userSel = "input[name=j_username],input[type=text],input[type=email]", passSel = "input[name=j_password],input[type=password]";
            if (chromeMode)
            {
                SetState(Ph.Login, "Chrome bilgileri dolduruyor", "Kayıtlı kullanıcı adı ve şifre bekleniyor");
                const string filledJs = "(function(){var p=document.querySelector('input[type=password],input[name=j_password]');if(!p)return false;if(p.value&&p.value.length>0)return true;try{return p.matches(':-webkit-autofill')||p.matches(':autofill');}catch(e){return false;}})()";
                bool filled = false;
                for (int i = 0; i < 20 && !cancel; i++)
                {
                    object v = b.Eval(filledJs);
                    if (v is bool && (bool)v) { filled = true; break; }
                    if (i == 8) { try { b.ClickSel(userSel, "Kullanıcı adı alanı"); b.Key("ArrowDown", 40); b.Key("Enter", 13); } catch { } }
                    Thread.Sleep(400);
                }
                if (cancel) return "cancel";
                if (!filled) { Log("Chrome alanları doldurmadı."); return "noautofill"; }
                Log("Chrome kayıtlı bilgileri getirdi.");
                b.ClickSel(userSel, "Kullanıcı adı alanı");
                b.ClickSel(passSel, "Şifre alanı");
            }
            else
            {
                if (!b.ClickSel(userSel, "Kullanıcı adı alanı")) return "nopage";
                b.TypeText(user, "Kullanıcı adı");
                if (!b.ClickSel(passSel, "Şifre alanı")) return "nopage";
                b.TypeText(pass, "Şifre");
            }
            if (cancel) return "cancel";
            if (!b.ClickSubmit()) return "nopage";
            Log("Giriş düğmesine tıklandı.");
            SetState(Ph.Login, "Giriş gönderildi", "İnternet bekleniyor");
            b.Action = "İnternet bekleniyor";
            for (int i = 0; i < 10 && !cancel; i++) { Thread.Sleep(1000); if (Internet()) return "ok"; }
            return cancel ? "cancel" : "failed";
        }
        catch (Exception ex)
        {
            if (cancel) return "cancel";
            Log("Tarayıcı hatası: " + ex.Message); CloseBrowser(); return "nopage";
        }
    }

    // Chrome yoksa eski yöntem: doğrudan form gönderimi
    string HttpLogin(string user, string pass)
    {
        try
        {
            var jar = new CookieContainer();
            bool reach;
            using (var resp = Req(PortalUrl, jar).GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) reach = sr.ReadToEnd().Contains("j_spring_security_check");
            if (!reach) return "nopage";
            var body = Encoding.UTF8.GetBytes("j_username=" + Uri.EscapeDataString(user) + "&j_password=" + Uri.EscapeDataString(pass) + "&submit=Login");
            var r = Req(PortalPost, jar);
            r.Method = "POST"; r.ContentType = "application/x-www-form-urlencoded"; r.Referer = PortalUrl; r.ContentLength = body.Length;
            using (var st = r.GetRequestStream()) st.Write(body, 0, body.Length);
            using (var resp = r.GetResponse()) { }
            return Internet() ? "ok" : "failed";
        }
        catch { return "nopage"; }
    }
    static HttpWebRequest Req(string url, CookieContainer jar)
    {
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.Timeout = 6000; r.CookieContainer = jar; r.Proxy = null; r.AllowAutoRedirect = true;
        r.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        return r;
    }

    // ---- Wi-Fi ----
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

    async Task Finish()
    {
        done = true; timer.Stop(); sw.Stop();
        SetState(Ph.Done, "Bağlandı, internet var!", "Pencere birkaç saniye sonra kapanacak.");
        Log("İnternet var. Kapanıyor.");
        tray.BalloonTipTitle = "GSBWIFI"; tray.BalloonTipText = "Bağlandı, internet var."; tray.ShowBalloonTip(3000);
        await Task.Delay(5000); Close();
    }

    // ---- ana döngü ----
    async Task Tick()
    {
        if (busy || !running || done) return;
        busy = true;
        try {
            string cur = null; bool ok = false;
            await Task.Run(() => { cur = CurrentSsid(); if (cur == Ssid) ok = Internet(); });
            if (!running) return;
            if (cur == Ssid) {
                if (ok) { await Finish(); return; }
                string u = null, p = null;
                if (credMode != "chrome" && !LoadCred(out u, out p)) {
                    SetState(Ph.Warn, "Giriş bilgisi gerekli", "'Giriş bilgileri' düğmesinden kullanıcı adı ve şifreni gir.");
                    Log("Kayıtlı giriş bilgisi yok."); return;
                }
                attempts++;
                SetState(Ph.Page, "İnternet yok, giriş sayfasına bakılıyor", Ssid + " bağlı ama internet gelmedi");
                string res = null;
                await Task.Run(() => { res = BrowserLogin(u, p); if (res == "nochrome" && u != null) { Log("Chrome bulunamadı, doğrudan giriş deneniyor."); res = HttpLogin(u, p); } });
                if (!running) return;
                if (res == "ok") { await Finish(); return; }
                if (res == "cancel") return;
                if (res == "noautofill" || (res == "nochrome" && credMode == "chrome")) {
                    StopRun();
                    SetState(Ph.Warn, "Bilgiler otomatik dolmadı", "Chrome kayıtlı bilgileri getirmedi. 'Giriş bilgileri'nden bilgilerini elle gir.");
                    Log("Chrome'da kayıtlı bilgiler bulunamadı, bilgileri elle girmen gerek."); return;
                }
                if (res == "failed") { loginFails++; Log("Giriş gönderildi ama internet gelmedi." + (loginFails >= 3 ? " Bilgiler yanlış olabilir, kontrol et." : "")); }
                else Log("Giriş sayfası açılmadı.");
                resets++;
                SetState(Ph.Reset, "Wi-Fi kapatılıp açılıyor", "Sayfa açılana kadar yeniden denenecek (#" + resets + ")");
                Log("Wi-Fi kapat/aç #" + resets);
                await Task.Run(() => ResetAdapter());
            } else {
                bool vis = true;
                await Task.Run(() => { vis = NetworkVisible(); });
                if (!running) return;
                if (!vis) {
                    if (missCount++ % 3 == 0) { bool ch = false; await Task.Run(() => { ch = EnsureWifiOn(); }); if (ch) Log("Wi-Fi kapalıydı, otomatik açıldı."); }
                    SetState(Ph.Warn, Ssid + " ağı bulunamadı", "Wi-Fi listesinde görünmüyor, aranmaya devam ediliyor...");
                    Log(Ssid + " listede yok, bekleniyor.");
                } else {
                    SetState(Ph.Connect, Ssid + "'e bağlanılıyor", "Şu an: " + (cur ?? "bağlı değil"));
                    Log("Şu an: " + (cur ?? "bağlı değil") + " -> " + Ssid + "'e bağlanılıyor");
                    await Task.Run(() => Connect());
                }
            }
        } catch (Exception ex) { Log("Hata: " + ex.Message); }
        finally { busy = false; }
    }
}
