using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SysInfoDash
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, "SysInfoDash_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("SysInfoDash 已经在运行中。", "SysInfoDash",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try { NativeMethods.SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                LogError(e.Exception);
                MessageBox.Show("程序发生异常：" + e.Exception.Message, "SysInfoDash",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) LogError(ex);
            };

            Application.Run(new MainForm());
        }

        private static void LogError(Exception ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysInfoDash");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "error.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + ex + Environment.NewLine);
            }
            catch { }
        }
    }
}
