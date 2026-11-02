using System;
using System.IO;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FormatFlow {
 public sealed class BannerPresentation : INotifyPropertyChanged,IDisposable {
  readonly Promotion ad;readonly CancellationTokenSource stop;bool loading;ImageSource picture;DateTime nextCheck;
  public BannerPresentation(Promotion source,CancellationToken token){ad=source;stop=CancellationTokenSource.CreateLinkedTokenSource(token);}
  public string Title=>string.IsNullOrWhiteSpace(ad.title)?"פרסומת":ad.title;
  public string Text=>ad.text;
  public string Link=>ad.link;
  public double BannerHeight=>ad.placement=="top"?90:144;
  public double MinimumHeight=>ad.placement=="top"?68:94;
  public bool HasLink=>!string.IsNullOrWhiteSpace(Link);
  public ImageSource Picture=>picture;
  public bool ShowFallback=>picture==null;
  public bool Matches(Promotion other)=>ad.image==other.image&&ad.link==other.link&&ad.title==other.title&&ad.text==other.text;
  public event PropertyChangedEventHandler PropertyChanged;
  public async Task LoadAsync(BannerImages images){
   if(loading||DateTime.UtcNow<nextCheck||string.IsNullOrEmpty(ad.image)||stop.IsCancellationRequested)return;loading=true;
   try{
    var bytes=await images.LoadAsync(ad.image,stop.Token);if(bytes==null||stop.IsCancellationRequested)return;
    if(!BannerImages.TryDimensions(bytes,out int width,out _))return;
    using(var stream=new MemoryStream(bytes)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=Math.Min(1600,width);bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();picture=bitmap;}
    nextCheck=DateTime.UtcNow.AddSeconds(10);PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Picture)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(ShowFallback)));
   }catch(OperationCanceledException){}catch{/* A broken or offline ad must never interrupt conversion. */}finally{loading=false;}
  }
  public void Dispose(){stop.Cancel();stop.Dispose();}
 }
}
