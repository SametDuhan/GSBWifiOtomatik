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
using Microsoft.Win32;
using System.Management;

// ============================================================================
//  GSBWIFI Otomatik Bağlantı
//  - Ağa bağlanır, "internet yok" olursa Wi-Fi kartını kapatıp açar
//  - Giriş sayfasını Windows kendisi Chrome'da açar; uygulama sadece sayfa gelene kadar kapat/aç döngüsünü sürdürür
//  - İstenirse sağ altta canlı görünüm: ikinci bir sanal imleç + ekran görüntüsü
//    (gerçek fare imleci kullanılmaz, senin farene dokunulmaz)
// ============================================================================

enum Ph { Idle, Stopped, Connect, Reset, Page, Login, Done, Warn }

static class T
{
    public static readonly Color Bg = Color.FromArgb(13, 17, 27), Side = Color.FromArgb(19, 25, 39), Card = Color.FromArgb(23, 30, 47),
        Line = Color.FromArgb(40, 50, 74), Text = Color.FromArgb(233, 237, 246), Muted = Color.FromArgb(176, 187, 208),
        Accent = Color.FromArgb(59, 130, 246), Green = Color.FromArgb(34, 197, 94), Amber = Color.FromArgb(245, 158, 11),
        Red = Color.FromArgb(239, 68, 68), Cursor = Color.FromArgb(56, 150, 255);
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
    string act = "";
    public Action<string> OnAction; public Action<float, float> GhostMove; public Action GhostClick; public PointF? InitScreen;
    double ox = 0, oy = 0, dpr = 1; public bool Headed = false;
    public string Action { get { return act; } set { act = value; var h = OnAction; if (h != null) h(value); } }
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
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
                try { var v = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe); string path = v == null ? null : v.GetValue("") as string; if (!string.IsNullOrEmpty(path)) list.Add(path); } catch { }
        foreach (var p in list) if (File.Exists(p)) return p;
        return null;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    public bool Start(string exe, string baseDir, bool headed)
    {
        // her çalıştırmaya ayrı geçici profil: eski/yarım kalmış bir Chrome profili kilitleyemez
        try { foreach (var d in Directory.GetDirectories(baseDir, "chrome*")) try { Directory.Delete(d, true); } catch { } } catch { }
        string profile = Path.Combine(baseDir, "chrome-" + Process.GetCurrentProcess().Id + "-" + Environment.TickCount);
        profileDir = profile;
        if (Seed != null) { try { Directory.CreateDirectory(profile); Seed(profile); } catch { } }
        foreach (bool noSandbox in new[] { false, true })
        {
            int port = FreePort();
            Headed = headed;
            string args = (headed ? "--window-position=140,40 --window-size=1100,800" : "--headless=new --window-size=" + VW + "," + VH) + " --remote-debugging-port=" + port + " --remote-allow-origins=* --user-data-dir=\"" + profile + "\"" +
                " --no-first-run --no-default-browser-check --disable-gpu --disable-extensions --mute-audio" +
                " --disable-sync --disable-features=Translate --user-agent=\"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36\"" +
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
                    if (!headed) Call("Emulation.setDeviceMetricsOverride", new Dictionary<string, object> { { "width", VW }, { "height", VH }, { "deviceScaleFactor", 1 }, { "mobile", false } });
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
            var gm = GhostMove; if (gm != null && Headed) { gm((float)(ox + CurX * dpr), (float)(oy + CurY * dpr)); }
            Thread.Sleep(16);
        }
    }

    public void Click()
    {
        Mouse("mousePressed", CurX, CurY, "left", 1); ClickTicks = Environment.TickCount;
        var gc = GhostClick; if (gc != null && Headed) gc();
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

    // Sayfa koordinatını ekran koordinatına çevirmek için pencerenin ekrandaki yeri
    void RefreshMetrics()
    {
        if (!Headed) return;
        try {
            string m = Eval("[window.screenX,window.screenY,window.outerWidth,window.outerHeight,window.innerWidth,window.innerHeight,window.devicePixelRatio].join(',')") as string;
            if (m == null) return;
            var q = m.Split(','); var ic = System.Globalization.CultureInfo.InvariantCulture;
            double sx = double.Parse(q[0], ic), sy = double.Parse(q[1], ic), ow = double.Parse(q[2], ic), oh = double.Parse(q[3], ic), iw = double.Parse(q[4], ic), ih = double.Parse(q[5], ic);
            dpr = double.Parse(q[6], ic); double side = (ow - iw) / 2;
            ox = (sx + side) * dpr; oy = (sy + (oh - ih) - side) * dpr;
            if (InitScreen.HasValue) { CurX = (float)((InitScreen.Value.X - ox) / dpr); CurY = (float)((InitScreen.Value.Y - oy) / dpr); InitScreen = null; }
        } catch { }
    }

    bool ClickPos(string pos, string what)
    {
        if (string.IsNullOrEmpty(pos)) return false;
        var p = pos.Split(',');
        double x = double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), y = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
        Action = what + " için gidiliyor";
        RefreshMetrics();
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
    // Düğmenin en sağında ayrı tıklanan ayar (dişli) bölgesi
    public bool Gear = false, GearEnabled = true; public event EventHandler GearClick; bool hoverGear, downGear;
    bool InGear(int x) { return Gear && x >= Width - (int)(54 * T.S); }
    protected override void OnMouseMove(MouseEventArgs e) { bool g = InGear(e.X); if (g != hoverGear) { hoverGear = g; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnClick(EventArgs e)
    {
        if (Gear && downGear) { if (GearEnabled && GearClick != null) GearClick(this, EventArgs.Empty); return; }
        base.OnClick(e);
    }
    public FlatBtn()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand; Font = new Font("Segoe UI Semibold", 10.5f);
    }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.No; hover = false; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; hoverGear = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; downGear = InGear(e.X); Invalidate(); base.OnMouseDown(e); }
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
        if (Gear)
        {
            float gx = w - 27, gy = h / 2f; Color gc = GearEnabled ? (hoverGear ? Color.White : Color.FromArgb(205, 214, 232)) : Color.FromArgb(95, 105, 128);
            if (hoverGear && GearEnabled) using (var b = new SolidBrush(T.A(T.Accent, 60))) g.FillEllipse(b, gx - 15, gy - 15, 30, 30);
            using (var pen = new Pen(T.A(Color.White, 28), 1f)) g.DrawLine(pen, w - 54, 10, w - 54, h - 10);
            using (var pen = new Pen(gc, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; g.DrawLine(pen, (float)(gx + Math.Cos(a) * 6.5), (float)(gy + Math.Sin(a) * 6.5), (float)(gx + Math.Cos(a) * 9), (float)(gy + Math.Sin(a) * 9)); }
            using (var pen = new Pen(gc, 2.4f)) { g.DrawEllipse(pen, gx - 6.5f, gy - 6.5f, 13, 13); g.DrawEllipse(pen, gx - 2.4f, gy - 2.4f, 4.8f, 4.8f); }
            w -= 54;
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
        T.Tx(g, Sub, 9.5f, false, Color.FromArgb(218, 225, 240), 12, 29, w - 66, 18, 0, false, false);
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
//  İkinci imleç: gerçek masaüstünün üstünde gezen, tıklanamaz sanal imleç.
//  Senin farene dokunmaz; programın o an nereye tıkladığını gösterir.
// ---------------------------------------------------------------------------
class Ghost : Form
{
    const int W = 760, H = 180, TX = 380, TY = 70; // pencere boyutu ve imlecin pencere içindeki ucu (mantıksal piksel)
    public volatile bool ChipLeft; public volatile float X, Y; public long ClickTicks = 0; public volatile string Label = "";
    long lastActive = 0; volatile bool wantShow;
    System.Windows.Forms.Timer tm; float anim; Bitmap bmp; int lastTop = 0;
    readonly PointF[] trail = new PointF[10];

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref Point pDst, ref Size sz, IntPtr hdcSrc, ref Point pSrc, int key, ref BLEND bl, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8; return cp; } // katmanlı, fareyi geçirir, araç penceresi, odaksız, üstte
    }

    public Ghost()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; Size = new Size((int)Math.Round(W * T.S), (int)Math.Round(H * T.S));
        bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var p = Cursor.Position; X = p.X + 90; Y = p.Y + 60;
        tm = new System.Windows.Forms.Timer { Interval = 16 }; tm.Tick += (s, e) => Render(); tm.Start();
    }

    public void Poke() { lastActive = Environment.TickCount; wantShow = true; }
    public void Start() { var p = Cursor.Position; X = p.X + 90; Y = p.Y + 60; for (int i = 0; i < trail.Length; i++) trail[i] = new PointF(X, Y); Poke(); }
    public void SetPos(float x, float y) { X = x; Y = y; Poke(); }
    public void Stop() { wantShow = false; }
    public void Tap(string label) { Label = label; ClickTicks = Environment.TickCount; Poke(); Thread.Sleep(550); }

    // Kolay hareket: eğrisel yol, ivmeli
    public void MoveTo(float x, float y)
    {
        Poke();
        float sx = X, sy = Y, dx = x - sx, dy = y - sy, dist = (float)Math.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(20, Math.Min(70, (int)(dist / 12)));
        float nx = -dy / (dist + 1) * Math.Min(60, dist * 0.12f); // hafif yay
        float ny = dx / (dist + 1) * Math.Min(60, dist * 0.12f);
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps, e = t < 0.5f ? 2 * t * t : 1 - (float)Math.Pow(-2 * t + 2, 2) / 2, bow = (float)Math.Sin(t * Math.PI);
            X = sx + dx * e + nx * bow; Y = sy + dy * e + ny * bow; Poke();
            Thread.Sleep(14);
        }
        X = x; Y = y; Thread.Sleep(180);
    }

    void Render()
    {
        long idle = unchecked(Environment.TickCount - lastActive);
        bool active = wantShow && idle < 3200;
        if (!active) { if (Visible) Hide(); return; }
        anim += 0.02f; if (anim > 1) anim -= 1;
        if (!Visible) { Show(); }
        float S = T.S;
        for (int i = trail.Length - 1; i > 0; i--) trail[i] = trail[i - 1];
        trail[0] = new PointF(X, Y);
        float fade = idle > 2400 ? Math.Max(0f, 1f - (idle - 2400) / 800f) : 1f;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(S, S);
            Paint2(g, fade);
        }
        IntPtr sdc = GetDC(IntPtr.Zero), mdc = CreateCompatibleDC(sdc), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mdc, hb);
        try
        {
            var dst = new Point((int)(X - TX * S), (int)(Y - TY * S)); var sz = new Size(bmp.Width, bmp.Height); var src = new Point(0, 0);
            var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, sdc, ref dst, ref sz, mdc, ref src, 0, ref bl, 2);
            if (unchecked(Environment.TickCount - lastTop) > 300) { lastTop = Environment.TickCount; SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40); }
        }
        finally { SelectObject(mdc, old); DeleteObject(hb); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc); }
    }

    void Paint2(Graphics g, float fade)
    {
        Func<int, int> al = a => (int)(a * fade);
        float S = T.S;
        // iz
        for (int i = trail.Length - 1; i >= 1; i--)
        {
            float px = TX + (trail[i].X - X) / S, py = TY + (trail[i].Y - Y) / S, r = 1.5f + (trail.Length - i) * 0.35f;
            using (var b = new SolidBrush(T.A(T.Cursor, al(14 + (trail.Length - i) * 4)))) g.FillEllipse(b, px - r, py - r, r * 2, r * 2);
        }
        // tıklama dalgası
        long dt = unchecked(Environment.TickCount - ClickTicks);
        if (ClickTicks != 0 && dt >= 0 && dt < 800)
        {
            float t = dt / 800f, r = 5 + 28 * t;
            using (var pen = new Pen(T.A(T.Cursor, al((int)(240 * (1 - t)))), 2.5f)) g.DrawEllipse(pen, TX - r, TY - r, r * 2, r * 2);
            using (var b = new SolidBrush(T.A(T.Cursor, al((int)(90 * (1 - t)))))) g.FillEllipse(b, TX - r, TY - r, r * 2, r * 2);
            float r2 = 3 + 14 * t;
            using (var pen = new Pen(T.A(Color.White, al((int)(200 * (1 - t)))), 2f)) g.DrawEllipse(pen, TX - r2, TY - r2, r2 * 2, r2 * 2);
        }
        // parıltı
        float pulse = 0.5f + 0.5f * (float)Math.Sin(anim * Math.PI * 2);
        using (var b = new SolidBrush(T.A(T.Cursor, al((int)(34 + 26 * pulse))))) g.FillEllipse(b, TX - 12, TY - 12, 24, 24);
        // imleç
        var pts = new[] { new PointF(0, 0), new PointF(0, 19), new PointF(5, 14.5f), new PointF(8.5f, 22), new PointF(12, 20.5f), new PointF(8.5f, 13.5f), new PointF(15, 13.5f) };
        float k = 0.95f;
        var sh = new PointF[pts.Length]; var cu = new PointF[pts.Length];
        for (int i = 0; i < pts.Length; i++) { cu[i] = new PointF(TX + pts[i].X * k, TY + pts[i].Y * k); sh[i] = new PointF(cu[i].X + 1.5f, cu[i].Y + 2f); }
        using (var b = new SolidBrush(Color.FromArgb(al(110), 0, 0, 0))) g.FillPolygon(b, sh);
        using (var b = new SolidBrush(T.A(T.Cursor, al(255)))) g.FillPolygon(b, cu);
        using (var pen = new Pen(T.A(Color.White, al(255)), 1.8f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, cu);
        // etiket
        string lab = Label;
        if (!string.IsNullOrEmpty(lab))
        {
            float tw = T.Measure(g, lab, 9f, true), cw = tw + 34;
            var scr = Screen.FromPoint(new Point((int)X, (int)Y)).Bounds;
            bool flip = ChipLeft || X + (30 + cw) * S > scr.Right - 10;
            bool up = Y + (70 + 40) * S > scr.Bottom;
            float cx = flip ? TX - 14 - cw : TX + 22, cy = up ? TY - 52 : TY + 26;
            var chip = new RectangleF(cx, cy, cw, 26);
            using (var p = T.Round(chip, 13)) using (var b = new SolidBrush(Color.FromArgb(al(238), 13, 17, 27))) using (var pen = new Pen(T.A(T.Cursor, al(200)), 1.4f)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            using (var b = new SolidBrush(T.A(T.Cursor, al(255)))) g.FillEllipse(b, chip.X + 11, chip.Y + 10, 6, 6);
            T.Tx(g, lab, 9f, true, T.A(Color.White, al(255)), chip.X + 24, chip.Y, tw + 8, 26, 0, true, false);
        }
    }
}

// ---------------------------------------------------------------------------
//  Windows arayüzünde (görev çubuğu, hızlı ayarlar) öğe bulma: UI Automation
// ---------------------------------------------------------------------------
static class Desk
{
    static readonly Regex TrayRx = new Regex(@"^\s*(Ağ|Network|Wi-?Fi)", RegexOptions.IgnoreCase);
    static readonly Regex TileRx = new Regex(@"^\s*Wi-?Fi", RegexOptions.IgnoreCase);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, int extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rectangle r4);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    public static string LastSeen = "";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void mouse_event(int flags, int dx, int dy, int data, int extra);
    static readonly Regex PortalTitleRx = new Regex(@"gsb|giriş|login|wi-?fi|oturum|sign.?in|connecttest", RegexOptions.IgnoreCase);
    static readonly Regex LinkRx = new Regex(@"web|site|sayfa|tarayıcı|browser|sign|oturum|giriş|login|portal|aç\b|open|click|tıkla", RegexOptions.IgnoreCase);
    static readonly Regex ExpandRx = new Regex(@"ağlarını yönet|bağlantılarını yönet|manage wi-?fi|wi-?fi connections|kullanılabilir|available", RegexOptions.IgnoreCase);

    // Chrome/Edge pencerelerinin başlıkları (tutamaç -> başlık)
    public static Dictionary<IntPtr, string> BrowserWindows()
    {
        var d = new Dictionary<IntPtr, string>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cn = new StringBuilder(128); GetClassName(h, cn, 128);
            if (cn.ToString() != "Chrome_WidgetWin_1") return true;
            var tt = new StringBuilder(512); GetWindowText(h, tt, 512);
            if (tt.Length > 0) d[h] = tt.ToString();
            return true;
        }, IntPtr.Zero);
        return d;
    }
    // Giriş formu bir tarayıcı penceresinde açık mı? (başlığı formu gösteriyor ya da bizden önce olmayan yeni pencere)
    public static bool PortalOpen(Dictionary<IntPtr, string> before, out string title)
    {
        title = "";
        foreach (var kv in BrowserWindows())
        {
            string t = Regex.Replace(kv.Value, @"\s+-\s+(Google Chrome|Microsoft.? Edge).*$", "");
            if (PortalTitleRx.IsMatch(t) || !before.ContainsKey(kv.Key)) { title = kv.Value; return true; }
        }
        return false;
    }

    static void Click(System.Windows.Automation.AutomationElement e)
    {
        try { Press(e); return; } catch { }
        var c = Center(e); SetCursorPos(c.X, c.Y); Thread.Sleep(80); mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
    }

    // Hızlı ayarlar panelinde ağın altındaki mavi bağlantıyı bulur; yoksa Wi-Fi listesini açıp tekrar bakar. Bulursa tıklar.
    public static bool ClickPortalLink(out string info)
    {
        info = "";
        var AEc = System.Windows.Automation.AutomationElement.ControlTypeProperty;
        for (int round = 0; round < 3; round++)
        {
            string seen; var wins = CornerWindows(out seen); LastSeen = seen;
            var names = new StringBuilder();
            foreach (var h in wins)
            {
                System.Windows.Automation.AutomationElement w; try { w = System.Windows.Automation.AutomationElement.FromHandle(h); } catch { continue; }
                System.Windows.Automation.AutomationElement expand = null;
                foreach (System.Windows.Automation.AutomationElement e in w.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                {
                    try
                    {
                        var ec = e.Current; if (ec.BoundingRectangle.IsEmpty) continue;
                        string n = ec.Name ?? ""; var ct = ec.ControlType;
                        if (n.Length > 0 && names.Length < 600) names.Append(ct.ProgrammaticName.Replace("ControlType.", "")).Append(':').Append(n).Append(" | ");
                        if (ct == System.Windows.Automation.ControlType.Hyperlink && n.Length > 0) { Click(e); info = "bağlantı: " + n; return true; }
                        if ((ct == System.Windows.Automation.ControlType.Button || ct == System.Windows.Automation.ControlType.Text) && n.Length > 0 && n.Length < 80 && LinkRx.IsMatch(n) && !TileRx.IsMatch(n) && !ExpandRx.IsMatch(n)) { Click(e); info = "bağlantı: " + n; return true; }
                        if (expand == null && ct == System.Windows.Automation.ControlType.Button && ExpandRx.IsMatch(n)) expand = e;
                    }
                    catch { }
                }
                if (expand != null && round == 0) { try { Click(expand); } catch { } Thread.Sleep(900); goto next; }
            }
            info = "görülen öğeler: " + names; if (round == 2) break;
            next: Thread.Sleep(400);
        }
        return false;
    }

    // Ekranın sağ alt köşesindeki (hızlı ayarlar paneli olabilecek) pencereler
    static List<IntPtr> CornerWindows(out string seen)
    {
        var list = new List<IntPtr>(); var sb0 = new StringBuilder(); var scr = Screen.PrimaryScreen.Bounds;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            Rectangle rc; if (!GetWindowRect(h, out rc)) return true;
            var cn = new StringBuilder(128); GetClassName(h, cn, 128); string c = cn.ToString();
            int w = rc.Width - rc.X, ht = rc.Height - rc.Y; // GetWindowRect: Width/Height alanları sağ/alt koordinat olarak gelir
            bool corner = w >= 250 && w <= 800 && ht >= 120 && rc.Width >= scr.Right - 90 && rc.Height >= scr.Bottom - 220 && c != "Shell_TrayWnd" && c != "Progman";
            if (corner) sb0.Append(c).Append('(').Append(w).Append('x').Append(ht).Append(") ");
            if (corner || Regex.IsMatch(c, "ControlCenter|NetworkUX|XamlExplorerHost|XamlIsland")) list.Add(h);
            return true;
        }, IntPtr.Zero);
        seen = sb0.ToString(); return list;
    }

    public static System.Windows.Automation.AutomationElement FindTrayButton()
    {
        // Görev çubuğunun XAML öğeleri ilk sorguda geç yüklenebiliyor: kısa süre tekrar denenir
        var end = DateTime.UtcNow.AddMilliseconds(5000);
        var AE = System.Windows.Automation.AutomationElement.ControlTypeProperty;
        var cond = new System.Windows.Automation.PropertyCondition(AE, System.Windows.Automation.ControlType.Button);
        while (DateTime.UtcNow < end)
        {
            try
            {
                IntPtr hw = FindWindow("Shell_TrayWnd", null);
                if (hw != IntPtr.Zero)
                {
                    var tb = System.Windows.Automation.AutomationElement.FromHandle(hw);
                    foreach (System.Windows.Automation.AutomationElement e in tb.FindAll(System.Windows.Automation.TreeScope.Descendants, cond))
                    { string n = e.Current.Name ?? ""; if (TrayRx.IsMatch(n) && !e.Current.BoundingRectangle.IsEmpty) return e; }
                }
            }
            catch { }
            Thread.Sleep(300);
        }
        return null;
    }

    public static Point Center(System.Windows.Automation.AutomationElement e)
    {
        var r = e.Current.BoundingRectangle; return new Point((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
    }

    public static void Press(System.Windows.Automation.AutomationElement e)
    {
        object p;
        if (e.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out p)) ((System.Windows.Automation.TogglePattern)p).Toggle();
        else if (e.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out p)) ((System.Windows.Automation.InvokePattern)p).Invoke();
        else throw new InvalidOperationException("öğe tıklanamıyor");
    }

    // Windows 11 hızlı ayarlar panelinde ilk düğmenin (Wi-Fi) yaklaşık yeri: panel sağ-alta yaslı, ölçüler ekran ölçeğiyle büyür
    public static Point? EstimateTile()
    {
        IntPtr hw = FindWindow("Shell_TrayWnd", null); Rectangle r; if (hw == IntPtr.Zero || !GetWindowRect(hw, out r)) return null;
        var scr = Screen.PrimaryScreen.Bounds;
        return new Point((int)(scr.Right - 300 * T.S), (int)(r.Y - 352 * T.S));
    }

    public static void Esc() { keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0); }

    // Hızlı ayarlar panelindeki "Wi-Fi" düğmesi
    public static System.Windows.Automation.AutomationElement FindTile(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        var btnCond = new System.Windows.Automation.OrCondition(
            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button),
            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.CheckBox));
        while (DateTime.UtcNow < end)
        {
            try
            {
                string seen; var wins = CornerWindows(out seen); LastSeen = seen;
                foreach (var h in wins)
                {
                    var w = System.Windows.Automation.AutomationElement.FromHandle(h);
                    System.Windows.Automation.AutomationElement best = null;
                    foreach (System.Windows.Automation.AutomationElement e in w.FindAll(System.Windows.Automation.TreeScope.Descendants, btnCond))
                    {
                        var ec = e.Current; if (ec.BoundingRectangle.IsEmpty || !TileRx.IsMatch(ec.Name ?? "")) continue;
                        object p; if (e.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out p)) return e;
                        if (best == null) best = e;
                    }
                    if (best != null) return best;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        return null;
    }
}

