using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program {
    [STAThread] static void Main(string[] args) {
        if (args.Length>0 && args[0]=="--self-test") {
            var valid=new byte[] {4,0,0,26,6,0,0,0,87,1};
            if (Hid.Parse(valid,10).Level!=87 || !Hid.Parse(valid,10).Charging) Environment.Exit(1);
            valid[8]=101; if(Hid.Parse(valid,10)!=null) Environment.Exit(2);
            valid[8]=0; valid[9]=0; if(Hid.Parse(valid,10).Level!=0) Environment.Exit(3);
            valid[3]=25; if(Hid.Parse(valid,10)!=null || Hid.Parse(valid,5)!=null) Environment.Exit(4);
            return;
        }
        if (args.Length>0 && args[0]=="--diagnose") {
            string path=args.Length>1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.txt");
            var log=new StringBuilder();
            try {
                foreach(var device in Hid.Enumerate()) {
                    log.AppendLine(device.ToString());
                    var battery=Hid.Query(device,log).GetAwaiter().GetResult();
                    if(battery!=null) log.AppendLine("电量="+battery.Level+"% 充电="+battery.Charging);
                }
                if(log.Length==0) log.AppendLine("未发现 320F:5088 接收器");
            } catch(Exception e) { log.AppendLine(e.ToString()); }
            File.WriteAllText(path,log.ToString(),Encoding.UTF8); return;
        }
        bool first;
        using(var mutex=new Mutex(true,"Local\\V98ProV2BatteryTray",out first)) {
            if(!first) return;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Tray());
        }
    }
}

