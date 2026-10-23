using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
namespace ClipWeave;
public partial class App:Application {
    MainWindow? window;
    AppInstance? instance;
    public App(){WindowsLanguage.Apply(WindowsPreferences.Load().Language);InitializeComponent();RequestedTheme=ApplicationTheme.Light;}
    protected override async void OnLaunched(LaunchActivatedEventArgs args){
        instance=AppInstance.FindOrRegisterForKey("Clipper.Windows");
        if(!instance.IsCurrent){await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());Exit();return;}
        instance.Activated+=(_,e)=>window?.DispatcherQueue.TryEnqueue(()=>{
            if(e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch&&launch.Arguments.Contains("--settings"))window.OpenSettings();else window.Shell.ShowPanel();
        });
        window=new MainWindow();window.Activate();
    }
}
