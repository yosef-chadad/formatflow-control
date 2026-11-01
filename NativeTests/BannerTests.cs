using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FormatFlow;
static class BannerTests {
 sealed class Handler : HttpMessageHandler {
  public int Calls;public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return Reply(request,token);}
 }
 public static async Task Run(){
  var results=new List<object>();Action<string,bool> check=(name,ok)=>{results.Add(new{name,passed=ok});Console.WriteLine(name+": "+ok);if(!ok)throw new Exception(name);};
  var feed=ChannelRules.Normalize(Json.Read<ChannelFeed>("{\"schema\":1,\"ads\":[{\"image\":\"https://example.com/banner.png\",\"link\":\"https://advertiser.example/offer\",\"placement\":\"belowSearch\"}]}"));
  check("image-only-ad-retained",feed.ads.Count==1&&feed.ads[0].image=="https://example.com/banner.png");
  var legacy=ChannelRules.Normalize(new ChannelFeed{schema=1,ads=new List<Promotion>{new Promotion{title="old",placement="bottom"},new Promotion{title="top",placement="top"},new Promotion{title="preferred",placement="belowSearch"}}});
  check("only-two-ads-with-legacy-config",legacy.ads.Count==2&&legacy.ads.TrueForAll(a=>a.placement!="bottom"));
  check("explicit-lower-over-legacy-bottom",legacy.ads.Find(a=>a.placement=="belowSearch").title=="preferred");
  var fallback=ChannelRules.Normalize(new ChannelFeed{schema=1,ads=new List<Promotion>{new Promotion{title="legacy lower",placement="bottom"}}});
  check("legacy-bottom-migrates-to-lower",fallback.ads.Count==1&&fallback.ads[0].placement=="belowSearch");
  check("image-link-roundtrip",ChannelRules.Normalize(Json.Read<ChannelFeed>(Json.Write(feed))).ads[0].link=="https://advertiser.example/offer");
  foreach(var url in new[]{"file:///c:/secret.png","data:image/png;base64,abc","http://example.com/a.png","https://user:password@example.com/a.png","https://example.com:444/a.png","https://127.0.0.1/a","https://192.168.1.1/a","https://[::1]/a","https://[fd00::1]/a"})check("unsafe-image-url-"+results.Count,!BannerImages.IsAllowedUrl(url));
  check("https-url-query-accepted",BannerImages.IsAllowedUrl("https://yosef-chadad.github.io/formatflow-control/banner.png?v=2"));
  var invalid=ChannelRules.Normalize(new ChannelFeed{schema=1,ads=new List<Promotion>{new Promotion{image="javascript:alert(1)"},new Promotion{title="Text fallback",image="http://bad.example",placement="top"}}});
  check("unsafe-image-removed-text-preserved",invalid.ads.Count==1&&invalid.ads[0].image==""&&invalid.ads[0].title=="Text fallback");
  byte[] png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK1sAAAAASUVORK5CYII=");
  check("png-dimensions",BannerImages.TryDimensions(png,out int w,out int h)&&w==1&&h==1);
  byte[] jpeg=new byte[24];jpeg[0]=255;jpeg[1]=216;jpeg[2]=255;jpeg[3]=192;jpeg[4]=0;jpeg[5]=8;jpeg[6]=8;jpeg[7]=1;jpeg[8]=64;jpeg[9]=6;jpeg[10]=64;
  check("jpeg-dimensions",BannerImages.TryDimensions(jpeg,out w,out h)&&w==1600&&h==320);
  var huge=(byte[])png.Clone();huge[16]=127;check("huge-image-dimensions-rejected",!BannerImages.TryDimensions(huge,out _,out _));
  check("html-rejected",!BannerImages.TryDimensions(System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),out _,out _));
  check("svg-rejected",!BannerImages.TryDimensions(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>"),out _,out _));
  check("truncated-jpeg-rejected",!BannerImages.TryDimensions(new byte[]{255,216,255,192},out _,out _));
  string folder=Path.Combine(Path.GetTempPath(),"formatflow-banner-tests-"+Guid.NewGuid().ToString("N"));
  try{
   var handler=new Handler{Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(png)})};
   using(var images=new BannerImages(folder,handler)){
    var bytes=await images.LoadAsync("https://example.com/banner.png",CancellationToken.None);check("valid-banner-downloaded",bytes.Length==png.Length&&handler.Calls==1);
    handler.Reply=(r,t)=>throw new HttpRequestException("offline");bytes=await images.LoadAsync("https://example.com/banner.png",CancellationToken.None);check("offline-cache-reused",bytes.Length==png.Length&&handler.Calls==1);
    bool changed=false;try{await images.LoadAsync("https://example.com/banner.png?v=2",CancellationToken.None);}catch(HttpRequestException){changed=true;}check("new-image-url-bypasses-old-cache",changed&&handler.Calls==2);
    handler.Reply=(r,t)=>{var reply=new HttpResponseMessage(HttpStatusCode.Redirect);reply.Headers.Location=new Uri("http://example.com/insecure");return Task.FromResult(reply);};
    bool rejected=false;try{await images.LoadAsync("https://example.com/redirect",CancellationToken.None);}catch(InvalidDataException){rejected=true;}check("redirect-to-http-rejected",rejected&&handler.Calls==3);
    handler.Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<html>This is not an image at all.</html>")});
    rejected=false;try{await images.LoadAsync("https://example.com/not-an-image",CancellationToken.None);}catch(InvalidDataException){rejected=true;}check("downloaded-html-rejected",rejected);
    handler.Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[BannerImages.MaxBytes+1])});
    rejected=false;try{await images.LoadAsync("https://example.com/oversize",CancellationToken.None);}catch(InvalidDataException){rejected=true;}check("download-size-limit",rejected);
    check("failed-downloads-not-cached",Directory.GetFiles(folder).Length==1);
    using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool cancelled=false;try{await images.LoadAsync("https://example.com/banner.png",cancel.Token);}catch(OperationCanceledException){cancelled=true;}check("cancelled-load-stops",cancelled);}
    var cachedPath=Directory.GetFiles(folder)[0];File.SetLastWriteTimeUtc(cachedPath,DateTime.UtcNow.AddMinutes(-11));int before=handler.Calls;
    handler.Reply=(r,t)=>throw new HttpRequestException("offline");
    var stale=await images.LoadAsync("https://example.com/banner.png",CancellationToken.None);check("stale-cache-available-offline",stale.Length==png.Length&&handler.Calls==before+1);
    handler.Reply=(r,t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(png)});
    await images.LoadAsync("https://example.com/banner.png",CancellationToken.None);check("same-url-image-refreshed",handler.Calls==before+2&&DateTime.UtcNow-File.GetLastWriteTimeUtc(cachedPath)<TimeSpan.FromMinutes(1));
    await images.LoadAsync("https://example.com/banner.png",CancellationToken.None);check("refreshed-cache-not-redownloaded",handler.Calls==before+2);
   }
  }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
  File.WriteAllText("NativeTests/banner-results.json",Json.Write(results));Console.WriteLine("Passed "+results.Count+" banner checks.");
 }
}
