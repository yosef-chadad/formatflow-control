using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace FormatFlow {
 public sealed class PublisherConfig {public string feed {get;set;}="";public string fallbackFeed {get;set;}="";}
 public sealed class ChannelResult {public ChannelFeed State;public Availability Policy=>State.policy;public List<Promotion> Ads=>State.ads;public Appearance Appearance=>State.appearance;public string UpdatePath;public string Version;public bool Automatic;}
 public sealed class ChannelService {
  static readonly PublisherConfig Config=Json.Read<PublisherConfig>(Embedded.Text("publisher.json"));
  public static string Feed=>Config.feed;
  public string UpdatePath {get;private set;}public string UpdateHash {get;private set;}public bool Automatic {get;private set;}
  public static readonly string CachePath=Path.Combine(Storage.Root,"channel-cache.json");
  bool checking;
  static bool preferWindowsFeed;
  public string CacheError {get;private set;}
  public static ChannelFeed ReadCache(){try{return ChannelRules.Normalize(Json.Read<ChannelFeed>(File.ReadAllText(CachePath)));}catch{return null;}}
  public async Task<ChannelResult> CheckAsync(CancellationToken token,bool downloadUpdate=true,bool persistFeed=true){
   if(checking||string.IsNullOrWhiteSpace(Feed))return null;checking=true;
   try{
    string folder=Path.Combine(Storage.Root,"updates");Directory.CreateDirectory(folder);string temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".json");ChannelFeed feed;
    try{
     try{await DownloadAsync(Feed,temp,262144,false,token).ConfigureAwait(false);}catch{token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(Config.fallbackFeed))throw;await DownloadAsync(Config.fallbackFeed,temp,262144,false,token).ConfigureAwait(false);}
     feed=ChannelRules.Normalize(Json.Read<ChannelFeed>(File.ReadAllText(temp)));
    }finally{if(File.Exists(temp))File.Delete(temp);}
    CacheError=null;
    if(persistFeed){try{Storage.Atomic(CachePath,Json.Write(feed));Storage.Atomic(Path.Combine(Storage.Root,"policy.json"),Json.Write(feed.policy));}catch(Exception ex){CacheError=ex.Message;}}
    var result=new ChannelResult{State=feed};UpdatePath=null;UpdateHash=null;Automatic=false;
    try{
     if(feed.policy.enabled&&ChannelRules.IsNewRelease(feed.latest)){
      var version=Version.Parse(feed.latest.version);string path=Path.Combine(folder,"FormatFlow-"+version+"-"+feed.latest.sha256.Substring(0,12)+".exe");
      bool verified=File.Exists(path)&&string.Equals(Storage.Hash(path),feed.latest.sha256,StringComparison.OrdinalIgnoreCase);
      if(!verified&&downloadUpdate){
       string partial=path+".download";try{await DownloadAsync(feed.latest.url,partial,314572800,true,token).ConfigureAwait(false);if(!string.Equals(Storage.Hash(partial),feed.latest.sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Update checksum mismatch.");using(var f=File.OpenRead(partial)){if(f.ReadByte()!=77||f.ReadByte()!=90)throw new InvalidDataException("Update is not a Windows executable.");}if(File.Exists(path))File.Delete(path);File.Move(partial,path);verified=true;}finally{if(File.Exists(partial))File.Delete(partial);}
      }
      if(verified){if(!Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion,out var actual)||ChannelRules.NormalizeVersion(actual)!=ChannelRules.NormalizeVersion(version))throw new InvalidDataException("Update file version does not match the feed.");UpdatePath=path;UpdateHash=feed.latest.sha256;Automatic=ChannelRules.Automatic(feed.latest);result.UpdatePath=path;result.Version=version.ToString();result.Automatic=Automatic;}
     }
    }catch(OperationCanceledException){token.ThrowIfCancellationRequested();}catch{/* Availability and notices remain usable if an update download fails. */}
    return result;
   }finally{checking=false;}
  }
  static async Task DownloadAsync(string address,string output,long limit,bool asset,CancellationToken token){
#if NETFRAMEWORK
   if(!asset){
    if(preferWindowsFeed){try{await WindowsFeedAsync(address,output,limit,token).ConfigureAwait(false);return;}catch{token.ThrowIfCancellationRequested();}}
    try{await DownloadNativeAsync(address,output,limit,false,token).ConfigureAwait(false);return;}
    catch{token.ThrowIfCancellationRequested();await WindowsFeedAsync(address,output,limit,token).ConfigureAwait(false);preferWindowsFeed=true;return;}
   }
#endif
   await DownloadNativeAsync(address,output,limit,asset,token).ConfigureAwait(false);
  }
#if NETFRAMEWORK
  static async Task WindowsFeedAsync(string address,string output,long limit,CancellationToken token){
   ChannelRules.ValidateUrl(new Uri(address),false);
   string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
   string script="$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 20 -Uri '"+address.Replace("'","''")+"' -OutFile '"+output.Replace("'","''")+"'";
   using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
    timeout.CancelAfter(25000);
    var result=await ProcessRunner.RunAsync(shell,new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))},timeout.Token).ConfigureAwait(false);
    if(result.ExitCode!=0)throw new IOException("Windows publisher reader failed: "+result.Error);
    if(!File.Exists(output)||new FileInfo(output).Length>limit)throw new InvalidDataException("Publisher feed exceeds its limit.");
   }
  }
#endif
  static async Task DownloadNativeAsync(string address,string output,long limit,bool asset,CancellationToken token){
#if NETFRAMEWORK
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
#endif
   using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
    timeout.CancelAfter(asset?180000:15000);var uri=new Uri(address);
    for(int redirect=0;redirect<7;redirect++){
     ChannelRules.ValidateUrl(uri,asset);
     var request=(HttpWebRequest)WebRequest.Create(uri);request.AllowAutoRedirect=false;request.UserAgent="FormatFlow/"+ChannelRules.CurrentVersion.ToString(3);request.Headers[HttpRequestHeader.CacheControl]="no-cache";
     request.Timeout=asset?180000:15000;request.ReadWriteTimeout=request.Timeout;request.KeepAlive=false;
     request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
     request.ServicePoint.ConnectionLimit=Math.Max(request.ServicePoint.ConnectionLimit,8);
     using(timeout.Token.Register(()=>request.Abort())){
      try{
       using(var response=(HttpWebResponse)await Task.Run(()=>request.GetResponse(),timeout.Token).ConfigureAwait(false)){
        int status=(int)response.StatusCode;
        if(status>=300&&status<400){string location=response.Headers[HttpResponseHeader.Location];if(string.IsNullOrEmpty(location))throw new InvalidDataException("Invalid redirect.");uri=new Uri(uri,location);continue;}
        if(response.ContentLength>limit)throw new InvalidDataException("Download exceeds its limit.");
        using(var input=response.GetResponseStream())using(var file=new FileStream(output,FileMode.Create,FileAccess.Write,FileShare.None,65536,true)){
         var buffer=new byte[65536];long total=0;int read;while((read=await input.ReadAsync(buffer,0,buffer.Length,timeout.Token).ConfigureAwait(false))>0){total+=read;if(total>limit)throw new InvalidDataException("Download exceeds its limit.");await file.WriteAsync(buffer,0,read,timeout.Token).ConfigureAwait(false);}
        }
        return;
       }
      }catch(WebException){timeout.Token.ThrowIfCancellationRequested();throw;}
     }
    }
    throw new InvalidDataException("Too many redirects.");
   }
  }

 }
}