internal sealed class Tray : ApplicationContext {
    const string RunKey="Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string AppName="V98ProV2Battery";
    readonly NotifyIcon tray=new NotifyIcon();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=60000 };
    readonly System.Windows.Forms.Timer blink=new System.Windows.Forms.Timer { Interval=600 };
    readonly System.Windows.Forms.Timer usbRefresh=new System.Windows.Forms.Timer { Interval=700 };
    DeviceNotifications devices;
    int deviceRevision, usbAttempts;
    bool? lastNotifiedConnection;
    readonly ToolStripMenuItem status=new ToolStripMenuItem("正在读取电量…");
    readonly ToolStripMenuItem startup=new ToolStripMenuItem("开机自启动");
    readonly StringBuilder lastLog=new StringBuilder();
    bool busy, closing, notified;
    string preferred;
    Icon current;
    int displayedLevel;
    bool blinkOn=true;
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    public Tray() {
        blink.Tick+=(s,e)=> { blinkOn=!blinkOn; SetIcon(displayedLevel.ToString(),blinkOn ? Color.Red : Color.Transparent); };
        var menu=new ContextMenuStrip(); status.Enabled=false; menu.Items.Add(status);
        menu.Items.Add("立即刷新",null,async (s,e)=>await Refresh());
        startup.CheckOnClick=false;
        startup.Click+=(s,e)=>SetStartup(!startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add("复制诊断信息",null,(s,e)=> { try { Clipboard.SetText(lastLog.Length==0 ? "等待首次读取" : lastLog.ToString()); } catch(Exception ex) { MessageBox.Show(ex.Message); } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出",null,(s,e)=>ExitThread());
        tray.ContextMenuStrip=menu;
        tray.DoubleClick+=async (s,e)=>await Refresh();
        SetIcon("?",Color.Gray); tray.Text="V98 Pro V2 · 正在检测"; tray.Visible=true;
        // Register once on first launch. Explicitly disabling it in the menu persists.
        try {
            using(var prefs=Registry.CurrentUser.CreateSubKey("Software\\"+AppName)) {
                if(prefs.GetValue("Initialized")==null) {
                    SetStartup(true); if(startup.Checked) prefs.SetValue("Initialized",1);
                } else { using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) startup.Checked=key!=null && key.GetValue(AppName)!=null; }
            }
        } catch(Exception ex) { MessageBox.Show("自启动设置失败："+ex.Message, "V98 电量"); }
        timer.Tick+=async (s,e)=>await Refresh(); timer.Start();
        usbRefresh.Tick+=async (s,e)=> {
            if(busy) return;
            if(--usbAttempts<=0) usbRefresh.Stop();
            await Refresh();
        };
        try {
            devices=new DeviceNotifications();
            devices.Changed+=arrived=> {
                if(closing) return;
                deviceRevision++; preferred=null;
                if(!arrived) {
                    blink.Stop(); status.Text="2.4G 接收器已断开";
                    tray.Text="V98 Pro V2 · 接收器已断开"; SetIcon("?",Color.Gray);
                }
                if(lastNotifiedConnection!=arrived) {
                    lastNotifiedConnection=arrived;
                    tray.ShowBalloonTip(4000,"V98 电量",arrived
                        ? "检测到VGN V98proV2键盘接收器插入"
                        : "2.4G键盘接收器已拔出",ToolTipIcon.Info);
                }
                // Composite receivers emit several events. Debounce and retry while interfaces settle.
                usbAttempts=4; usbRefresh.Stop(); usbRefresh.Start();
            };
        } catch(Exception ex) { lastLog.AppendLine("USB 通知注册失败，继续定时检测："+ex.Message); }
        var initial=new System.Windows.Forms.Timer { Interval=200 };
        initial.Tick+=async (s,e)=> { initial.Stop(); initial.Dispose(); await Refresh(); }; initial.Start();
    }
    void SetStartup(bool enabled) {
        try {
            using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                if(enabled) key.SetValue(AppName,"\""+Application.ExecutablePath+"\""); else key.DeleteValue(AppName,false);
            }
            startup.Checked=enabled;
        } catch(Exception ex) { MessageBox.Show("无法设置开机启动："+ex.Message,"V98 电量"); }
    }
    async Task Refresh() {
        if(busy || closing) return; busy=true;
        int revision=deviceRevision;
        string selectedPath=preferred;
        try {
            var log=new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+"\r\n");
            Battery battery=await Task.Run(async ()=> {
                var devices=Hid.Enumerate();
                if(devices.Count==0) { log.AppendLine("未发现 320F:5088 接收器"); selectedPath=null; return null; }
                if(selectedPath!=null && !devices.Exists(d=>d.Path==selectedPath)) selectedPath=null;
                foreach(var d in devices) {
                    if(selectedPath!=null && d.Path!=selectedPath) continue;
                    log.AppendLine(d.ToString());
                    var value=await Hid.Query(d,log);
                    if(value!=null) { selectedPath=d.Path; return value; }
                }
                return null;
            });
            if(closing || revision!=deviceRevision) return;
            preferred=selectedPath;
            lastLog.Clear(); lastLog.Append(log);
            if(battery==null) { blink.Stop(); status.Text="未读取到电量（休眠 / 未连接）"; tray.Text="V98 Pro V2 · 电量未知，唤醒键盘后刷新"; SetIcon("?",Color.Gray); return; }
            string text=battery.Level+"%"+(battery.Charging ? " · 充电中" : "");
            status.Text="V98 Pro V2 · "+text;
            tray.Text=status.Text+" · "+DateTime.Now.ToString("HH:mm");
            displayedLevel=battery.Level;
            SetIcon(displayedLevel.ToString(),BatteryColor(displayedLevel));
            if(displayedLevel<20) { blinkOn=true; blink.Start(); } else { blink.Stop(); }
            if(battery.Level<=20 && !battery.Charging && !notified) { tray.ShowBalloonTip(5000,"键盘电量偏低","V98 Pro V2 剩余 "+battery.Level+"%",ToolTipIcon.Warning); notified=true; }
            if(battery.Level>25 || battery.Charging) notified=false;
        } catch(Exception ex) {
            if(!closing && revision==deviceRevision) { blink.Stop(); lastLog.Clear(); lastLog.Append(ex.ToString()); status.Text="读取失败，可复制诊断信息"; tray.Text="V98 Pro V2 · 读取失败"; SetIcon("?",Color.Gray); }
        } finally { busy=false; }
    }
    internal static Color BatteryColor(int level) {
        // HSV hue 0..120 degrees: red at 20%, yellow at 60%, green at 100%.
        double position=Math.Max(0,Math.Min(1,(level-20)/80.0));
        int red=position<=0.5 ? 255 : (int)Math.Round(510*(1-position));
        int green=position>=0.5 ? 255 : (int)Math.Round(510*position);
        return Color.FromArgb(red,green,0);
    }
    void SetIcon(string text,Color color) {
        using(var bitmap=new Bitmap(32,32)) using(var g=Graphics.FromImage(bitmap)) using(var brush=new SolidBrush(color))
        {
            g.Clear(Color.Transparent);
            DrawArtwork(g,text,brush);
            IntPtr native=bitmap.GetHicon();
            Icon next; try { next=(Icon)Icon.FromHandle(native).Clone(); } finally { DestroyIcon(native); }
            tray.Icon=next; var old=current; current=next; if(old!=null) old.Dispose();
        }
    }
    internal static void DrawArtwork(Graphics g,string text,Brush digits) {
        // Draw the keyboard first; the offset digits overlap it in the foreground.
        g.SmoothingMode=SmoothingMode.None;
        using(var outline=new SolidBrush(Color.FromArgb(185,195,205))) {
            g.FillRectangle(outline,2,0,22,2);
            g.FillRectangle(outline,2,16,22,2);
            g.FillRectangle(outline,0,2,2,14);
            g.FillRectangle(outline,24,2,2,14);
            for(int x=4;x<=20;x+=4) {
                g.FillRectangle(outline,x,4,2,2);
                g.FillRectangle(outline,x,8,2,2);
            }
            g.FillRectangle(outline,6,12,14,2);
        }
        var state=g.Save();
        try {
            g.TranslateTransform(8,7);
            g.ScaleTransform(0.75f,0.8f);
            // A narrow dark outline separates foreground segments from keyboard keys.
            var solid=digits as SolidBrush;
            if(solid!=null && solid.Color.A>0) {
                using(var shadow=new SolidBrush(Color.FromArgb(28,28,28))) {
                    foreach(var offset in new Point[] { new Point(-1,0),new Point(1,0),new Point(0,-1),new Point(0,1) }) {
                        var shifted=g.Save();
                        g.TranslateTransform(offset.X,offset.Y);
                        DrawDigits(g,text,shadow);
                        g.Restore(shifted);
                    }
                }
            }
            DrawDigits(g,text,digits);
        } finally { g.Restore(state); }
    }
    // Seven segments, drawn directly to avoid font padding at small tray sizes.
    // Coordinates align to the 16 px tray grid, preserving thick strokes after scaling.
    internal static void DrawDigits(Graphics g,string text,Brush brush) {
        int[] masks={0x3F,0x06,0x5B,0x4F,0x66,0x6D,0x7D,0x07,0x7F,0x6F};
        bool numeric=true; foreach(char c in text) if(c<'0' || c>'9') numeric=false;
        if(!numeric) {
            using(var font=new Font("Segoe UI",30,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var format=new StringFormat { Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center })
                g.DrawString(text,font,brush,new RectangleF(-1,-2,34,34),format);
            return;
        }
        float width=text.Length==3 ? 9 : 14;
        float gap=text.Length==3 ? 1 : 2;
        float start=(32-(text.Length*width+(text.Length-1)*gap))/2;
        g.SmoothingMode=SmoothingMode.None;
        for(int i=0;i<text.Length;i++) {
            int mask=masks[text[i]-'0']; float x=start+i*(width+gap), t=text.Length==3 ? 3 : 4;
            RectangleF[] segments={
                new RectangleF(x,2,width,t), new RectangleF(x+width-t,2,t,14),
                new RectangleF(x+width-t,16,t,14), new RectangleF(x,30-t,width,t),
                new RectangleF(x,16,t,14), new RectangleF(x,2,t,14),
                new RectangleF(x,14,width,t)
            };
            for(int s=0;s<7;s++) if((mask & (1<<s))!=0) g.FillRectangle(brush,segments[s]);
        }
    }
    protected override void ExitThreadCore() {
        closing=true; if(devices!=null) devices.Dispose(); usbRefresh.Stop(); usbRefresh.Dispose(); blink.Stop(); blink.Dispose(); timer.Stop(); timer.Dispose(); tray.Visible=false; tray.Dispose(); if(current!=null) current.Dispose(); base.ExitThreadCore();
    }
}
