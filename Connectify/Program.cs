using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    static class Program
    {
        private static Mutex _mutex = null;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            const string appName = "Connectify_SingleInstanceMutex";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // App is already running! Exit this duplicate instance.
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            bool startMinimized = args.Length > 0 && args[0].ToLowerInvariant() == "-minimized";
            var form = new main();
            form.StartMinimized = startMinimized;
            
            Application.Run(form);

            GC.KeepAlive(_mutex); // prevent garbage collection of the mutex until app exits
        }
    }
}
