using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace KosherLink
{
    /// <summary>
    /// Stand-alone "install my filter's certificate" step, for the per-filter downloads and the tray menu.
    /// Works whether or not the tray program is running: certificates go to the shared data folder, and if the
    /// virtual phone is up they are put in right away (otherwise the next phone start does it).
    /// </summary>
    static class FilterInstaller
    {
        /// <param name="what">A filter id/name (NetFree, נטפרי...), a certificate file, or empty = anything found in Windows.</param>
        public static void Run(string what)
        {
            try
            {
                var filter = what.Length > 0 && !File.Exists(what) ? FilterCert.Find(what) : null;
                string label = filter != null ? filter.Name : "הסינון";
                List<X509Certificate2> certs;

                if (File.Exists(what)) certs = FilterCert.Parse(File.ReadAllBytes(what));
                else
                {
                    certs = FilterCert.InWindows(filter);
                    if (certs.Count == 0 && filter != null && filter.Url != null)
                    {
                        Log.Write("תעודת " + filter.Name + " לא נמצאה ב־Windows – מוריד מהאתר הרשמי " + filter.Url);
                        certs = FilterCert.Download(filter);
                    }
                }

                if (certs.Count == 0)
                {
                    if (!Ask("לא נמצאה במחשב תעודת אבטחה של " + label + ".\n\n" +
                             "אם יש לך קובץ תעודה מאתר הסינון (‎.crt / .cer / .pem‏), אפשר לבחור אותו עכשיו.\n\nלבחור קובץ?")) return;
                    string file = PickFile();
                    if (file == null) return;
                    certs = FilterCert.Parse(File.ReadAllBytes(file));
                    if (certs.Count == 0) { Program.Msg("הקובץ שנבחר אינו תעודת אבטחה.", MessageBoxIcon.Warning); return; }
                }

                FilterCert.Save(certs);
                string names = FilterCert.Describe();
                string result;
                if (AndroidSdk.AvdExists() && Emulator.IsRunning && Emulator.IsBooted)
                {
                    if (FilterCert.Apply() && WhatsAppAndroid.IsInstalled)
                    {
                        Proc.Shell("am force-stop " + WhatsAppAndroid.Package);
                        WhatsAppAndroid.Launch();
                        Emulator.ScreenOff();
                    }
                    result = "תעודת האבטחה הותקנה בטלפון הווירטואלי ✅\n(" + names + ")";
                }
                else if (AndroidSdk.AvdExists())
                    result = "תעודת האבטחה נשמרה ✅\nהיא תותקן בטלפון הווירטואלי אוטומטית כשהוא יעלה.\n(" + names + ")";
                else
                    result = "תעודת האבטחה נשמרה ✅\nKosherLink עוד לא הותקן במחשב הזה – התעודה תותקן אוטומטית בסוף ההתקנה.\n(" + names + ")";
                Program.Msg(result, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Write("התקנת תעודת סינון נכשלה: " + ex.Message);
                Program.Msg("התקנת התעודה נכשלה:\n" + ex.Message, MessageBoxIcon.Error);
            }
        }

        public static string PickFile()
        {
            using (var d = new OpenFileDialog
            {
                Title = "בחר את קובץ תעודת האבטחה של הסינון",
                Filter = "תעודת אבטחה (*.crt;*.cer;*.pem;*.der)|*.crt;*.cer;*.pem;*.der|כל הקבצים|*.*"
            })
                return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
        }

        static bool Ask(string text)
        {
            return MessageBox.Show(text, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading) == DialogResult.Yes;
        }
    }
}
