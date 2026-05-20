using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MBRDeepDrawer
{
    public partial class App : System.Windows.Application
    {
        private EventWaitHandle? _eventWaitHandle;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, "MBRDeepDrawerInstanceEvent", out createdNew);

            if (!createdNew)
            {
                // Signal the existing instance to show itself
                _eventWaitHandle.Set();
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // This is the first instance. Listen for signals.
            Task.Run(() =>
            {
                while (_eventWaitHandle.WaitOne())
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (System.Windows.Application.Current.MainWindow is AppDrawerWindow window)
                        {
                            window.ExternalShowDrawer();
                        }
                    });
                }
            });

            base.OnStartup(e);
        }
    }
}
