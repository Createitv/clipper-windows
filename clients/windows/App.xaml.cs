using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
namespace ClipWeave;
public partial class App:Application {
    MainWindow? window;
    AppInstance? instance;
    public App(){UnhandledException+=(_,e)=>UiDiagnostics.Record("application-unhandled",e.Exception);AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception failure)UiDiagnostics.Record("domain-unhandled",failure);};WindowsLanguage.Apply(WindowsPreferences.Load().Language);InitializeComponent();RequestedTheme=ApplicationTheme.Light;}
    protected override async void OnLaunched(LaunchActivatedEventArgs args){
        instance=AppInstance.FindOrRegisterForKey("Clipper.Windows");
        if(!instance.IsCurrent){await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());Exit();return;}
        instance.Activated+=(_,e)=>window?.DispatcherQueue.TryEnqueue(()=>{
            if(e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch&&launch.Arguments.Contains("--settings"))window.OpenSettings();else window.Shell.ShowPanel();
        });
        try{window=new MainWindow();window.Activate();}catch(Exception failure){UiDiagnostics.Record("create-main-window",failure);throw;}
    }
}
