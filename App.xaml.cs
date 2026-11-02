using System.Windows;
using VoiceCraft.App_Core.ViewModels;

namespace VoiceCraft
{
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                vm.Shutdown();
            }
            base.OnExit(e);
        }
    }
}