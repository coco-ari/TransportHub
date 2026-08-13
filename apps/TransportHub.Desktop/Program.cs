using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using TransportHub.Desktop.Application;
using TransportHub.Desktop.Testing;

namespace TransportHub.Desktop
{
    internal static class Program
    {
        private const string MutexName = "Local\\TransportHub.Desktop.4D504DF5-0CB9-4AB9-A3A6-7653830CC63C";
        private const string ActivationEventName = "Local\\TransportHub.Desktop.Activate.4D504DF5-0CB9-4AB9-A3A6-7653830CC63C";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                return SelfTest.Run();
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            using (var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName))
            {
                if (!createdNew)
                {
                    activationEvent.Set();
                    return 0;
                }

                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    using (var context = new TransportHubApplicationContext())
                    {
                        var activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                            activationEvent,
                            delegate { context.ActivateFromExternalLaunch(); },
                            null,
                            Timeout.Infinite,
                            false);
                        try
                        {
                            System.Windows.Forms.Application.Run(context);
                        }
                        finally
                        {
                            activationRegistration.Unregister(null);
                        }
                    }
                    return 0;
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        "TransportHub 无法启动：" + exception.Message + Environment.NewLine + Environment.NewLine +
                        "请确认 Syncthing 已安装，并已运行项目中的 bootstrap.ps1。",
                        "TransportHub",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }
}
