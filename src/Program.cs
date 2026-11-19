using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KosherLink
{
    static class Program
    {
        public const string Version = "1.0.4";
        public const string AppName = "KosherLink";

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "KosherLink_SingleInstance", out created))
            {
                // after a self-update the previous version is still closing: wait for it
                if (!created && Array.IndexOf(args, "--after-update") >= 0)
                {
                    try { created = mutex.WaitOne(60000); } catch (AbandonedMutexException) { created = true; }
                }
                try
                {
                    string old = System.Reflection.Assembly.GetExecutingAssembly().Location + ".old";
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                }
                catch { }
                if (!created)
                {
                    MessageBox.Show("התוכנה כבר פועלת – חפש את הסמל שלה ליד השעון.", AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("שגיאה: " + e.Exception);
                Log.Write("הפעלה, גרסה " + Version + ", תיקיית נתונים: " + Paths.Root);
                Application.Run(new TrayApp());
            }
        }

        public static Icon MakeIcon(Color color)
        {
            var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(color)) g.FillEllipse(b, 2, 2, 60, 60);
                using (var f = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("K", f, Brushes.White, new RectangleF(0, 2, 64, 62), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        public static void Msg(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(text, AppName, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }
    }

    /// <summary>Lives next to the clock. Keeps the virtual phone running and up to date.</summary>
    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _status, _autoStart;
        readonly System.Windows.Forms.Timer _timer;
        readonly Control _ui = new Control();
        readonly Icon _iconOk = Program.MakeIcon(Color.FromArgb(37, 160, 90));
        readonly Icon _iconBusy = Program.MakeIcon(Color.FromArgb(220, 150, 30));
        readonly Icon _iconErr = Program.MakeIcon(Color.FromArgb(200, 60, 60));
        int _busy; // 1 while a background job runs
        bool _exiting;
        SetupForm _setup;
        DateTime _lastAppCheck = DateTime.MinValue;

        public TrayApp()
        {
            _ui.CreateControl();
            var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
            _status = new ToolStripMenuItem("מצב: מתחיל...") { Enabled = false };
            var open = new ToolStripMenuItem("פתח וואטסאפ", null, (s, e) => OpenWhatsApp());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add("הצג את הטלפון הווירטואלי", null, (s, e) => ShowPhone());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_status);
            menu.Items.Add("בדוק עדכונים עכשיו", null, (s, e) => Job("עדכון", () => DoUpdates(true)));
            var backup = new ToolStripMenuItem("גיבוי");
            backup.DropDown.RightToLeft = RightToLeft.Yes;
            backup.DropDownItems.Add("גבה עכשיו", null, (s, e) => Job("גיבוי", () => Notify(Backup.Run())));
            backup.DropDownItems.Add("גיבוי מלא של כל הטלפון...", null, (s, e) => FullBackup());
            backup.DropDownItems.Add(new ToolStripSeparator());
            backup.DropDownItems.Add("בחר תיקיית גיבוי (למשל Google Drive)...", null, (s, e) => ChooseBackupDir());
            backup.DropDownItems.Add("פתח את תיקיית הגיבויים", null, (s, e) =>
            {
                System.IO.Directory.CreateDirectory(Backup.Dir); Process.Start("explorer.exe", "\"" + Backup.Dir + "\"");
            });
            menu.Items.Add(backup);
            menu.Items.Add("אשף התקנה ורישום", null, (s, e) => ShowSetup());
            _autoStart = new ToolStripMenuItem("הפעל אוטומטית עם Windows", null, (s, e) =>
            {
                AutoStart.Enabled = !AutoStart.Enabled; _autoStart.Checked = AutoStart.Enabled;
            });
            menu.Items.Add(_autoStart);
            menu.Items.Add("פתח את תיקיית הנתונים", null, (s, e) => Process.Start("explorer.exe", Paths.Root));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("יציאה (כיבוי הטלפון הווירטואלי)", null, (s, e) => ExitApp());
            menu.Opening += (s, e) => _autoStart.Checked = AutoStart.Enabled;

            _tray = new NotifyIcon { Icon = _iconBusy, Text = Program.AppName, ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (s, e) => OpenWhatsApp();

            SystemEvents.SessionEnding += (s, e) => { _exiting = true; try { Emulator.Stop(); } catch { } };

            _timer = new System.Windows.Forms.Timer { Interval = 2 * 60 * 1000 };
            _timer.Tick += (s, e) => Watchdog();
            _timer.Start();

            if (Config.GetBool("SetupDone")) Job("הפעלה", StartPhone);
            else ShowSetup();
        }

        void Ui(Action a)
        {
            if (_ui.IsHandleCreated) _ui.BeginInvoke(a); else a();
        }

        void SetStatus(string text, Icon icon)
        {
            Ui(() =>
            {
                _status.Text = "מצב: " + text;
                _tray.Icon = icon;
                string tip = Program.AppName + " – " + text;
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            });
        }

        /// <summary>Runs one background job at a time.</summary>
        void Job(string name, Action work)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    Log.Write(name + " נכשל: " + ex.Message);
                    SetStatus("שגיאה – " + ex.Message, _iconErr);
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        void StartPhone()
        {
            if (!AndroidSdk.ComponentsInstalled() || !AndroidSdk.AvdExists())
            {
                SetStatus("צריך להשלים התקנה", _iconErr);
                return;
            }
            bool wasRunning = Emulator.IsRunning;
            SetStatus("מפעיל את הטלפון הווירטואלי...", _iconBusy);
            Emulator.Start();
            if (!Emulator.WaitBoot(600)) throw new Exception("הטלפון הווירטואלי לא עלה");
            if (!wasRunning) Emulator.Tune(); else Emulator.LowerPriority();
            SetStatus("פועל", _iconOk);
            DoUpdates(false);
        }

        void DoUpdates(bool force)
        {
            if (_exiting) return;
            if (Emulator.IsRunning && Emulator.IsBooted && (force || WhatsAppAndroid.DueForCheck(TimeSpan.FromHours(12))))
            {
                SetStatus("בודק עדכון לוואטסאפ...", _iconBusy);
                bool updated = WhatsAppAndroid.Update(false, null);
                if (updated) Notify("וואטסאפ עודכן לגרסה " + WhatsAppAndroid.InstalledVersion);
                SetStatus("פועל", _iconOk);
            }
            if (force || DateTime.Now - _lastAppCheck > TimeSpan.FromHours(24))
            {
                _lastAppCheck = DateTime.Now;
                try
                {
                    if (AppUpdater.CheckAndApply())
                    {
                        _exiting = true;
                        Emulator.Stop();
                        Ui(() => { _tray.Visible = false; Application.Exit(); });
                    }
                }
                catch (Exception ex) { Log.Write("בדיקת עדכון לתוכנה נכשלה: " + ex.Message); }
            }
            if (force) Notify("הבדיקה הסתיימה – הכל מעודכן.");
        }

        void Watchdog()
        {
            if (_exiting || !Config.GetBool("SetupDone") || _busy != 0) return;
            Job("בדיקה תקופתית", () =>
            {
                if (!Emulator.IsRunning) { Log.Write("הטלפון הווירטואלי לא פעל – מפעיל מחדש"); StartPhone(); }
                else
                {
                    Emulator.LowerPriority();
                    DoUpdates(false);
                    if (Backup.Due && Emulator.IsBooted)
                    {
                        try { Backup.Run(); }
                        catch (Exception ex) { Log.Write("גיבוי נכשל: " + ex.Message); Notify("הגיבוי נכשל: " + ex.Message); }
                    }
                }
            });
        }

        void ChooseBackupDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "בחר תיקייה לגיבויים. כדי לגבות לענן, בחר תיקייה בתוך Google Drive או OneDrive.";
                dlg.SelectedPath = Backup.Dir;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                Backup.Dir = System.IO.Path.Combine(dlg.SelectedPath, "KosherLink Backup");
                Config.Set("BackupLast", ""); // back up to the new place soon
                Notify("הגיבויים יישמרו ב: " + Backup.Dir);
            }
        }

        void FullBackup()
        {
            if (MessageBox.Show(
                    "גיבוי מלא מעתיק את כל הטלפון הווירטואלי (כ־8GB) אל:\n" + Backup.Dir +
                    "\n\nבזמן הגיבוי (כמה דקות) הטלפון כבוי, אבל וואטסאפ במחשב ממשיך לעבוד.\nלהמשיך?",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading) != DialogResult.Yes) return;
            Job("גיבוי מלא", () =>
            {
                SetStatus("מגבה את כל הטלפון...", _iconBusy);
                Emulator.Stop();
                try { Notify(Backup.Full()); }
                finally { StartPhone(); }
            });
        }

        void Notify(string text)
        {
            Ui(() => _tray.ShowBalloonTip(4000, Program.AppName, text, ToolTipIcon.Info));
        }

        void OpenWhatsApp()
        {
            if (!Config.GetBool("SetupDone")) { ShowSetup(); return; }
            Task.Run(() =>
            {
                if (!WhatsAppDesktop.Launch())
                    Ui(() => ShowPhone()); // no Windows app installed: show the phone screen instead
            });
        }

        void ShowPhone()
        {
            Task.Run(() =>
            {
                try
                {
                    if (!Emulator.IsRunning || !Emulator.IsBooted) { Notify("הטלפון הווירטואלי עדיין עולה, נסה שוב בעוד רגע."); return; }
                    if (!PhoneViewer.IsInstalled) PhoneViewer.Install(null);
                    PhoneViewer.Show();
                }
                catch (Exception ex) { Log.Write("הצגת המסך נכשלה: " + ex.Message); Notify("הצגת המסך נכשלה: " + ex.Message); }
            });
        }

        void ShowSetup()
        {
            if (_setup != null && !_setup.IsDisposed) { _setup.Activate(); return; }
            _setup = new SetupForm();
            _setup.Finished += () => Job("הפעלה", StartPhone);
            _setup.Show();
        }

        void ExitApp()
        {
            _exiting = true;
            _timer.Stop();
            SetStatus("נסגר...", _iconBusy);
            Task.Run(() =>
            {
                try { Emulator.Stop(); } catch { }
                Ui(() => { _tray.Visible = false; Application.Exit(); });
            });
        }
    }
}
