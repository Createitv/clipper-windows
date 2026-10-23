using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
namespace ClipWeave;
public partial class App:Application {
    MainWindow? window;
    AppInstance? instance;
    public App(){InitializeComponent();RequestedTheme=ApplicationTheme.Light;}
    protected override async void OnLaunched(LaunchActivatedEventArgs args){
        instance=AppInstance.FindOrRegisterForKey("Clipper.Windows");
        if(!instance.IsCurrent){await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());Exit();return;}
        instance.Activated+=(_,_)=>window?.DispatcherQueue.TryEnqueue(()=>window.Shell.ShowPanel());
        window=new MainWindow();window.Activate();
    }
}
