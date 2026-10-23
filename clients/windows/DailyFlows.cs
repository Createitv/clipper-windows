using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
namespace ClipWeave;
static class WindowsIdentity {
    public static JsonObject Information(){string model="";try{model=new Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation().SystemProductName;}catch{}
        return new(){["name"]=Environment.MachineName,["model"]=model,["platform"]="windows",["osVersion"]=Environment.OSVersion.VersionString};}
}
public sealed partial class MainWindow {
    bool searchVisible;
    internal void ChangeLanguage(string language){Preferences.Language=language;Preferences.Save();WindowsLanguage.Apply(language);DispatcherQueue.TryEnqueue(()=>{settingsWindow?.Close();previewWindow?.Close();if(loginWindow?.IsBusy!=true)loginWindow?.Close();historyItems.Clear();historySignature="";search.PlaceholderText=L10n.Text("搜索你的剪贴板");Render();Shell.RefreshLanguage();OpenSettings();});}
    void ToggleSearch(){searchVisible=!searchVisible;search.Visibility=searchVisible?Visibility.Visible:Visibility.Collapsed;if(searchVisible)search.Focus(FocusState.Programmatic);else search.Text="";}
    void RetryVisibleThumbnails(){foreach(var clip in visibleClips.Where(c=>ClipPresentation.Kind(c)=="image"&&!thumbnails.ContainsKey(Str(c,"id"))))if(thumbnailTargets.TryGetValue(Str(clip,"id"),out var weak)&&weak.TryGetTarget(out var target))_=LoadThumbnail(clip,target);}
    internal FrameworkElement DeviceCard(JsonObject device,bool request=false){
        var row=new Grid{ColumnSpacing=12};row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.Children.Add(new FontIcon{Glyph=DevicePresentation.Glyph(device),Foreground=Palette.Blue,FontSize=25,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,4,0,0)});
        var detail=new StackPanel{Spacing=5};Grid.SetColumn(detail,1);row.Children.Add(detail);detail.Children.Add(Palette.Text(DevicePresentation.Name(device),15,true));detail.Children.Add(Palette.Text(DevicePresentation.Summary(device),12));
        if(device["created"]!=null)detail.Children.Add(Palette.Text(L10n.Text(request?"申请时间":"授权时间")+" · "+DevicePresentation.Time(device["created"]),12));
        if(device["seen"]!=null)detail.Children.Add(Palette.Text(L10n.Text("最近活跃")+" · "+DevicePresentation.Time(device["seen"]),11));
        if(SettingsValues.Boolean(device,"current")||SettingsValues.Boolean(device,"revoked"))detail.Children.Add(Palette.Text(L10n.Text(SettingsValues.Boolean(device,"current")?"当前设备":"已撤销"),11));return row;
    }
    JsonObject RequestDevice(JsonObject request){var info=(request["deviceInfo"] as JsonObject)?.DeepClone().AsObject()??new();info["created"]=request["approvedAt"]?.DeepClone()??request["created"]?.DeepClone();return info;}
    ComboBox ImportGroups(){var choices=new ComboBox{Header=L10n.Text("分组"),HorizontalAlignment=HorizontalAlignment.Stretch};choices.Items.Add(L10n.Text("未分组"));foreach(var item in status["groups"] as JsonArray??new())choices.Items.Add(item?.ToString()??"");choices.SelectedIndex=0;return choices;}
    string ChosenGroup(ComboBox groups)=>groups.SelectedIndex<=0?"":groups.SelectedItem?.ToString()??"";
    async Task ReviewImports(IReadOnlyList<ImportItem> items){
        var review=new StackPanel{Spacing=16,MinWidth=400};review.Children.Add(Palette.Text(L10n.Text("确认保存后才会加入资料库，取消不会保存此内容。")));
        foreach(var item in items){review.Children.Add(Palette.Text(item.Name,15,true));if(!item.Folder&&File.Exists(item.Path)){review.Children.Add(Palette.Text(ClipPresentation.Size(new(){["size"]=new FileInfo(item.Path).Length}),12));if(new[]{".png",".jpg",".jpeg",".webp",".bmp"}.Contains(Path.GetExtension(item.Path).ToLowerInvariant())&&new FileInfo(item.Path).Length<=25*1024*1024){try{review.Children.Add(new Image{Source=await DecodeImage(await File.ReadAllBytesAsync(item.Path),600),MaxHeight=220,Stretch=Stretch.Uniform});}catch{review.Children.Add(Palette.Text(L10n.Text("预览不可用，仍可作为文件导入"),12));}}}}
        var groups=ImportGroups();review.Children.Add(groups);
        if(await Dialog(L10n.Text("保存前预览"),new ScrollViewer{Content=review,MaxHeight=500},L10n.Text("保存"))!=ContentDialogResult.Primary)return;
        var batch=new ImportBatch(items,ChosenGroup(groups));await batch.Save(core.Call);await Refresh();
        while(batch.Items.Any(x=>!x.Complete)){
            var results=new StackPanel{Spacing=14};foreach(var item in batch.Items){results.Children.Add(Palette.Text((item.Complete?"✓ ":"! ")+item.Name,14,true));if(!item.Complete)results.Children.Add(Palette.Text(L10n.Error(item.Error),12));}
            if(await Dialog(L10n.Text("操作结果"),new ScrollViewer{Content=results,MaxHeight=480},L10n.Text("重试失败项目"))!=ContentDialogResult.Primary)return;
            await batch.Save(core.Call);await Refresh();
        }
        SetNotice(L10n.Text("已完成"));
    }
    async Task ShareClip(JsonObject clip){
        var hwnd=WindowNative.GetWindowHandle(DialogOwner);var manager=DataTransferManagerInterop.GetForWindow(hwnd);
        var text=clip["text"]?.ToString();IStorageItem? file=null;
        if(text==null){var path=await ExportTemporary(clip);file=Directory.Exists(path)?await StorageFolder.GetFolderFromPathAsync(path):await StorageFile.GetFileFromPathAsync(path);}
        Windows.Foundation.TypedEventHandler<DataTransferManager,DataRequestedEventArgs>? handler=null;
        handler=(sender,args)=>{sender.DataRequested-=handler;args.Request.Data.Properties.Title=Str(clip,"name",Str(clip,"title","Clipper"));if(text!=null)args.Request.Data.SetText(text);else if(file!=null)args.Request.Data.SetStorageItems(new[]{file});};
        manager.DataRequested+=handler;try{DataTransferManagerInterop.ShowShareUIForWindow(hwnd);}catch{manager.DataRequested-=handler;throw;}
    }
    async Task OpenFullImage(JsonObject clip){var path=await ExportTemporary(clip);if(!await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path)))throw new IOException(L10n.Text("此文件暂不支持预览，可导出后使用其他应用打开。"));}
    internal async Task DeleteAccount(){
        var form=new StackPanel{Spacing=16,MaxWidth=520};form.Children.Add(Palette.Text(L10n.Text("删除将永久移除 Clipper 服务器上的账户、加密历史、附件与设备授权，无法撤销。")));form.Children.Add(Palette.Text(L10n.Text("本机加密资料库会保留，其他设备已有的本机副本不会被远程抹除。")));form.Children.Add(Palette.Text(L10n.Text("删除账户不会取消 Apple 或其他平台的订阅。请前往购买平台管理或取消订阅。")));
        var acknowledged=new CheckBox{Content=L10n.Text("我了解删除不可撤销，且不会取消订阅。")};form.Children.Add(acknowledged);var dialog=new ContentDialog{Title=L10n.Text("删除 Clipper 账户"),Content=form,PrimaryButtonText=L10n.Text("提交账户删除请求"),CloseButtonText=L10n.Text("取消"),IsPrimaryButtonEnabled=false,XamlRoot=DialogRoot};acknowledged.Checked+=(_,_)=>dialog.IsPrimaryButtonEnabled=true;acknowledged.Unchecked+=(_,_)=>dialog.IsPrimaryButtonEnabled=false;
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary||acknowledged.IsChecked!=true)return;
        deletingAccount=true;try{var receipt=await core.Call("requestAccountDeletion",new(){["confirm"]=true}) as JsonObject;await Refresh();var result=new StackPanel{Spacing=16};result.Children.Add(Palette.Text(L10n.Text("请求已提交至服务器后台处理。本机同步已断开；登记成功不代表清理已经完成。")));result.Children.Add(new TextBlock{Text=receipt?["requestId"]?.ToString()??"",IsTextSelectionEnabled=true});await Dialog(L10n.Text("删除请求已登记"),result,L10n.Text("完成"));}finally{deletingAccount=false;}ShowLogin();
    }
}
