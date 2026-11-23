using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Paperleaf
{
    internal static class App
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTests.Run();
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show("操作未完成，当前文档仍保留在窗口中。\n\n" + e.Exception.Message,
                    "纸间 · Paperleaf", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            app.Run(new EditorWindow(args.FirstOrDefault(a => !a.StartsWith("--"))).Window);
            return 0;
        }
    }
}