// ---------------------------------------------------------------------------
//  Ana pencere
// ---------------------------------------------------------------------------
class App : Form
{
    const string Ssid = "GSBWIFI";
    const int TickMs = 4000;
    static bool Demo = false;
    // Demo sayfası, adres çubuğunda kişisel bir ad görünmesin diye ortak klasörde ("Public\kullanici") durur
    static string DemoPath { get { return Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "kullanici", "login.html"); } }
    static string PortalUrl { get { return Demo ? new Uri(DemoPath).AbsoluteUri : (Environment.GetEnvironmentVariable("GSB_PORTAL") ?? "https://wifi.gsb.gov.tr/login.html"); } }
    const string DemoHtml = "<!doctype html><html lang=tr><meta charset=utf-8><title>Wi-Fi Giriş (Demo)</title><body style=\"font-family:Segoe UI,sans-serif;background:#f3ecd2;margin:0\"><div style=\"background:#9aa0ad;padding:28px;text-align:center;color:#6b1f1f;font:600 22px Georgia,serif\">Wi-Fi Giriş (Demo)</div><p style=\"text-align:center;color:#3d5a1f;font:600 16px Georgia,serif;margin:28px 0\">Hoşgeldiniz. İnternet erişimi için bilgilerinizi girerek 'Giriş' butonuna basınız.</p><form onsubmit=\"event.preventDefault();document.getElementById('o').textContent='Demo: giriş gönderildi';\" style=\"width:360px;margin:30px auto\"><p>Kullanıcı Adı:<br><input name=j_username style=\"font-size:18px;padding:8px;width:100%;box-sizing:border-box\"></p><p>Şifre:<br><input type=password name=j_password style=\"font-size:18px;padding:8px;width:100%;box-sizing:border-box\"></p><p><input type=submit name=submit value=\"Giriş\" style=\"font-size:18px;padding:10px;width:100%;background:#e8c547;border:1px solid #b39a2e;border-radius:22px\"></p><h3 id=o style=\"text-align:center;color:#2d6a2d\"></h3></form></body></html>";
    const string PortalPost = "https://wifi.gsb.gov.tr/j_spring_security_check";

    RichTextBox box; FlatBtn btn, btnHide, btnCred; Switch swView; Card card; Label logHead;
    NotifyIcon tray; System.Windows.Forms.Timer timer, animTimer, watch;
    int attempts = 0, resets = 0, loginFails = 0, missCount = 0, logCount = 0; float anim = 0;
    bool running = false, busy = false, done = false, viewOn = false;
    volatile bool cancel = false;
    Ph ph = Ph.Idle; string title = "Hazır", detail = "Başlat'a basınca GSBWIFI'ye bağlanmayı dener."; int step = -1;
    Stopwatch sw = new Stopwatch();
    volatile Ghost ghost; Cdp cdp;

    static void CrashLog(Exception ex)
    {
        try { Directory.CreateDirectory(DataDir); File.AppendAllText(Path.Combine(DataDir, "hata.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (ex == null ? "bilinmeyen hata" : ex.ToString()) + Environment.NewLine + Environment.NewLine); } catch { }
    }

    // Uygulama beklenmedik kapandıysa geride kalmış, bizim profilimizle açılmış Chrome'ları kapatır
    static void KillStaleChrome()
    {
        try {
            using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='chrome.exe' OR Name='msedge.exe'"))
                foreach (ManagementObject o in q.Get()) {
                    string cl = o["CommandLine"] as string; if (cl == null) continue;
                    if (cl.IndexOf("remote-debugging-port", StringComparison.OrdinalIgnoreCase) >= 0 && cl.IndexOf("GSBWifiOtomatik", StringComparison.OrdinalIgnoreCase) >= 0 && cl.IndexOf("--type=", StringComparison.Ordinal) < 0)
                        try { var k = Process.Start(new ProcessStartInfo("taskkill", "/PID " + o["ProcessId"] + " /T /F") { UseShellExecute = false, CreateNoWindow = true }); if (k != null) k.WaitForExit(3000); } catch { }
                }
        } catch { }
    }

    static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSBWifiOtomatik"); } }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread] static void Main(string[] args)
    {
        Demo = Array.IndexOf(args, "--demo") >= 0;
        bool first; var single = new Mutex(true, "Global\\GSBWifiOtomatik_TekKopya", out first);
        if (!first && !Demo) { MessageBox.Show("GSBWIFI Otomatik zaten çalışıyor. Görev çubuğunun sağındaki simge alanına bak.", "GSBWIFI Otomatik", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (first) KillStaleChrome();
        try { SetProcessDPIAware(); } catch { }
        using (var gg = Graphics.FromHwnd(IntPtr.Zero)) T.S = gg.DpiX / 96f;
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => CrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { CrashLog(e.ExceptionObject as Exception); if (adapterOffByUs) EnableAdapter(); };
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
        var brandT = new Label { Left = 72, Top = 19, AutoSize = true, Text = "GSB WiFi", Font = new Font("Segoe UI Semibold", 13.5f), ForeColor = T.Text, BackColor = T.Side };
        var brandS = new Label { Left = 74, Top = 49, AutoSize = true, Text = "Otomatik bağlantı", Font = new Font("Segoe UI", 10f), ForeColor = Color.FromArgb(214, 222, 238), BackColor = T.Side };
        btn = new FlatBtn { Left = 20, Top = 100, Width = 180, Height = 50, Text = "Başlat", Fill = T.Green, Fill2 = Color.FromArgb(14, 165, 160), BackColor = T.Side, Font = new Font("Segoe UI Semibold", 12f) };
        var secHead = new Label { Left = 22, Top = 176, AutoSize = true, Text = "SEÇENEKLER", Font = new Font("Segoe UI Semibold", 8f), ForeColor = T.Muted, BackColor = T.Side };
        swView = new Switch { Left = 20, Top = 198, Width = 180, Height = 54, Text = "Görüntü", Sub = "İkinci imleci göster", BackColor = T.Side };
        btnCred = new FlatBtn { Left = 20, Top = 440, Width = 180, Height = 40, Text = "Giriş bilgileri", Ghost = true, BackColor = T.Side };
        btnHide = new FlatBtn { Left = 20, Top = 492, Width = 180, Height = 40, Text = "Arka plana al", Ghost = true, BackColor = T.Side };
        side.Controls.AddRange(new Control[] { brand, brandT, brandS, btn, secHead, swView, btnCred, btnHide });

        card = new Card(this) { Left = 244, Top = 24, Width = 532, Height = 276, BackColor = T.Bg };
        logHead = new Label { Left = 246, Top = 312, AutoSize = true, Text = "KAYIT", Font = new Font("Segoe UI Semibold", 8f), ForeColor = T.Muted, BackColor = T.Bg };
        var logHost = new Panel { Left = 244, Top = 334, Width = 532, Height = 204, BackColor = T.Card, Padding = new Padding(14, 12, 10, 10) };
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
        FormClosed += (s, e) => { tray.Visible = false; tray.Dispose(); if (ghost != null) ghost.Close(); CloseBrowser(); if (adapterOffByUs) EnableAdapter(); };

        if (T.S != 1f) Scale(new SizeF(T.S, T.S));
        LoadSettings(); swView.Checked = viewOn;
        Shown += (s, e) => {
            if (Demo) { RunDemo(); return; }
#if SELFTEST
            viewOn = true; swView.Checked = true; running = true; sw.Start(); RefreshOverlay(); UpdateButton();
            if (Environment.GetEnvironmentVariable("GSB_DESKTEST") == "1") { EnsureGhost(); Task.Run(() => { bool ok = VisualWifi(true, null, null, true); Log("DESKTEST sonuç: " + ok); }); return; }
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

    // "--demo": gerçek ağ ve gerçek bilgi olmadan, örnek giriş sayfasında ikinci imleç gösterisi (tanıtım videosu için)
    void RunDemo()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(DemoPath)); File.WriteAllText(DemoPath, DemoHtml, new UTF8Encoding(false)); } catch (Exception ex) { Log("Demo sayfası yazılamadı: " + ex.Message); return; }
        viewOn = true; swView.Checked = true; running = true; sw.Start(); UpdateButton(); RefreshOverlay();
        SetState(Ph.Page, "Demo başlıyor", "Örnek giriş sayfası açılıyor");
        Log("Demo: örnek bilgiler kullanılıyor, gerçek ağa ve gerçek hesaba dokunulmaz.");
        Task.Run(() => {
            Thread.Sleep(1200);
            string r = BrowserLogin("12312312312", "demo-sifre");
            Thread.Sleep(2500);
            running = false; sw.Stop();
            SetState(Ph.Done, "Demo tamamlandı", "Örnek sayfada alanlar dolduruldu ve Giriş'e tıklandı.");
            Log("Demo bitti (" + r + ").");
            BeginInvoke(new Action(() => { UpdateButton(); RefreshOverlay(); }));
            Thread.Sleep(3000); CloseBrowser();
        });
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
            string u, p;
            if (LoadCred(out u, out p)) { credMode = "manual"; SaveSettings(); Log("Kayıtlı giriş bilgileri kullanılacak."); } // bir kez girildi, tekrar sorulmaz
            else if (AskCred(this)) { credMode = "manual"; SaveSettings(); Log("Giriş bilgileri kaydedildi."); }
            else Log("Giriş bilgisi girilmedi.");
        }
        else if (m == 3) {
            if (AskCred(this)) { credMode = "manual"; SaveSettings(); Log("Giriş bilgileri güncellendi."); }
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
        box.SelectionColor = c; box.AppendText(m + Environment.NewLine);
        if (++logCount % 40 == 0 && box.Lines.Length > 500) { box.Select(0, box.GetFirstCharIndexFromLine(150)); box.SelectedText = ""; }
        box.SelectionStart = box.TextLength; box.ScrollToCaret();
    }

    void SetState(Ph p, string t, string d)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetState(p, t, d))); return; }
        ph = p; title = t; detail = d;
        switch (p) { case Ph.Connect: case Ph.Reset: step = 0; break; case Ph.Page: step = 1; break; case Ph.Login: step = 2; break; case Ph.Done: step = 4; break; }
        string tt = t + " - " + d; tray.Text = tt.Length > 60 ? tt.Substring(0, 60) : tt;
        card.Invalidate();
    }

    void UpdateButton()
    {
        btn.Text = running ? "Durdur" : "Başlat"; btn.Fill = running ? T.Red : T.Green; btn.Fill2 = running ? Color.FromArgb(249, 115, 22) : Color.FromArgb(14, 165, 160); btn.Invalidate();
    }

    void EnsureGhost() { if (ghost == null || ghost.IsDisposed) { ghost = new Ghost(); ghost.Show(); ghost.Hide(); } }

    void RefreshOverlay()
    {
        if (viewOn && running) EnsureGhost();
        else if (ghost != null) ghost.Stop();
    }

    async Task TryStart()
    {
        btn.Enabled = false; watch.Stop(); if (viewOn) EnsureGhost();
        SetState(Ph.Connect, "Wi-Fi kontrol ediliyor", "Kapalıysa açılır, sonra " + Ssid + " aranır");
        bool vis = false, changed = false;
        await Task.Run(() => {
            if (viewOn && WifiIsOff()) { changed = true; VisualWifi(false, null, () => EnsureWifiOn()); EnsureWifiOn(); }
            else changed = EnsureWifiOn();
            for (int i = 0; i < (changed ? 6 : 1) && !vis; i++) { vis = NetworkVisible(); if (!vis && changed) Thread.Sleep(2000); }
        });
        if (changed) Log("Wi-Fi kapalıydı, otomatik açıldı.");
        if (vis && ListUnknown) Log("Ağ listesi okunamadı (Windows konum izni kapalı olabilir), doğrudan bağlanılacak.");
        if (cancel && !running && ph == Ph.Stopped) { btn.Enabled = true; return; }
        if (vis) { btn.Enabled = true; StartRun(); } else EnterWait();
    }

    // Ağ görünmüyor: Başlat gri, hata ekranda; ağ çıkınca otomatik açılır
    void EnterWait()
    {
        running = false; step = -1; sw.Stop(); btn.Enabled = false; UpdateButton();
        if (!HasWifiAdapter()) {
            SetState(Ph.Warn, "Wi-Fi kartı bulunamadı", "Kablosuz ağ kartı görünmüyor. Uçak modunun ve Wi-Fi donanım anahtarının (Fn tuşu) kapalı olduğundan emin ol; masaüstü bilgisayarda Wi-Fi kartı gerekir.");
            Log("Bu bilgisayarda Wi-Fi kartı yok.");
        } else {
            SetState(Ph.Warn, Ssid + " ağı bulunamadı", "Wi-Fi listesinde " + Ssid + " görünmüyor. Ağın menzilinde olduğundan emin ol. Görününce Başlat açılacak.");
            Log(Ssid + " Wi-Fi listesinde yok. Bekleniyor...");
        }
        RefreshOverlay(); watch.Start();
    }

    async Task WatchTick()
    {
        if (running) { watch.Stop(); return; }
        bool vis = false;
        await Task.Run(() => { vis = NetworkVisible(); });
        if (!vis || running) return;
        watch.Stop(); btn.Enabled = true;
        Log(Ssid + " ağı göründü, otomatik başlatılıyor.");
        StartRun();
    }

    void StartRun()
    {
        running = true; cancel = false; done = false; loginFails = 0;
        sw.Start(); UpdateButton(); Log("Başladı. Hedef: " + Ssid + " (sürüm: kapat/aç döngüsü, kendi giriş denemesi yok)");
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
        T.Tx(g, detail, 11f, false, Color.FromArgb(205, 214, 230), 128, 82, sz.Width - 148, 46, 0, false, true);

        // adımlar
        string[] names = { "Wi-Fi", "Giriş sayfası", "Giriş", "İnternet" };
        float x0 = 56, x1 = sz.Width - 56, y = 158;
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
            T.Tx(g, names[i], 10f, true, (complete || current) ? T.Text : Color.FromArgb(190, 200, 220), x - 60, y + 17, 120, 20, 1, false, false);
        }

        // sayaçlar
        var el = sw.Elapsed; string time = ((int)el.TotalMinutes).ToString("00") + ":" + el.Seconds.ToString("00");
        string[] lab = { "DENEME", "WI-FI YENİLEME", "SÜRE" }, val = { attempts.ToString(), resets.ToString(), time };
        float gap = 10, bw = (sz.Width - 48 - gap * 2) / 3f;
        for (int i = 0; i < 3; i++)
        {
            var box = new RectangleF(24 + (bw + gap) * i, 204, bw, 58);
            using (var p = T.Round(box, 10))
            {
                using (var b = new SolidBrush(Color.FromArgb(150, 10, 15, 28))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(120, 70, 86, 120), 1.2f)) g.DrawPath(pen, p);
            }
            T.Tx(g, lab[i], 9.5f, true, Color.FromArgb(212, 220, 238), box.X + 14, box.Y + 8, bw - 20, 17, 0, false, false);
            T.Tx(g, val[i], 15f, true, T.Text, box.X + 14, box.Y + 26, bw - 20, 26, 0, false, false);
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
    public static volatile bool ListUnknown = false;
    // GSBWIFI listede görünüyor mu? Liste okunamıyorsa (ör. Windows 11'de konum izni kapalı) engellemez, doğrudan bağlanmayı dener.
    static bool NetworkVisible()
    {
        string o = Netsh("wlan show networks"); bool any = false, found = false;
        foreach (var l in o.Split('\n')) {
            var m = Regex.Match(l, @"^\s*SSID\s+\d+\s*:\s*(.+?)\s*$");
            if (!m.Success) continue; any = true;
            if (string.Equals(m.Groups[1].Value, Ssid, StringComparison.OrdinalIgnoreCase)) found = true;
        }
        ListUnknown = !any && (o.Trim().Length == 0 || Regex.IsMatch(o, "konum|location|permission|izin|elevat|yükseltilmiş", RegexOptions.IgnoreCase));
        return found || ListUnknown;
    }

    // Bilgisayarda Wi-Fi kartı var mı (kapalı olsa bile)?
    static bool HasWifiAdapter()
    {
        try { foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) return true; return false; }
        catch { return true; }
    }

    // GSBWIFI profili kayıtlı değilse ve ağ açık (şifresiz) görünüyorsa profil eklenir; aksi halde bağlanmak mümkün olmaz.
    static void EnsureProfile()
    {
        try {
            if (Regex.IsMatch(Netsh("wlan show profiles"), @":\s*" + Regex.Escape(Ssid) + @"\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)) return;
            bool open = false, inBlock = false;
            foreach (var l in Netsh("wlan show networks").Split('\n')) {
                var m = Regex.Match(l, @"^\s*SSID\s+\d+\s*:\s*(.+?)\s*$");
                if (m.Success) { inBlock = string.Equals(m.Groups[1].Value, Ssid, StringComparison.OrdinalIgnoreCase); continue; }
                if (inBlock && Regex.IsMatch(l, @":\s*(Open|A.{1,3}k)\s*$", RegexOptions.IgnoreCase)) open = true;
            }
            if (!open) return;
            string xml = "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>" + Ssid + "</name><SSIDConfig><SSID><name>" + Ssid + "</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>manual</connectionMode><MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security></MSM></WLANProfile>";
            string f = Path.Combine(Path.GetTempPath(), "gsbwifi-profil.xml");
            File.WriteAllText(f, xml, new UTF8Encoding(false)); Netsh("wlan add profile filename=\"" + f + "\" user=current"); try { File.Delete(f); } catch { }
        } catch { }
    }
    static string CurrentSsid()
    {
        foreach (var l in Netsh("wlan show interfaces").Split('\n')) {
            var m = Regex.Match(l, @"^\s*SSID\s*:\s*(.+?)\s*$");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
    // Bazı ağlar tek bir test adresini engelleyebilir: iki bağımsız adresle bakılır
    static bool Internet()
    {
        try {
            var r = (HttpWebRequest)WebRequest.Create("http://www.msftconnecttest.com/connecttest.txt");
            r.Timeout = 2500; r.AllowAutoRedirect = false; r.Proxy = null;
            using (var resp = r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                if (sr.ReadToEnd().Trim() == "Microsoft Connect Test") return true;
        } catch { }
        try {
            var r = (HttpWebRequest)WebRequest.Create("http://clients3.google.com/generate_204");
            r.Timeout = 2500; r.AllowAutoRedirect = false; r.Proxy = null;
            using (var resp = (HttpWebResponse)r.GetResponse()) return resp.StatusCode == HttpStatusCode.NoContent;
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
    // 1 = Chrome'da kayıtlı, 2 = elle girilecek (kayıtlıysa tekrar sorulmaz), 3 = kayıtlı bilgiyi değiştir, 0 = vazgeçti
    static int AskMode(IWin32Window owner)
    {
        int result = 0; string cu, cp; bool has = LoadCred(out cu, out cp);
        using (var f = new Form { Text = "GSBWIFI giriş bilgileri", ClientSize = new Size(470, 300), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = T.Bg, ForeColor = T.Text })
        {
            var head = new Label { Text = "Giriş sayfasındaki bilgilerin nerede?", Location = new Point(20, 18), AutoSize = true, Font = new Font("Segoe UI Semibold", 12.5f), ForeColor = T.Text };
            var b1 = new FlatBtn { Text = "Chrome'da bilgilerim kayıtlı", Location = new Point(20, 62), Size = new Size(430, 46), Fill = T.Accent, Fill2 = Color.FromArgb(34, 211, 238), BackColor = T.Bg };
            var h1 = new Label { Text = "wifi.gsb.gov.tr'de kullanıcı adı ve şifre Chrome'dan kendiliğinden doluyor. Bir şey girmene gerek kalmaz.", Location = new Point(22, 112), Size = new Size(426, 38), ForeColor = T.Muted };
            var b2 = new FlatBtn { Text = "Bilgilerimi gireyim", Location = new Point(20, 164), Size = new Size(430, 46), Ghost = true, BackColor = T.Bg, Gear = true, GearEnabled = has };
            var h2 = new Label { Text = has ? "Kayıtlı bilgilerin var, tekrar sorulmaz. Güncellemek için sağdaki ayar simgesine tıkla." : "Bir kez sorulur ve kaydedilir. Bilgiler bu bilgisayarda, sadece senin Windows hesabınla açılabilecek şekilde şifreli saklanır.", Location = new Point(22, 214), Size = new Size(426, 38), ForeColor = T.Muted };
            b1.Click += (s, e) => { result = 1; f.Close(); };
            b2.Click += (s, e) => { result = 2; f.Close(); };
            b2.GearClick += (s, e) => { result = 3; f.Close(); };
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

    static void Wait(Ghost g, int ms) { for (int t = 0; t < ms; t += 100) { g.Poke(); Thread.Sleep(100); } }

    // Görüntü açıkken: ikinci imleç görev çubuğundaki Wi-Fi simgesine, sonra hızlı ayarlardaki Wi-Fi düğmesine tıklar.
    // Simge gerçekten tıklanır (panel açılır, senin farene dokunulmaz). Düğme Windows'ta tıklanabilir bulunursa gerçekten tıklanır,
    // bulunamazsa imleç düğmeye gider ve aynı anda işlem arka planda yapılır (doOff/doOn). Yapılamazsa false: normal yöntem devreye girer.
    bool VisualWifi(bool reset, Action doOff, Action doOn, bool dry = false)
    {
        var g = ghost; if (g == null || !viewOn) return false;
        try {
            var trayBtn = Desk.FindTrayButton();
            if (trayBtn == null) { Log("Görev çubuğunda Wi-Fi simgesi bulunamadı, arka planda yapılıyor."); return false; }
            bool win11 = (trayBtn.Current.ClassName ?? "").StartsWith("SystemTray");
            var c = Desk.Center(trayBtn);
            g.ChipLeft = false; g.Start(); g.Label = "Wi-Fi simgesine gidiliyor"; g.MoveTo(c.X, c.Y);
            g.Tap("Wi-Fi simgesine tıklandı"); Desk.Press(trayBtn);
            Wait(g, 700);
            var tile = Desk.FindTile(1200); bool real = tile != null;
            Point? target = real ? Desk.Center(tile) : (win11 ? Desk.EstimateTile() : null);
            if (target == null) { Log("Hızlı ayarlardaki Wi-Fi düğmesinin yeri belirlenemedi (" + Desk.LastSeen + "), arka planda yapılıyor."); Desk.Esc(); return false; }
            // Windows'un kendi paneli (hızlı ayarlar) her pencerenin üstünde çizilir; ikinci imleç panelin içinde görünemez.
            // Bu yüzden imleç panelin hemen sol kenarında, düğmenin hizasında durur; işlem düğmede yapılır.
            float panelLeft = Screen.PrimaryScreen.Bounds.Right - 372 * T.S;
            float dockX = Math.Min(target.Value.X, panelLeft - 12 * T.S);
            g.ChipLeft = true; g.Label = "Wi-Fi düğmesine gidiliyor"; g.MoveTo(dockX, target.Value.Y);
            Action<bool> act = on => { if (dry) return; if (real) Desk.Press(tile); else { var f = on ? doOn : doOff; if (f != null) f(); } };
            if (dry) { g.Tap("(deneme) Wi-Fi düğmesine tıklandı"); Log("Deneme: Wi-Fi düğmesine gidildi (" + (real ? "bulundu" : "tahmini yer") + "), dokunulmadı."); }
            else if (reset) { g.Tap("Wi-Fi kapatılıyor"); act(false); Wait(g, 3500); g.Tap("Wi-Fi açılıyor"); act(true); }
            else { g.Tap("Wi-Fi açılıyor"); act(true); }
            Wait(g, dry ? 600 : 1500); g.Label = "Panel kapatılıyor"; Wait(g, 500); Desk.Esc(); g.ChipLeft = false; Thread.Sleep(300);
            return true;
        }
        catch (Exception ex) { Log("İkinci imleçle Wi-Fi adımı yapılamadı (" + ex.Message + "), arka planda yapılıyor."); try { Desk.Esc(); } catch { } return false; }
    }

    // Wi-Fi radyosu ya da kartı kapalıysa açar. Kapalıydıysa true döner.
    static bool EnsureWifiOn() { return RunWifiScript("on"); }
    static bool WifiIsOff() { return RunWifiScript("check"); }

    static bool RunWifiScript(string mode)
    {
        const string ps0 = @"
$ErrorActionPreference='SilentlyContinue'
$was=$false
$mode='__MODE__'
try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $asTask=([System.WindowsRuntimeSystemExtensions].GetMethods() | ? { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  function Await($op,$t){ $n=$asTask.MakeGenericMethod($t).Invoke($null,@($op)); $n.Wait(-1)|Out-Null; $n.Result }
  [Windows.Devices.Radios.Radio,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null
  [Windows.Devices.Radios.RadioAccessStatus,Windows.System.Devices,ContentType=WindowsRuntime]|Out-Null
  Await ([Windows.Devices.Radios.Radio]::RequestAccessAsync()) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null
  $radios=Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])
  foreach($r in $radios){ if($r.Kind -ne 'WiFi'){ continue }; if($mode -eq 'off'){ $was=$true; Await ($r.SetStateAsync('Off')) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null } elseif($r.State -ne 'On'){ $was=$true; if($mode -eq 'on'){ Await ($r.SetStateAsync('On')) ([Windows.Devices.Radios.RadioAccessStatus])|Out-Null } } }
} catch {}
try { Get-NetAdapter -Physical | ? { $_.Status -eq 'Disabled' -and ($_.PhysicalMediaType -match '802.11' -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless') } | % { $was=$true; if($mode -eq 'on'){ Enable-NetAdapter -Name $_.Name -Confirm:$false } } } catch {}
if($was){'WAS_OFF'}
";
        string ps = ps0.Replace("__MODE__", mode);
        try {
            Directory.CreateDirectory(DataDir);
            string file = Path.Combine(DataDir, "wifi-" + mode + ".ps1");
            File.WriteAllText(file, ps, new UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
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
                var gh = ghost;
                if (viewOn && gh != null)
                {
                    c.InitScreen = new PointF(gh.X, gh.Y);
                    c.GhostMove = (x, y) => gh.SetPos(x, y);
                    c.GhostClick = () => { gh.ClickTicks = Environment.TickCount; gh.Poke(); };
                    c.OnAction = t => { gh.Label = t; gh.Poke(); };
                }
                Directory.CreateDirectory(DataDir);
                if (!c.Start(exe, DataDir, viewOn && ghost != null)) { c.Dispose(); return cancel ? "cancel" : "nochrome"; }
                cdp = c;
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
    static string adapterNow = "Wi-Fi";
    static volatile bool adapterOffByUs = false;
    // Wi-Fi'ı kapat/aç: Windows'un Wi-Fi anahtarı (hızlı ayarlardaki düğmeyle aynı), yönetici izni gerekmez.
    // Olmazsa (ör. radyo arayüzü yok) kart devre dışı bırakılır; bu yalnızca yönetici olarak çalışırken işe yarar.
    static void DisableAdapter()
    {
        adapterOffByUs = true;
        if (!RunWifiScript("off")) { adapterNow = AdapterName(); Netsh("interface set interface name=\"" + adapterNow + "\" admin=disabled"); }
    }
    static void EnableAdapter()
    {
        adapterOffByUs = false; RunWifiScript("on");
        Netsh("interface set interface name=\"" + adapterNow + "\" admin=enabled");
    }
    static void ResetAdapter()
    {
        DisableAdapter();
        Thread.Sleep(3000);
        EnableAdapter();
        Thread.Sleep(5000);
        Connect();
    }
    static void Connect() { EnsureProfile(); Netsh("wlan connect name=\"" + Ssid + "\" ssid=\"" + Ssid + "\""); }

    // Giriş formu (captive portal) geldi: Windows onu Chrome'da kendisi açar, uygulama işini bitirir.
    async Task FormReady()
    {
        done = true; timer.Stop();
        // Önce Windows/Chrome formu kendisi açıyor mu diye beklenir; açarsa hiçbir şeye tıklanmaz.
        var before = new Dictionary<IntPtr, string>(); string title = ""; bool open = false, clicked = false;
        SetState(Ph.Page, "Giriş sayfası bekleniyor", "Chrome formu kendisi açarsa dokunulmaz");
        await Task.Run(() => {
            before = Desk.BrowserWindows();
            for (int i = 0; i < 16 && !open && !cancel; i++) { Thread.Sleep(500); open = Desk.PortalOpen(before, out title); }
        });
        if (!open && !cancel)
        {
            SetState(Ph.Page, "Form açılmadı, panelden açılıyor", "Wi-Fi panelindeki mavi bağlantıya tıklanıyor");
            Log("Chrome formu açmadı. Wi-Fi panelindeki bağlantıya tıklanıyor.");
            for (int k = 0; k < 2 && !open && !cancel; k++)
            {
                string info = "";
                await Task.Run(() => {
                    var tb = Desk.FindTrayButton();
                    if (tb == null) { info = "Görev çubuğunda Wi-Fi simgesi bulunamadı"; return; }
                    try { Desk.Press(tb); } catch (Exception ex) { info = "simge tıklanamadı: " + ex.Message; return; }
                    Thread.Sleep(900);
                    clicked = Desk.ClickPortalLink(out info);
                    if (clicked) for (int i = 0; i < 20 && !open; i++) { Thread.Sleep(500); open = Desk.PortalOpen(before, out title); }
                    Desk.Esc();
                });
                Log(clicked ? "Panel " + info : "Panelde bağlantı bulunamadı (" + info + ")");
            }
        }
        sw.Stop();
        if (open) { SetState(Ph.Done, "Giriş sayfası açık", "Pencere birkaç saniye sonra kapanacak."); Log("Giriş formu açık: " + title); }
        else { SetState(Ph.Warn, "Form açılamadı", "Giriş sayfası gelmişti ama Chrome'da açılmadı. Hata kaydına bak ya da panelden bağlantıya elle tıkla."); Log("Form açılmadı."); await Task.Delay(1000); StopRun(); return; }
        tray.BalloonTipTitle = "GSBWIFI"; tray.BalloonTipText = "Giriş sayfası açık."; tray.ShowBalloonTip(3000);
        await Task.Delay(5000); Close();
    }

    // Ağ giriş sayfasına yönlendiriyor mu? (internet yok + yönlendirme yok = "internet yok, açık" takılması)
    static bool PortalPresent()
    {
        try {
            var r = (HttpWebRequest)WebRequest.Create("http://www.msftconnecttest.com/connecttest.txt");
            r.Timeout = 3000; r.AllowAutoRedirect = false; r.Proxy = null;
            using (var resp = (HttpWebResponse)r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                { int c = (int)resp.StatusCode; string body = sr.ReadToEnd(); if (c >= 300 && c < 400) return true; if (body.Contains("j_username") || body.IndexOf("gsb.gov.tr", StringComparison.OrdinalIgnoreCase) >= 0) return true; }
        }
        catch (WebException ex) {
            var hr = ex.Response as HttpWebResponse;
            if (hr != null) { int c = (int)hr.StatusCode; if (c >= 300 && c < 400) return true; }
        }
        catch { }
        try {
            var r = (HttpWebRequest)WebRequest.Create(PortalUrl);
            r.Timeout = 3000; r.Proxy = null;
            using (var resp = (HttpWebResponse)r.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd().Contains("j_username");
        } catch { return false; }
    }

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
                // Giriş formunu Windows kendisi Chrome'da açar; uygulama açmaz. Form gelmediyse Wi-Fi kapat/aç döngüsü sürer.
                attempts++;
                SetState(Ph.Page, "İnternet yok, giriş sayfası bekleniyor", Ssid + " bağlı ama internet gelmedi");
                bool portal = false;
                await Task.Run(() => { portal = PortalPresent(); });
                if (!running) return;
                if (portal) { await FormReady(); return; }
                Log("Giriş sayfası gelmedi (\"internet yok, açık\").");
                resets++;
                SetState(Ph.Reset, "Wi-Fi kapatılıp açılıyor", "Sayfa açılana kadar yeniden denenecek (#" + resets + ")");
                Log("Wi-Fi kapat/aç #" + resets);
                await Task.Run(() => {
                    if (viewOn && VisualWifi(true, DisableAdapter, EnableAdapter)) { EnsureWifiOn(); Thread.Sleep(5000); Connect(); }
                    else { EnsureWifiOn(); ResetAdapter(); }
                });
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
