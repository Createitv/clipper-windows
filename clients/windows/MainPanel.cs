using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using Windows.System;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClipWeave;
static class Palette {
    public static SolidColorBrush White=>new(Colors.White);
    public static SolidColorBrush Ink=>new(Windows.UI.Color.FromArgb(255,26,31,41));
    public static SolidColorBrush Blue=>new(Windows.UI.Color.FromArgb(255,31,100,255));
    public static SolidColorBrush Border=>new(Windows.UI.Color.FromArgb(255,230,232,237));
    public static SolidColorBrush Muted=>new(Windows.UI.Color.FromArgb(255,117,124,137));
    public static SolidColorBrush Soft=>new(Windows.UI.Color.FromArgb(255,246,247,250));
    public static TextBlock Text(string text,double size=13,bool bold=false)=>new(){Text=text,FontSize=size,FontWeight=bold?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,Foreground=Ink,TextWrapping=TextWrapping.Wrap};
    public static Button Icon(string glyph,string label,Action clicked){var b=new Button{Content=new FontIcon{Glyph=glyph,FontSize=16},Foreground=Blue,Background=new SolidColorBrush(Colors.Transparent),BorderThickness=new Thickness(0),Width=34,Height=34,Padding=new Thickness(6)};ToolTipService.SetToolTip(b,L10n.Text(label));AutomationProperties.SetName(b,L10n.Text(label));b.Click+=(_,_)=>clicked();return b;}
}
public sealed partial class MainWindow {
    readonly StackPanel groupStrip=new(){Orientation=Orientation.Horizontal,Spacing=8};
    readonly TextBlock countLabel=Palette.Text("",11);
    readonly TextBlock syncLabel=Palette.Text("",11);
    readonly Dictionary<string,BitmapImage> thumbnails=new();
    readonly ObservableCollection<JsonObject> historyItems=new();
    readonly HashSet<string> loadingThumbnails=new(),thumbnailMisses=new();
    readonly SemaphoreSlim thumbnailWork=new(1,1);
    int thumbnailEpoch;
    readonly Dictionary<string,WeakReference<Image>> thumbnailTargets=new();
    readonly FontIcon syncGlyph=new(){Glyph="\uE72C",FontSize=16,Foreground=Palette.Blue};
    readonly RotateTransform syncRotation=new();
    readonly Storyboard syncStoryboard=new();
    bool syncAnimationRunning;

