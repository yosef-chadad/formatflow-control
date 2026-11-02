using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace FormatFlow {
 public sealed class DesktopWindow {
  public Window Window {get;}
  readonly ObservableCollection<FileCard> cards=new ObservableCollection<FileCard>();
  readonly List<FormatProfile> profiles;
  readonly Preferences preferences;
  readonly CancellationTokenSource lifetime=new CancellationTokenSource();
  readonly SemaphoreSlim previews=new SemaphoreSlim(1);
  readonly CompletionSound completionSound=new CompletionSound();
  readonly BannerImages bannerImages=new BannerImages(Path.Combine(Storage.Root,"ad-images"));
  readonly HashSet<Button> bannerButtons=new HashSet<Button>();
  readonly HashSet<string> previewed=new HashSet<string>();
  readonly string settingsPath=Path.Combine(Storage.Root,"preferences.json"),session=Path.Combine(Storage.Root,"previews",Guid.NewGuid().ToString("N"));
  string selectedExtension="mp4",formatCategory="all";Appearance publisherAppearance;Task<string> engineInitialization;
  CancellationTokenSource activity;Converter converter;bool busy,selecting,allowed=true;ChannelService channel,updateChannel;System.Windows.Threading.DispatcherTimer channelTimer;bool updateChecking;DateTime nextUpdateCheck;
  T C<T>(string name) where T:class=>Window.FindName(name) as T??throw new InvalidOperationException("Missing UI component: "+name);
  void Say(string message){C<TextBlock>("StatusText").Text=message;}
  public DesktopWindow(){
   preferences=Storage.Load(settingsPath,()=>new Preferences());profiles=Json.Read<List<FormatProfile>>(Embedded.Text("formats.json"));
   Window=(Window)XamlReader.Parse(Embedded.Text("Main.xaml"));C<ItemsControl>("Cards").ItemsSource=cards;C<TextBlock>("VersionLabel").Text="V1 · "+ChannelRules.CurrentVersion.ToString(3);selectedExtension=preferences.RememberFormat?preferences.LastFormat:"mp4";
   var work=SystemParameters.WorkArea;Window.MinWidth=Math.Min(420,work.Width-16);Window.MinHeight=Math.Min(320,work.Height-16);
   Window.Width=Math.Max(Window.MinWidth,Math.Min(preferences.Width>0?preferences.Width:1080,work.Width*.9));Window.Height=Math.Max(Window.MinHeight,Math.Min(preferences.Height>0?preferences.Height:760,work.Height*.88));Window.WindowStartupLocation=WindowStartupLocation.Manual;Window.Left=work.Left+(work.Width-Window.Width)/2;Window.Top=work.Top+(work.Height-Window.Height)/2;
   LoadPreferenceControls();ApplyTheme();Refresh();HookEvents();
   Directory.CreateDirectory(session);
   using(var icon=Embedded.Open("App.ico")){var bitmap=new IconBitmapDecoder(icon,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.Last();bitmap.Freeze();C<Image>("SplashLogo").Source=bitmap;Window.Icon=bitmap;}
   foreach(string name in new[]{"SettingsCard","FormatCard","TextCard"})C<Border>(name).Effect=new DropShadowEffect{Color=Colors.Black,BlurRadius=38,ShadowDepth=10,Opacity=.28};
   Window.Loaded+=async(s,e)=>{
    StartSplashMotion();
    try{
     Buttons();_ =InitializeChannel();var minimumSplash=Task.Delay(1000,lifetime.Token);
     await minimumSplash;
     if(preferences.Animations){C<Border>("SplashOverlay").BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(180)));await Task.Delay(180,lifetime.Token);}
     C<Border>("SplashOverlay").Visibility=Visibility.Collapsed;C<Image>("SplashLogo").RenderTransform=Transform.Identity;AnimateWelcome();
     if(allowed)_ =PrepareEngineSafely();
    }catch(OperationCanceledException){}catch(Exception ex){C<Border>("SplashOverlay").Visibility=Visibility.Collapsed;Say(ex.Message);}
   };
  }
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
  void NativeChrome(){try{var handle=new WindowInteropHelper(Window).Handle;if(handle==IntPtr.Zero)return;int dark=preferences.Theme==0?1:0;if(DwmSetWindowAttribute(handle,20,ref dark,4)!=0)DwmSetWindowAttribute(handle,19,ref dark,4);int rounded=2;DwmSetWindowAttribute(handle,33,ref rounded,4);}catch{}}
  void HookEvents(){
   Window.SourceInitialized+=(s,e)=>NativeChrome();
   EventManager.RegisterClassHandler(typeof(Button),FrameworkElement.LoadedEvent,new RoutedEventHandler((s,e)=>{if(e.OriginalSource is Button button&&button.Name=="AdLink"){bannerButtons.Add(button);AnimateBanner(button);}}));
   EventManager.RegisterClassHandler(typeof(Button),FrameworkElement.UnloadedEvent,new RoutedEventHandler((s,e)=>{if(e.OriginalSource is Button button&&bannerButtons.Remove(button))button.RenderTransform=Transform.Identity;}));
   C<Button>("SettingsButton").Click+=(s,e)=>ShowOverlay("SettingsOverlay");
   C<Button>("CloseSettings").Click+=(s,e)=>{SavePreferences();C<Border>("SettingsOverlay").Visibility=Visibility.Collapsed;};
   C<Button>("SaveSettings").Click+=(s,e)=>{SavePreferences();C<Border>("SettingsOverlay").Visibility=Visibility.Collapsed;};
   C<Button>("DarkThemeButton").Click+=(s,e)=>{preferences.Theme=0;ApplyTheme();};C<Button>("LightThemeButton").Click+=(s,e)=>{preferences.Theme=1;ApplyTheme();};
   C<Slider>("Brightness").ValueChanged+=(s,e)=>{preferences.Brightness=C<Slider>("Brightness").Value;C<Border>("Dimmer").Opacity=1-preferences.Brightness;};
   C<Button>("ChooseOutput").Click+=(s,e)=>{using(var dialog=new System.Windows.Forms.FolderBrowserDialog{Description="בחר תיקייה לקבצים המומרים",SelectedPath=preferences.Output}){if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK){preferences.Output=dialog.SelectedPath;C<TextBlock>("OutputBox").Text=preferences.Output;C<TextBlock>("OutputBox").ToolTip=preferences.Output;}}};
   C<Button>("AboutButton").Click+=(s,e)=>ShowAbout();
   C<Button>("TermsButton").Click+=(s,e)=>ShowTerms();
   C<Button>("LicensesButton").Click+=(s,e)=>ShowLicenses();
   C<Button>("CloseText").Click+=(s,e)=>C<Border>("TextOverlay").Visibility=Visibility.Collapsed;
   C<Button>("TextDone").Click+=(s,e)=>C<Border>("TextOverlay").Visibility=Visibility.Collapsed;
   C<Button>("ResetSettings").Click+=(s,e)=>ResetPreferences();
   C<Button>("FormatButton").Click+=(s,e)=>OpenFormats();
   C<Button>("CloseFormats").Click+=(s,e)=>C<Border>("FormatOverlay").Visibility=Visibility.Collapsed;
   C<TextBox>("FormatSearch").TextChanged+=(s,e)=>RenderFormatTiles();
   C<CheckBox>("Animations").Checked+=(s,e)=>{preferences.Animations=true;AnimateWelcome();AnimateBanners();};C<CheckBox>("Animations").Unchecked+=(s,e)=>{preferences.Animations=false;AnimateWelcome();AnimateBanners();};
   C<Button>("BrowseButton").Click+=(s,e)=>Browse();
   foreach(string overlay in new[]{"SettingsOverlay","FormatOverlay","TextOverlay"}){var border=C<Border>(overlay);border.MouseDown+=(s,e)=>{if(ReferenceEquals(e.OriginalSource,border)){if(overlay=="SettingsOverlay")SavePreferences();border.Visibility=Visibility.Collapsed;}};}
   C<Button>("OpenButton").Click+=(s,e)=>{try{Directory.CreateDirectory(preferences.Output);Process.Start(new ProcessStartInfo("explorer.exe",ProcessRunner.Quote(preferences.Output)){UseShellExecute=true});}catch(Exception ex){Say(ex.Message);}};
   C<Button>("ClearButton").Click+=(s,e)=>{if(busy)return;cards.Clear();Refresh();Say("");};
   C<CheckBox>("SelectAll").Click+=(s,e)=>{selecting=true;foreach(var card in cards)card.IsSelected=C<CheckBox>("SelectAll").IsChecked==true;selecting=false;Formats();};
   C<Button>("CancelButton").Click+=(s,e)=>{activity?.Cancel();Say("מבטל את הפעולה...");};
   C<Button>("ConvertButton").Click+=async(s,e)=>await ConvertSelected();C<Button>("OpenResults").Click+=(s,e)=>OpenResults();
   C<Button>("InstallUpdate").Click+=(s,e)=>InstallUpdate(true);
   C<Button>("RefreshPublisher").Click+=async(s,e)=>{C<Button>("RefreshPublisher").IsEnabled=false;try{await CheckChannel(false);nextUpdateCheck=DateTime.MinValue;_ =CheckForUpdate();}finally{C<Button>("RefreshPublisher").IsEnabled=true;}};
   C<Button>("RetryChannel").Click+=async(s,e)=>{C<Button>("RetryChannel").IsEnabled=false;try{await CheckChannel(false);if(allowed)await EnsureEngine();}finally{C<Button>("RetryChannel").IsEnabled=true;}};
   C<Button>("CloseBlocked").Click+=(s,e)=>Window.Close();
   C<Button>("OpenSaved").Click+=(s,e)=>{try{Directory.CreateDirectory(preferences.Output);Process.Start(new ProcessStartInfo("explorer.exe",ProcessRunner.Quote(preferences.Output)){UseShellExecute=true});}catch(Exception ex){Say(ex.Message);}};
   Window.AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler((s,e)=>{
    var button=e.OriginalSource as Button;if(button==null)return;
    try{
     if(ReferenceEquals(button.Parent,C<WrapPanel>("FormatTabs"))){formatCategory=button.Tag as string??"all";RenderFormatTiles();return;}
     switch(button.Name){case "ChooseFormatTile":ChooseFormat(button.Tag as string);break;case "RemoveFile":if(!busy){cards.Remove((FileCard)button.Tag);Refresh();}break;case "RevealFile":RevealFile((FileCard)button.Tag);break;case "AdLink":OpenLink(button.Tag as string);break;}}
    catch(Exception ex){Say(ex.Message);}
   }));
   Window.DragOver+=(s,e)=>{e.Effects=!busy&&allowed&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};Window.Drop+=(s,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop))AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));};
   C<Grid>("DragHandle").MouseLeftButtonDown+=(s,e)=>{
    DependencyObject element=e.OriginalSource as DependencyObject;while(element!=null&&element!=C<Grid>("DragHandle")){if(element is ButtonBase)return;element=VisualTreeHelper.GetParent(element);}
    if(e.ClickCount==2)Window.WindowState=Window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;else if(Window.WindowState==WindowState.Normal)Window.DragMove();
   };
   Window.SizeChanged+=(s,e)=>Resize();Window.PreviewKeyDown+=async(s,e)=>{
    if(e.Key==Key.Escape){foreach(string name in new[]{"TextOverlay","FormatOverlay","SettingsOverlay"}){if(C<Border>(name).Visibility==Visibility.Visible){if(name=="SettingsOverlay")SavePreferences();C<Border>(name).Visibility=Visibility.Collapsed;e.Handled=true;break;}}}
    if((Keyboard.Modifiers&ModifierKeys.Control)!=0){if(e.Key==Key.O){e.Handled=true;if(!busy&&allowed)Browse();}else if(e.Key==Key.Enter){e.Handled=true;await ConvertSelected();}else if(e.Key==Key.L){e.Handled=true;if(!busy&&allowed)OpenFormats();}}
   };
   Window.Closing+=(s,e)=>{if(busy){e.Cancel=true;activity?.Cancel();Say("מבטל את הפעולה. ניתן לסגור שוב לאחר הסיום.");return;}try{SavePreferences();}catch{}channelTimer?.Stop();lifetime.Cancel();};
   Window.Closed+=(s,e)=>{foreach(string name in new[]{"AdTop","AdSearch"})(C<ContentControl>(name).Content as BannerPresentation)?.Dispose();bannerImages.Dispose();completionSound.Dispose();try{Directory.Delete(session,true);}catch{}};
  }
  void Browse(){if(!allowed)return;var dialog=new OpenFileDialog{Multiselect=true,Title="בחר קבצים להמרה",Filter="All files|*.*"};if(dialog.ShowDialog(Window)==true)AddFiles(dialog.FileNames);}
  void LoadPreferenceControls(){C<TextBlock>("OutputBox").Text=preferences.Output;C<TextBlock>("OutputBox").ToolTip=preferences.Output;C<Slider>("Brightness").Value=preferences.Brightness;C<CheckBox>("OpenAfter").IsChecked=preferences.OpenAfter;C<CheckBox>("KeepMetadata").IsChecked=preferences.KeepMetadata;C<CheckBox>("SoundOnComplete").IsChecked=preferences.SoundOnComplete;C<CheckBox>("RememberFormat").IsChecked=preferences.RememberFormat;C<CheckBox>("Animations").IsChecked=preferences.Animations;}
  void ResetPreferences(){var defaults=new Preferences();foreach(var property in typeof(Preferences).GetProperties())property.SetValue(preferences,property.GetValue(defaults));selectedExtension="mp4";LoadPreferenceControls();ApplyTheme();AnimateWelcome();AnimateBanners();Formats();SavePreferences();Say("ההעדפות שוחזרו לברירת המחדל. הקבצים שלך נשמרו.");}
  void ShowOverlay(string name){var border=C<Border>(name);border.Visibility=Visibility.Visible;border.BeginAnimation(UIElement.OpacityProperty,null);border.Opacity=1;border.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));if(preferences.Animations){border.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(150)));}}
  void AnimateWelcome(){var title=C<TextBlock>("WelcomeTitle");title.BeginAnimation(UIElement.OpacityProperty,null);var translate=new TranslateTransform();title.RenderTransform=translate;title.Opacity=1;if(!preferences.Animations)return;translate.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(-2.5,2.5,TimeSpan.FromSeconds(2.4)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}});title.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(.84,1,TimeSpan.FromSeconds(2.4)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});}
  void AnimateBanners(){foreach(var button in bannerButtons)AnimateBanner(button);}
  void AnimateBanner(Button button){
   button.RenderTransform=Transform.Identity;if(!preferences.Animations)return;
   var scale=new ScaleTransform(1,1);button.RenderTransform=scale;
   var pulse=new DoubleAnimation(1,1.012,TimeSpan.FromSeconds(3)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};
   scale.BeginAnimation(ScaleTransform.ScaleXProperty,pulse);scale.BeginAnimation(ScaleTransform.ScaleYProperty,pulse);
  }
  void StartSplashMotion(){if(!preferences.Animations)return;var scale=new ScaleTransform();C<Image>("SplashLogo").RenderTransform=scale;var pulse=new DoubleAnimation(.94,1.04,TimeSpan.FromMilliseconds(500)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever};scale.BeginAnimation(ScaleTransform.ScaleXProperty,pulse);scale.BeginAnimation(ScaleTransform.ScaleYProperty,pulse);}
  void OpenFormats(){if(busy||!allowed)return;formatCategory="all";C<TextBox>("FormatSearch").Text="";RenderFormatTiles();ShowOverlay("FormatOverlay");C<TextBox>("FormatSearch").Focus();}
  void RenderFormatTiles(){var tiles=FormatPicker.Tiles(profiles,cards,selectedExtension,formatCategory,C<TextBox>("FormatSearch").Text);C<ItemsControl>("FormatTiles").ItemsSource=tiles;C<ItemsControl>("FormatTiles").Tag=Window.ActualWidth<650?3:4;C<TextBlock>("FormatCount").Text=FormatRules.Options(profiles,cards).Count+" יעדים מתאימים · "+profiles.Count+" פורמטים בסך הכול";C<TextBlock>("FormatWatermark").Visibility=string.IsNullOrEmpty(C<TextBox>("FormatSearch").Text)?Visibility.Visible:Visibility.Collapsed;C<TextBlock>("NoFormats").Visibility=tiles.Count==0?Visibility.Visible:Visibility.Collapsed;foreach(var tab in C<WrapPanel>("FormatTabs").Children.OfType<Button>()){bool active=(string)tab.Tag==formatCategory;tab.Background=active?(Brush)Window.Resources["Field"]:Brushes.Transparent;tab.Foreground=active?(Brush)Window.Resources["Accent"]:(Brush)Window.Resources["Muted"];}}
  void ChooseFormat(string extension){var profile=profiles.FirstOrDefault(p=>p.ext==extension);if(profile==null||!cards.Where(c=>c.IsSelected).All(c=>FormatRules.Accepts(profile,c.Path)))return;selectedExtension=extension;Formats();C<Border>("FormatOverlay").Visibility=Visibility.Collapsed;}
  void ShowSections(string title,IEnumerable<TextSection> sections){C<TextBlock>("TextTitle").Text=title;C<ItemsControl>("TextSections").ItemsSource=sections;C<ItemsControl>("TextSections").Visibility=Visibility.Visible;C<TextBox>("LicenseText").Visibility=Visibility.Collapsed;C<Button>("LicensesButton").Visibility=Visibility.Visible;ShowOverlay("TextOverlay");}
  void ShowTerms(){ShowSections("תנאי השימוש — FormatFlow",Json.Read<List<TextSection>>(Embedded.Text("Terms.json")));}
  void ShowAbout(){ShowSections("אודות FormatFlow",new[]{new TextSection{Title="FormatFlow · V1",Body="ממיר קבצים מקומי ל־Windows. תמונות, וידאו, שמע וטבלאות — במקום אחד, עם 41 פורמטי פלט. התוכנה נפתחת מקובץ EXE אחד והקבצים נשארים במחשב שלך."},new TextSection{Title="קיצורי מקלדת",Body="Ctrl+O — הוספת קבצים.\nCtrl+Enter — התחלת המרה.\nCtrl+L — בחירת פורמט.\nEsc — סגירת החלונית העליונה."},new TextSection{Title="גרסה ורישיונות",Body="מהדורה V1 · גרסת קובץ "+ChannelRules.CurrentVersion.ToString(3)+".\nהתוכנה משתמשת ברכיבי קוד פתוח, לרבות FFmpeg. כפתור רישיונות הרכיבים מציג את הודעות הזכויות ואת הרישיונות הנפרדים."}});}
  void ShowLicenses(){C<TextBlock>("TextTitle").Text="רישיונות רכיבים";C<ItemsControl>("TextSections").Visibility=Visibility.Collapsed;C<TextBox>("LicenseText").Text=Embedded.Text("Notices.txt");C<TextBox>("LicenseText").Visibility=Visibility.Visible;C<Button>("LicensesButton").Visibility=Visibility.Collapsed;ShowOverlay("TextOverlay");}
  void SavePreferences(){preferences.Brightness=C<Slider>("Brightness").Value;preferences.OpenAfter=C<CheckBox>("OpenAfter").IsChecked==true;preferences.KeepMetadata=C<CheckBox>("KeepMetadata").IsChecked==true;preferences.SoundOnComplete=C<CheckBox>("SoundOnComplete").IsChecked==true;preferences.RememberFormat=C<CheckBox>("RememberFormat").IsChecked==true;preferences.Animations=C<CheckBox>("Animations").IsChecked==true;preferences.LastFormat=preferences.RememberFormat?selectedExtension:"mp4";if(Window.WindowState==WindowState.Normal){preferences.Width=Window.Width;preferences.Height=Window.Height;}Storage.Atomic(settingsPath,Json.Write(preferences));}
  void ApplyTheme(){
   var dark=new Dictionary<string,string>{{"Page","#101114"},{"Card","#202127"},{"Field","#2B2C34"},{"Ink","#F2F0FA"},{"Muted","#A8A8B7"},{"Line","#3B3B47"},{"Accent","#B2A0FF"}};
   var light=new Dictionary<string,string>{{"Page","#F7F6FA"},{"Card","#FFFFFF"},{"Field","#EBE8F1"},{"Ink","#252331"},{"Muted","#6A6677"},{"Line","#DEDAE7"},{"Accent","#7654B8"}};
   foreach(var pair in preferences.Theme==1?light:dark)Window.Resources[pair.Key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
   var glow=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(1,1)};glow.GradientStops.Add(new GradientStop(Color.FromRgb(133,154,255),0));glow.GradientStops.Add(new GradientStop(Color.FromRgb(188,139,244),.6));glow.GradientStops.Add(new GradientStop(Color.FromRgb(232,168,205),1));Window.Resources["Glow"]=glow;
   if(publisherAppearance!=null&&!string.IsNullOrEmpty(publisherAppearance.accent)){var accent=(Color)ColorConverter.ConvertFromString(publisherAppearance.accent);Window.Resources["Accent"]=new SolidColorBrush(accent);Window.Resources["Glow"]=new LinearGradientBrush(accent,Color.FromRgb((byte)Math.Min(255,accent.R+35),(byte)Math.Min(255,accent.G+25),(byte)Math.Min(255,accent.B+12)),45);}
   foreach(string name in new[]{"DarkThemeButton","LightThemeButton"}){bool selected=(name=="DarkThemeButton")== (preferences.Theme==0);C<Button>(name).Background=selected?(Brush)Window.Resources["Accent"]:Brushes.Transparent;C<Button>(name).Foreground=selected?new SolidColorBrush(Color.FromRgb(28,25,38)):(Brush)Window.Resources["Ink"];}
   C<Border>("Dimmer").Opacity=1-Math.Max(.6,Math.Min(1,preferences.Brightness));NativeChrome();
  }
  void Resize(){double w=Window.ActualWidth,h=Window.ActualHeight;C<StackPanel>("ContentPanel").Margin=new Thickness(w<650?14:30,0,w<650?14:30,20);C<TextBlock>("WelcomeTitle").FontSize=Math.Max(25,Math.Min(42,w/25));C<ColumnDefinition>("FormatColumn").Width=new GridLength(w<600?110:146);C<StackPanel>("Hero").Margin=new Thickness(0,Math.Max(12,(cards.Count==0?Math.Max(18,Math.Min(110,(h-410)/2)):25)-(C<ContentControl>("AdTop").Visibility==Visibility.Visible?112:0)),0,30);foreach(string name in new[]{"SettingsCard","FormatCard","TextCard"})C<Border>(name).MaxHeight=Math.Max(170,h-82);C<ItemsControl>("FormatTiles").Tag=w<650?3:4;C<TextBlock>("BrowseLabel").MaxWidth=Math.Max(90,Math.Min(820,w-70)-(w<600?110:146)-64-64);C<TextBlock>("BrowseLabel").FontSize=w<600?11:13;C<TextBlock>("PrivacyLabel").Visibility=w<520?Visibility.Collapsed:Visibility.Visible;}
  void Refresh(){C<StackPanel>("QueueSection").Visibility=cards.Count==0?Visibility.Collapsed:Visibility.Visible;C<TextBlock>("BrowseLabel").Text=cards.Count==0?"גרור קבצים לכאן או לחץ כדי לבחור":"הוסף או גרור עוד קבצים · "+cards.Count;Resize();Formats();}
  void Formats(){var options=FormatRules.Options(profiles,cards);if(!options.Any(p=>p.Extension==selectedExtension))selectedExtension=options.FirstOrDefault()?.Extension??"zip";C<TextBlock>("SelectedFormatLabel").Text=selectedExtension.ToUpperInvariant();if(C<Border>("FormatOverlay").Visibility==Visibility.Visible)RenderFormatTiles();Buttons();}
  void Buttons(){C<Button>("ConvertButton").IsEnabled=!busy&&allowed&&converter!=null;C<Button>("OpenResults").IsEnabled=!busy&&cards.Any(c=>c.HasOutput&&File.Exists(c.Output));C<TextBlock>("CountLabel").Text=cards.Count+" קבצים · "+cards.Count(c=>c.IsSelected)+" נבחרו";}
  void Busy(bool value){busy=value;foreach(var name in new[]{"BrowseButton","ClearButton","FormatButton"})C<Button>(name).IsEnabled=!value;C<CheckBox>("SelectAll").IsEnabled=!value;C<Button>("CancelButton").IsEnabled=value;foreach(var card in cards)card.IsEditable=!value;Buttons();RefreshAvailabilityScreen();}
  void AddFiles(IEnumerable<string> paths){
   if(busy||!allowed)return;selecting=true;
   foreach(string path in paths){if(!File.Exists(path)||cards.Any(c=>string.Equals(c.Path,path,StringComparison.OrdinalIgnoreCase)))continue;var info=new FileInfo(path);string ext=info.Extension.TrimStart('.').ToUpperInvariant(),icon="≡";if(new[]{"MP4","MOV","MKV","WEBM","AVI","WMV","FLV","M4V","MPG","MPEG","TS","3GP","OGV","VOB","M2TS"}.Contains(ext))icon="▶";else if(new[]{"MP3","WAV","FLAC","AAC","M4A","OGG","OPUS","WMA","AIFF","AC3","AU","MKA"}.Contains(ext))icon="♫";else if(new[]{"PNG","JPG","JPEG","WEBP","BMP","TIF","TIFF","GIF","ICO","TGA","PPM","PGM","PAM","PBM","AVIF","HEIC"}.Contains(ext))icon="◈";
    var card=new FileCard{Name=info.Name,Path=info.FullName,Detail=ext+" · "+(info.Length/1048576.0).ToString("N1")+" MB",Icon=icon};card.PropertyChanged+=(s,e)=>{if(e.PropertyName==nameof(FileCard.IsSelected)&&!selecting)Formats();};cards.Add(card);Preview(card);
   }
   selecting=false;Refresh();
  }
  async void Preview(FileCard card){
   if(converter==null||!previewed.Add(card.Id)||card.Icon=="≡")return;
   try{await previews.WaitAsync(lifetime.Token);try{string path=Path.Combine(session,card.Id+".png");await converter.ThumbnailAsync(card.Path,path,lifetime.Token);if(lifetime.IsCancellationRequested)return;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=410;bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();card.Thumbnail=bitmap;}finally{previews.Release();}}
   catch(OperationCanceledException){}catch{}
  }
  async Task ConvertSelected(){
   if(busy||!allowed||converter==null)return;var selected=cards.Where(c=>c.IsSelected).ToList();if(selected.Count==0){Say("בחר לפחות קובץ אחד.");return;}string ext=selectedExtension;var profile=profiles.FirstOrDefault(p=>p.ext==ext);if(profile==null){Say("בחר פורמט פלט מתאים.");return;}
   SavePreferences();activity=new CancellationTokenSource();Busy(true);int success=0,errors=0;
   foreach(var card in selected){card.Progress=0;card.Output="";card.Status="ממתין בתור";}
   try{
    foreach(var card in selected){
     if(activity.IsCancellationRequested){card.Status="בוטל";continue;}
     var progress=new Progress<ConversionProgress>(p=>{card.Progress=p.Percent;card.IsIndeterminate=p.Indeterminate;card.Status=p.Indeterminate?"ממיר...":"ממיר · "+p.Percent.ToString("0")+"%";});
     try{card.Output=await converter.ConvertAsync(profile,card.Path,preferences.Output,preferences.KeepMetadata,progress,activity.Token);card.Progress=100;card.IsIndeterminate=false;card.Status="ההמרה הושלמה";success++;}
     catch(OperationCanceledException){card.Status="בוטל";card.IsIndeterminate=false;}catch(Exception ex){card.Status=ex.Message;card.IsIndeterminate=false;errors++;}
    }
    Say("הסתיים · "+success+" קבצים נשמרו בתיקייה שבהגדרות · "+errors+" שגיאות");if(preferences.SoundOnComplete&&success>0){try{completionSound.Play();}catch{}}if(preferences.OpenAfter&&success>0)Process.Start(new ProcessStartInfo("explorer.exe",ProcessRunner.Quote(preferences.Output)){UseShellExecute=true});
   }finally{Busy(false);activity.Dispose();activity=null;}
  }
  void RevealFile(FileCard card){
   if(card==null||!card.HasOutput)return;
   if(!File.Exists(card.Output)){Say("קובץ התוצאה הועבר או נמחק.");return;}
   Process.Start(new ProcessStartInfo("explorer.exe","/select,"+ProcessRunner.Quote(card.Output)){UseShellExecute=true});
  }
  void OpenResults(){
   var card=cards.LastOrDefault(c=>c.HasOutput&&File.Exists(c.Output));
   if(card==null){Say("לא נמצאו קובצי תוצאה להצגה.");return;}
   Process.Start(new ProcessStartInfo("explorer.exe",ProcessRunner.Quote(Path.GetDirectoryName(card.Output))){UseShellExecute=true});
  }
  static void OpenLink(string url){if(Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&string.IsNullOrEmpty(uri.UserInfo))Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
  async Task PrepareEngineSafely(){try{await EnsureEngine();}catch(OperationCanceledException){}catch(Exception ex){Say(ex.Message);}}
  async Task EnsureEngine(){if(converter!=null)return;if(engineInitialization==null)engineInitialization=Embedded.EngineAsync();Say("מכין את מנוע ההמרה...");converter=new Converter(await engineInitialization);Say("");Buttons();foreach(var card in cards)Preview(card);}
  async Task InitializeChannel(){
   channel=new ChannelService();updateChannel=new ChannelService();var cached=ChannelService.ReadCache();if(cached!=null)ApplyChannelState(cached,false);else ApplyPolicy(Storage.Load(Path.Combine(Storage.Root,"policy.json"),()=>new Availability{enabled=true}));
   channelTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(10)};
   channelTimer.Tick+=async(s,e)=>{await CheckChannel(false);_ =CheckForUpdate();};channelTimer.Start();
   await CheckChannel(false);
   if(allowed&&channel.Automatic&&channel.UpdatePath!=null){InstallUpdate(false);return;}
   _ =CheckForUpdate();
  }
  void RefreshAvailabilityScreen(){C<Border>("BlockedOverlay").Visibility=!allowed&&!busy?Visibility.Visible:Visibility.Collapsed;C<Border>("PolicyBanner").Visibility=!allowed&&busy?Visibility.Visible:Visibility.Collapsed;}
  void ApplyPolicy(Availability policy){allowed=policy.enabled;string message=string.IsNullOrWhiteSpace(policy.message)?"התוכנה אינה זמינה כרגע מטעם המפרסם. ניתן לבדוק שוב בהמשך.":policy.message;C<TextBlock>("PolicyText").Text=message;C<TextBlock>("BlockedMessage").Text=message;RefreshAvailabilityScreen();Buttons();}
  void ApplyChannelState(ChannelFeed state,bool showAds=true){
   ApplyPolicy(state.policy);publisherAppearance=state.appearance;C<TextBlock>("WelcomeTitle").Text=string.IsNullOrWhiteSpace(state.appearance.title)?"Welcome to Format Switcher":state.appearance.title;C<TextBlock>("WelcomeSubtitle").Text=string.IsNullOrWhiteSpace(state.appearance.subtitle)?"בחר-המר-הורד":state.appearance.subtitle;ApplyTheme();
   foreach(string name in new[]{"AdTop","AdSearch"}){
    var slot=C<ContentControl>(name);string placement=name=="AdTop"?"top":"belowSearch";var ad=showAds?state.ads.FirstOrDefault(a=>a.placement==placement):null;var view=slot.Content as BannerPresentation;
    if(ad==null){view?.Dispose();slot.Content=null;slot.Visibility=Visibility.Collapsed;continue;}
    if(view==null||!view.Matches(ad)){view?.Dispose();view=new BannerPresentation(ad,lifetime.Token);slot.Content=view;}
    slot.Visibility=Visibility.Visible;_ =view.LoadAsync(bannerImages);
   }
   Resize();
  }
  async Task CheckChannel(bool downloadUpdate=true){
   try{
    var result=await channel.CheckAsync(lifetime.Token,downloadUpdate);if(result==null)return;ApplyChannelState(result.State);
    C<TextBlock>("PublisherStatus").Text=channel.CacheError==null?"מחובר · ההגדרות עודכנו ("+result.State.revision+")":"מחובר · ההגדרות מוצגות אך לא נשמרו במחשב";
    WriteChannelStatus(result.State.revision,channel.CacheError);
    C<Button>("InstallUpdate").Visibility=result.UpdatePath==null?Visibility.Collapsed:Visibility.Visible;
    C<TextBlock>("UpdateText").Text=result.UpdatePath==null?"":("גרסה "+result.Version+(result.Automatic?" תותקן אוטומטית בהפעלה הבאה. אפשר להתקין גם כעת.":" מוכנה להתקנה."));
    if(allowed&&converter==null&&C<Border>("SplashOverlay").Visibility!=Visibility.Visible)await EnsureEngine();
   }catch(OperationCanceledException){if(!lifetime.IsCancellationRequested)PublisherUnavailable("החיבור לא השלים את הבדיקה בזמן");}catch(Exception ex){PublisherUnavailable(ex.Message);}
  }
  void PublisherUnavailable(string message){
   foreach(string name in new[]{"AdTop","AdSearch"}){var slot=C<ContentControl>(name);(slot.Content as BannerPresentation)?.Dispose();slot.Content=null;slot.Visibility=Visibility.Collapsed;}
   C<TextBlock>("PublisherStatus").Text="לא התקבלו הגדרות עדכניות · ננסה שוב אוטומטית";
   C<TextBlock>("PublisherStatus").ToolTip=message;WriteChannelStatus(null,message);Resize();
  }
  void WriteChannelStatus(int? revision,string error){try{Storage.Atomic(Path.Combine(Storage.Root,"channel-status.json"),Json.Write(new{version=ChannelRules.CurrentVersion.ToString(3),checkedUtc=DateTime.UtcNow.ToString("O"),revision,error=error==null?null:error.Substring(0,Math.Min(1500,error.Length))}));}catch{}}
  async Task CheckForUpdate(){
   if(updateChannel==null||updateChecking||DateTime.UtcNow<nextUpdateCheck)return;
   updateChecking=true;nextUpdateCheck=DateTime.UtcNow.AddMinutes(1);
   try{
    var result=await updateChannel.CheckAsync(lifetime.Token,true,false);if(result?.UpdatePath==null)return;
    C<Button>("InstallUpdate").Visibility=Visibility.Visible;
    C<TextBlock>("UpdateText").Text="גרסה "+result.Version+(result.Automatic?" תותקן אוטומטית בהפעלה הבאה. אפשר להתקין גם כעת.":" מוכנה להתקנה.");
   }catch(OperationCanceledException){}catch{}finally{updateChecking=false;}
  }
  void InstallUpdate(bool ask=true){var source=updateChannel?.UpdatePath!=null?updateChannel:channel;if(busy||source==null||source.UpdatePath==null)return;if(ask&&MessageBox.Show(Window,"להתקין את העדכון ולהפעיל מחדש?","FormatFlow",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;SavePreferences();string target=Process.GetCurrentProcess().MainModule.FileName;Process.Start(new ProcessStartInfo(source.UpdatePath,"--apply-update "+ProcessRunner.Quote(target)+" "+Process.GetCurrentProcess().Id+" "+ProcessRunner.Quote(source.UpdateHash)){UseShellExecute=false,CreateNoWindow=true});Window.Close();}
 }
}

