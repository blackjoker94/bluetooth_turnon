using System;
using System.Threading;
using System.Windows.Forms;

namespace bluetooth_turnon
{
    static class Program
    {
        private const string MutexName = "Connectify_SingleInstanceMutex";
        private const string EventName = "Connectify_ShowInstanceEvent";

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew = false;
            Mutex mutex = null;
            EventWaitHandle showEvent = null;
            RegisteredWaitHandle registeredWait = null;

            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);

                if (!createdNew)
                {
                    // App is already running! Signal the running tray instance to restore and activate.
                    try
                    {
                        using (var existingEvent = EventWaitHandle.OpenExisting(EventName))
                        {
                            existingEvent.Set();
                        }
                    }
                    catch { }

                    return;
                }

                // Primary instance: create the named event to receive restore signals from second instances
                try
                {
                    showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                }
                catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool startMinimized = args.Length > 0 && args[0].ToLowerInvariant() == "-minimized";
                var form = new main();
                form.StartMinimized = startMinimized;

                if (showEvent != null)
                {
                    registeredWait = ThreadPool.RegisterWaitForSingleObject(
                        showEvent,
                        (state, timedOut) =>
                        {
                            var target = state as main;
                            if (target != null && !target.IsDisposed)
                            {
                                target.ShowAndActivate();
                            }
                        },
                        form,
                        -1,
                        false);
                }

                Application.Run(form);
            }
            finally
            {
                if (registeredWait != null)
                {
                    try { registeredWait.Unregister(null); } catch { }
                }

                if (showEvent != null)
                {
                    try { showEvent.Dispose(); } catch { }
                }

                if (mutex != null)
                {
                    if (createdNew)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }

                    try { mutex.Dispose(); } catch { }
                }
            }
        }
    }
}