    string renderedGroups="";
    string historySignature="";
    string selectedClipId="";
    List<JsonObject> visibleClips=[];
    bool historyConfigured;
    void Render() {
        renderedGroups=status["groups"]?.ToJsonString()??"";
        Detach(search);Detach(groupStrip);Detach(history);Detach(state);Detach(countLabel);
        body.Children.Clear();body.RowDefinitions.Clear();
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})body.RowDefinitions.Add(new RowDefinition{Height=height});
        var toolbar=new Grid{Padding=new Thickness(22,14,22,14),ColumnSpacing=12};
        foreach(var width in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=width});
        var logo=new Image{Width=30,Height=30,Source=new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","CliperBrand.png")))};
        toolbar.Children.Add(logo);
        var brand=Palette.Text("Clipper",16,true);brand.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(brand,1);toolbar.Children.Add(brand);
        search.Visibility=searchVisible?Visibility.Visible:Visibility.Collapsed;search.MinWidth=100;search.MaxWidth=380;search.HorizontalAlignment=HorizontalAlignment.Stretch;search.Width=double.NaN;search.CornerRadius=new CornerRadius(11);search.Background=Palette.Soft;search.BorderThickness=new Thickness(0);search.Padding=new Thickness(12,9,12,9);search.Margin=new Thickness(12,0,0,0);
        search.TextChanged-=FilterChanged;search.TextChanged+=FilterChanged;Grid.SetColumn(search,2);toolbar.Children.Add(search);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};Grid.SetColumn(actions,3);toolbar.Children.Add(actions);
        Detach(syncGlyph);syncGlyph.RenderTransform=syncRotation;syncGlyph.RenderTransformOrigin=new Windows.Foundation.Point(.5,.5);
        if(syncStoryboard.Children.Count==0){var animation=new DoubleAnimation{From=0,To=360,Duration=new Duration(TimeSpan.FromSeconds(1)),RepeatBehavior=RepeatBehavior.Forever,EnableDependentAnimation=true};Storyboard.SetTarget(animation,syncRotation);Storyboard.SetTargetProperty(animation,"Angle");syncStoryboard.Children.Add(animation);}
        actions.Children.Add(Palette.Icon("\uE721","搜索",ToggleSearch));
        var sync=Palette.Icon("\uE72C","立即同步",()=>_=Run(SyncNow));sync.Content=syncGlyph;sync.IsEnabled=AccountReady;actions.Children.Add(sync);
        var pin=Palette.Icon("\uE718","固定工具栏",()=>{});pin.Click+=(_,_)=>{Preferences.Pinned=!Preferences.Pinned;Preferences.Save();pin.Foreground=Palette.Blue;pin.Background=Preferences.Pinned?Palette.Soft:Palette.White;};pin.Foreground=Palette.Blue;pin.Background=Preferences.Pinned?Palette.Soft:Palette.White;actions.Children.Add(pin);
        var add=Palette.Icon("\uE710","添加",()=>{});var menu=new MenuFlyout();
        MenuItem(menu,"添加文字…",()=>_=Run(AddText));MenuItem(menu,"导入文件…",()=>_=Run(ImportFile));MenuItem(menu,"导入文件夹…",()=>_=Run(ImportFolder));menu.Items.Add(new MenuFlyoutSeparator());
        MenuItem(menu,(status["config"] as JsonObject)?["captureEnabled"]?.GetValue<bool>()==true?"暂停采集":"启用采集",()=>_=Run(ToggleCapture));TrackMenu(menu);add.Flyout=menu;add.IsEnabled=AccountReady;actions.Children.Add(add);
        actions.Children.Add(Palette.Icon("\uE713","设置",OpenSettings));
        body.Children.Add(toolbar);
        var navigation=new Grid{Padding=new Thickness(22,0,22,0),BorderBrush=Palette.Border,BorderThickness=new Thickness(0,1,0,1)};navigation.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});navigation.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});Grid.SetRow(navigation,1);body.Children.Add(navigation);
        UpdateGroups();
        navigation.Children.Add(new ScrollViewer{Content=groupStrip,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        Button? filter=null;var kinds=new MenuFlyout();foreach(var kind in new[]{"","text","link","image","file","folder","code","svg"}){var selected=kind;MenuItem(kinds,kind==""?"全部类型":ClipPresentation.KindLabel(kind),()=>{activeKind=selected;if(filter!=null){filter.Foreground=selected==""?Palette.Blue:Palette.Blue;ToolTipService.SetToolTip(filter,selected==""?L10n.Text("全部类型"):ClipPresentation.KindLabel(selected));}RenderHistory(true);});}TrackMenu(kinds);
        filter=Palette.Icon("\uE71C","筛选",()=>{});filter.Flyout=kinds;AutomationProperties.SetAutomationId(filter,"library-filter");Grid.SetColumn(filter,1);navigation.Children.Add(filter);
        var content=new Grid{Padding=new Thickness(6,10,6,10)};Grid.SetRow(content,2);body.Children.Add(content);
        if(!AccountReady)content.Children.Add(EmptyState(true));
        else {
            ConfigureHistory();content.Children.Add(history);
            var left=Palette.Icon("\uE76B","向左翻一屏",()=>Page(-1));left.VerticalAlignment=VerticalAlignment.Center;left.HorizontalAlignment=HorizontalAlignment.Left;left.Background=Palette.White;content.Children.Add(left);
            var right=Palette.Icon("\uE76C","向右翻一屏",()=>Page(1));right.VerticalAlignment=VerticalAlignment.Center;right.HorizontalAlignment=HorizontalAlignment.Right;right.Background=Palette.White;content.Children.Add(right);
        }
        var footer=new Grid{Padding=new Thickness(22,10,22,10),ColumnSpacing=14,BorderBrush=Palette.Border,BorderThickness=new Thickness(0,1,0,0)};foreach(var width in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})footer.ColumnDefinitions.Add(new ColumnDefinition{Width=width});Grid.SetRow(footer,3);body.Children.Add(footer);
        footer.Children.Add(countLabel);Grid.SetColumn(state,1);state.FontSize=11;state.Foreground=Palette.Muted;state.TextTrimming=TextTrimming.CharacterEllipsis;state.MaxLines=1;footer.Children.Add(state);
        var keys=Palette.Text(L10n.Text("← → 选择   空格预览   Enter 复制   Esc 收起"),11);keys.Foreground=Palette.Muted;Grid.SetColumn(keys,2);footer.Children.Add(keys);
        body.RemoveHandler(UIElement.KeyDownEvent,new KeyRoutedEventHandler(PanelKeyDown));body.AddHandler(UIElement.KeyDownEvent,new KeyRoutedEventHandler(PanelKeyDown),true);
        historySignature="";RenderHistory();UpdateSyncIndicator();
    }
    void UpdateGroups(){
        renderedGroups=status["groups"]?.ToJsonString()??"";
        groupStrip.Children.Clear();GroupButton("最近","");GroupButton("收藏","★");
        foreach(var group in (status["groups"] as JsonArray??new()).Select(x=>x?.ToString()??"").Where(x=>x.Length>0))GroupButton(group,group,false);
        groupStrip.Children.Add(Palette.Icon("\uE710","创建分组",()=>_=Run(()=>MoveClip(null))));
    }
    static void Detach(FrameworkElement element){if(element.Parent is Panel panel)panel.Children.Remove(element);else if(element.Parent is ScrollViewer scroll)scroll.Content=null;else if(element.Parent is ContentControl host)host.Content=null;}
    void ConfigureHistory() {
        if(historyConfigured)return;historyConfigured=true;
        history.SelectionMode=ListViewSelectionMode.Single;history.IsItemClickEnabled=true;history.Background=Palette.White;history.ItemsSource=historyItems;history.ItemContainerTransitions=new TransitionCollection();
        history.ItemsPanel=(ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal' ItemsUpdatingScrollMode='KeepItemsInView'/></ItemsPanelTemplate>");
        history.ItemTemplate=(DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Width='226' Margin='6,0,6,0'/></DataTemplate>");
        history.ItemContainerStyle=new Style(typeof(ListViewItem));history.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));history.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        ScrollViewer.SetHorizontalScrollMode(history,ScrollMode.Enabled);ScrollViewer.SetVerticalScrollMode(history,ScrollMode.Disabled);ScrollViewer.SetIsHorizontalRailEnabled(history,true);
        history.AddHandler(UIElement.PointerWheelChangedEvent,new PointerEventHandler(HistoryWheel),true);
        ScrollViewer.SetHorizontalScrollBarVisibility(history,ScrollBarVisibility.Hidden);ScrollViewer.SetVerticalScrollBarVisibility(history,ScrollBarVisibility.Disabled);
        history.ContainerContentChanging+=(_,e)=>{if(e.InRecycleQueue)return;if(e.ItemContainer.ContentTemplateRoot is Border root&&e.Item is JsonObject clip){root.Child=Card(clip);}else e.RegisterUpdateCallback((_,next)=>{if(!next.InRecycleQueue&&next.Item is JsonObject value&&next.ItemContainer.ContentTemplateRoot is Border border)border.Child=Card(value);});};
        history.SelectionChanged+=(_,_)=>{if(history.SelectedItem is JsonObject clip)selectedClipId=Str(clip,"id");};
        history.DoubleTapped+=(_,_)=>{if(history.SelectedItem is JsonObject clip)_=Run(()=>CopyClip(clip));};
    }
    void RenderHistory(bool resetScroll=false) {
        if(!AccountReady){countLabel.Text=L10n.Text("资料库已锁定");return;}
        var list=clips.Where(c=>ClipPresentation.Matches(c,search.Text,activeGroup,activeKind)).ToList();
        countLabel.Text=L10n.Template("{0} 条记录",list.Count);
        var signature=search.Text+"|"+activeGroup+"|"+activeKind+"|"+string.Join(";",list.Select(c=>Str(c,"id")+Str(c,"version")+Str(c,"favourite")+Str(c,"group")+Str(c,"localAvailable")));
        if(signature==historySignature)return;historySignature=signature;
        HistoryUpdates.Apply(historyItems,list);visibleClips=historyItems.ToList();
        var selected=historyItems.FirstOrDefault(c=>Str(c,"id")==selectedClipId)??historyItems.FirstOrDefault();
        if(!ReferenceEquals(history.SelectedItem,selected))history.SelectedItem=selected;
        if(resetScroll)DispatcherQueue.TryEnqueue(()=>FindScroll(history)?.ChangeView(0,null,null,true));
        if(list.Count==0)history.Footer=EmptyState(false);else history.Footer=null;
    }
    Border Card(JsonObject clip) {
        var panel=new Grid{Padding=new Thickness(16)};panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var header=new Grid();header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var type=Palette.Text(ClipPresentation.KindLabel(ClipPresentation.Kind(clip)),11,true);type.Foreground=Palette.Blue;header.Children.Add(type);
        if(clip["favourite"]?.GetValue<bool>()==true){var star=Palette.Text("★",13);star.Foreground=new SolidColorBrush(Colors.DarkOrange);Grid.SetColumn(star,1);header.Children.Add(star);}panel.Children.Add(header);
        var text=Palette.Text(Str(clip,"title",Str(clip,"text",Str(clip,"name",L10n.Text("内容")))),15);text.MaxLines=6;text.TextTrimming=TextTrimming.CharacterEllipsis;text.Margin=new Thickness(0,14,0,14);text.VerticalAlignment=VerticalAlignment.Center;Grid.SetRow(text,1);panel.Children.Add(text);
        if(ClipPresentation.Link(clip) is Uri uri){text.Text=uri.Host+"\n\n"+uri.PathAndQuery;text.FontSize=16;}
        if(ClipPresentation.Kind(clip)=="image") {
            var image=new Image{Stretch=Stretch.Uniform,Margin=new Thickness(0,12,0,12),MaxHeight=150};Grid.SetRow(image,1);panel.Children.Remove(text);panel.Children.Add(image);
            thumbnailTargets[Str(clip,"id")]=new WeakReference<Image>(image);
            if(thumbnails.TryGetValue(Str(clip,"id"),out var cached))image.Source=cached;else _=LoadThumbnail(clip,image);
        }
        var bottom=new Grid{Padding=new Thickness(0,10,0,0),BorderBrush=Palette.Border,BorderThickness=new Thickness(0,1,0,0)};bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var size=Palette.Text(ClipPresentation.Size(clip),10);size.Foreground=Palette.Muted;bottom.Children.Add(size);var when=Palette.Text(ClipPresentation.Time(clip),10);when.Foreground=Palette.Muted;Grid.SetColumn(when,1);bottom.Children.Add(when);Grid.SetRow(bottom,2);panel.Children.Add(bottom);
        var border=new Border{Background=Palette.White,BorderBrush=Palette.Border,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(16),Child=panel,MinHeight=210,ContextFlyout=ClipMenu(clip)};
        border.PointerPressed+=(_,_)=>{history.SelectedItem=clip;};return border;
    }
    async Task LoadThumbnail(JsonObject clip,Image target) {
        var id=Str(clip,"id");var stamp=id+Str(clip,"version")+Str(clip,"localAvailable");var epoch=thumbnailEpoch;
        if(thumbnailMisses.Contains(stamp)||!loadingThumbnails.Add(id))return;
        await thumbnailWork.WaitAsync();
        try{
            if(!AccountReady||epoch!=thumbnailEpoch)return;
            // Shared Go downloads, validates and encrypts remote thumbnails in the local cache.
            var value=await core.Call("imageThumbnail",new(){["clipId"]=id}) as JsonObject;
            if(value?["bytes"] is null)return;
            var bytes=await Task.Run(()=>Convert.FromBase64String(Str(value,"bytes")));
            var image=await DecodeImage(bytes,320);
            if(!AccountReady||epoch!=thumbnailEpoch)return;
            if(thumbnails.Count>=128)thumbnails.Remove(thumbnails.Keys.First());
            thumbnails[id]=image;target.Source=image;
        }catch{if(epoch==thumbnailEpoch)thumbnailMisses.Add(stamp);}
        finally{loadingThumbnails.Remove(id);thumbnailWork.Release();}
    }
    MenuFlyout ClipMenu(JsonObject clip) {
        var menu=new MenuFlyout();
        if(ClipPresentation.Link(clip) is Uri link)MenuItem(menu,"在默认浏览器中打开",()=>_=Windows.System.Launcher.LaunchUriAsync(link));
        MenuItem(menu,"复制",()=>_=Run(()=>CopyClip(clip)));MenuItem(menu,"预览",()=>_=Run(()=>PreviewClip(clip)));MenuItem(menu,clip["favourite"]?.GetValue<bool>()==true?"取消收藏":"收藏",()=>_=Run(()=>Action("favourite",new(){["clipId"]=Str(clip,"id"),["value"]=clip["favourite"]?.GetValue<bool>()!=true})));
        MenuItem(menu,"移到分组…",()=>_=Run(()=>MoveClip(clip)));MenuItem(menu,"导出…",()=>_=Run(()=>ExportClip(clip)));menu.Items.Add(new MenuFlyoutSeparator());MenuItem(menu,"删除…",()=>_=Run(()=>DeleteClip(clip)));TrackMenu(menu);return menu;
    }
    void MenuItem(MenuFlyout menu,string text,System.Action action){var item=new MenuFlyoutItem{Text=L10n.Text(text)};item.Click+=(_,_)=>action();menu.Items.Add(item);}
    void TrackMenu(MenuFlyout menu){menu.Opening+=(_,_)=>Shell.MenuOpen=true;menu.Closed+=(_,_)=>Shell.MenuOpen=false;}
    void GroupButton(string title,string value,bool translate=true){var label=Palette.Text(translate?L10n.Text(title):title,13,activeGroup==value);label.Foreground=activeGroup==value?Palette.Blue:Palette.Muted;var b=new Button{Content=new Border{Child=label,Padding=new Thickness(12,13,12,13),BorderBrush=activeGroup==value?Palette.Blue:Palette.White,BorderThickness=new Thickness(0,0,0,2)},Background=Palette.White,BorderThickness=new Thickness(0),Padding=new Thickness(0)};b.Click+=(_,_)=>{activeGroup=value;UpdateGroups();RenderHistory(true);};groupStrip.Children.Add(b);}
    FrameworkElement EmptyState(bool locked){var p=new StackPanel{Spacing=12,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,MaxWidth=400};p.Children.Add(Palette.Text(L10n.Text(locked?"你的剪贴板已安全锁定":search.Text.Length>0||activeGroup.Length>0?"没有找到匹配记录":"留住每一次灵感"),20,true));p.Children.Add(Palette.Text(L10n.Text(locked?"解锁后查看记录并继续同步":"开启采集后复制一段文字，或从这里添加内容。")));p.Children.Add(Button(L10n.Text(locked?"登录 Clipper":"添加文字"),locked?Connect:AddText));return p;}
    void FilterChanged(object sender,TextChangedEventArgs args)=>RenderHistory(true);
    void UpdateSyncIndicator(){if(DateTime.UtcNow>=noticeUntil)state.Text=L10n.Text(Str(status,"status",L10n.Text("等待云端连接")));syncLabel.Text=state.Text;var active=syncing||status["status"]?.ToString()=="正在同步";if(active&&!syncAnimationRunning)syncStoryboard.Begin();else if(!active&&syncAnimationRunning)syncStoryboard.Stop();syncAnimationRunning=active;}
    void PanelKeyDown(object sender,KeyRoutedEventArgs e) {
        if(e.Key==VirtualKey.Escape){if(previewWindow!=null)previewWindow.Close();else if(search.Visibility==Visibility.Visible)ToggleSearch();else Shell.HidePanel(true);e.Handled=true;return;}
        if(WindowsShell.ControlDown&&e.Key>=VirtualKey.Number1&&e.Key<=VirtualKey.Number9){var i=(int)e.Key-(int)VirtualKey.Number1;if(i<visibleClips.Count)_=Run(()=>CopyClip(visibleClips[i]));e.Handled=true;return;}
        if(FocusManager.GetFocusedElement(body.XamlRoot) is TextBox or PasswordBox or RichEditBox or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)return;
        if(e.Key is VirtualKey.Left or VirtualKey.Right){history.SelectedIndex=Math.Clamp(history.SelectedIndex+(e.Key==VirtualKey.Right?1:-1),0,Math.Max(0,visibleClips.Count-1));if(history.SelectedItem!=null)history.ScrollIntoView(history.SelectedItem);e.Handled=true;}
        if(history.SelectedItem is JsonObject clip&&e.Key is VirtualKey.Enter or VirtualKey.Space){_=Run(()=>e.Key==VirtualKey.Enter?CopyClip(clip):PreviewClip(clip));e.Handled=true;}
    }
    void HistoryWheel(object sender,PointerRoutedEventArgs e){
        var point=e.GetCurrentPoint(history);if(point.Properties.IsHorizontalMouseWheel)return;
        var scroll=FindScroll(history);if(scroll==null||scroll.ScrollableWidth<=0)return;
        scroll.ChangeView(Math.Clamp(scroll.HorizontalOffset-point.Properties.MouseWheelDelta*1.8,0,scroll.ScrollableWidth),null,null,true);e.Handled=true;
    }
    void Page(int direction){var scroll=FindScroll(history);if(scroll!=null)scroll.ChangeView(Math.Max(0,scroll.HorizontalOffset+direction*scroll.ViewportWidth*.85),null,null);}
    static ScrollViewer? FindScroll(DependencyObject root){if(root is ScrollViewer s)return s;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=FindScroll(VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}return null;}
}
