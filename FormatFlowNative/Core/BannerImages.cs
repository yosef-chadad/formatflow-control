using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace FormatFlow {
 // Only raster bytes reach the WPF decoder. No HTML, scripts or remote XAML.
 public sealed class BannerImages : IDisposable {
  public const int MaxBytes=6*1024*1024;
  readonly HttpClient client;readonly string folder;
  public BannerImages(string cacheFolder,HttpMessageHandler handler=null){
   folder=cacheFolder;client=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false,UseDefaultCredentials=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate});client.Timeout=Timeout.InfiniteTimeSpan;
  }
  public static bool IsAllowedUrl(string value){
   if(string.IsNullOrEmpty(value)||value.Length>2048||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.IsLoopback)return false;
   if(IPAddress.TryParse(uri.Host.Trim('[',']'),out var ip)){
    if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();var b=ip.GetAddressBytes();
    if(b.Length==4&&(b[0]==0||b[0]==10||b[0]==127||b[0]>=224||b[0]==169&&b[1]==254||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168))return false;
    if(b.Length==16&&(ip.IsIPv6LinkLocal||ip.IsIPv6SiteLocal||ip.IsIPv6Multicast||(b[0]&254)==252))return false;
   }
   return true;
  }
  public static bool TryDimensions(byte[] bytes,out int width,out int height){
   width=height=0;if(bytes==null||bytes.Length<24||bytes.Length>MaxBytes)return false;
   if(bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&bytes[12]==73&&bytes[13]==72&&bytes[14]==68&&bytes[15]==82){
    long w=((long)bytes[16]<<24)|((long)bytes[17]<<16)|((long)bytes[18]<<8)|bytes[19],h=((long)bytes[20]<<24)|((long)bytes[21]<<16)|((long)bytes[22]<<8)|bytes[23];if(w>int.MaxValue||h>int.MaxValue)return false;width=(int)w;height=(int)h;
   }else if(bytes[0]==255&&bytes[1]==216){
    int p=2;while(p<bytes.Length){
     if(bytes[p++]!=255)return false;while(p<bytes.Length&&bytes[p]==255)p++;if(p>=bytes.Length)return false;int marker=bytes[p++];
     if(marker==217||marker==218)break;if(marker==1||marker>=208&&marker<=215)continue;
     if(p+2>bytes.Length)return false;int len=(bytes[p]<<8)|bytes[p+1];if(len<2||p+len>bytes.Length)return false;
     if(marker>=192&&marker<=207&&marker!=196&&marker!=200&&marker!=204){if(len<8)return false;height=(bytes[p+3]<<8)|bytes[p+4];width=(bytes[p+5]<<8)|bytes[p+6];break;}p+=len;
    }
   }
   return width>0&&height>0&&width<=8192&&height<=4096&&(long)width*height<=8000000;
  }
  string CachePath(string url){using(var hash=SHA256.Create())return Path.Combine(folder,BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","")+".img");}
  public async Task<byte[]> LoadAsync(string url,CancellationToken token){
   if(!IsAllowedUrl(url))return null;token.ThrowIfCancellationRequested();string path=CachePath(url);
   byte[] cached=null;
   try{if(File.Exists(path)&&new FileInfo(path).Length<=MaxBytes){cached=File.ReadAllBytes(path);if(!TryDimensions(cached,out _,out _)){cached=null;File.Delete(path);}else if(DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromSeconds(30))return cached;}}catch(IOException){}
   try{
   using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
    timeout.CancelAfter(TimeSpan.FromSeconds(10));Uri uri=new Uri(url);
    for(int redirects=0;redirects<5;redirects++){
     if(!IsAllowedUrl(uri.AbsoluteUri))throw new InvalidDataException("Unsupported banner URL.");
     using(var request=new HttpRequestMessage(HttpMethod.Get,uri)){
     request.Headers.CacheControl=new System.Net.Http.Headers.CacheControlHeaderValue{NoCache=true};
     using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false)){
      int status=(int)response.StatusCode;if(status>=300&&status<=399){if(response.Headers.Location==null)throw new InvalidDataException("Missing redirect URL.");uri=new Uri(uri,response.Headers.Location);continue;}
      response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>MaxBytes)throw new InvalidDataException("Banner is too large.");
      using(var source=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new MemoryStream()){
       var buffer=new byte[32768];int count;while((count=await source.ReadAsync(buffer,0,buffer.Length,timeout.Token).ConfigureAwait(false))>0){if(output.Length+count>MaxBytes)throw new InvalidDataException("Banner is too large.");output.Write(buffer,0,count);}
       var bytes=output.ToArray();if(!TryDimensions(bytes,out _,out _))throw new InvalidDataException("Use a PNG or JPEG banner within the image limits.");
       Directory.CreateDirectory(folder);string partial=path+"."+Guid.NewGuid().ToString("N")+".tmp";
       try{File.WriteAllBytes(partial,bytes);if(File.Exists(path))File.Replace(partial,path,null);else File.Move(partial,path);}finally{if(File.Exists(partial))File.Delete(partial);}
       foreach(var old in new DirectoryInfo(folder).GetFiles("*.img").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(24))try{old.Delete();}catch(IOException){}
       return bytes;
      }
     }
     }
    }
    throw new InvalidDataException("Too many banner redirects.");
   }
   }catch(OperationCanceledException){token.ThrowIfCancellationRequested();if(cached!=null)return cached;throw;}catch{if(cached!=null)return cached;throw;}
  }
  public void Dispose(){client.Dispose();}
 }
}
