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
  public static ChannelFeed ReadCache(){try{return ChannelRules.Normalize(Json.Read<ChannelFeed>(File.ReadAllText(CachePath)));}catch{return null;}}
  public async Task<ChannelResult> CheckAsync(CancellationToken token,bool downloadUpdate=true){
   if(checking||string.IsNullOrWhiteSpace(Feed))return null;checking=true;
   try{
    string folder=Path.Combine(Storage.Root,"updates");Directory.CreateDirectory(folder);string temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".json");ChannelFeed feed;
    try{
     try{await DownloadAsync(Feed,temp,262144,false,token).ConfigureAwait(false);}catch{token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(Config.fallbackFeed))throw;await DownloadAsync(Config.fallbackFeed,temp,262144,false,token).ConfigureAwait(false);}
     feed=ChannelRules.Normalize(Json.Read<ChannelFeed>(File.ReadAllText(temp)));
    }finally{if(File.Exists(temp))File.Delete(temp);}
    Storage.Atomic(CachePath,Json.Write(feed));Storage.Atomic(Path.Combine(Storage.Root,"policy.json"),Json.Write(feed.policy));
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
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
#endif
   using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))using(var handler=new HttpClientHandler{AllowAutoRedirect=false})using(var client=new HttpClient(handler)){
    timeout.CancelAfter(asset?180000:5000);client.Timeout=Timeout.InfiniteTimeSpan;client.DefaultRequestHeaders.UserAgent.ParseAdd("FormatFlow/2.2.0");client.DefaultRequestHeaders.CacheControl=new System.Net.Http.Headers.CacheControlHeaderValue{NoCache=true};var uri=new Uri(address);
    for(int redirect=0;redirect<7;redirect++){
     ChannelRules.ValidateUrl(uri,asset);using(var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false)){
      if((int)response.StatusCode>=300&&(int)response.StatusCode<400){if(response.Headers.Location==null)throw new InvalidDataException("Invalid redirect.");uri=new Uri(uri,response.Headers.Location);continue;}
      response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("Download exceeds its limit.");
      using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var file=new FileStream(output,FileMode.Create,FileAccess.Write,FileShare.None,65536,true)){
       var buffer=new byte[65536];long total=0;int read;while((read=await input.ReadAsync(buffer,0,buffer.Length,timeout.Token).ConfigureAwait(false))>0){total+=read;if(total>limit)throw new InvalidDataException("Download exceeds its limit.");await file.WriteAsync(buffer,0,read,timeout.Token).ConfigureAwait(false);}
      }
      return;
     }
    }
    throw new InvalidDataException("Too many redirects.");
   }
  }
 }
}
