using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace FormatFlow {
 public sealed class Availability {public bool enabled {get;set;}=true;public string message {get;set;}="";}
 public sealed class Promotion {public string title {get;set;}public string text {get;set;}public string image {get;set;}public string link {get;set;}public string placement {get;set;}}
 public sealed class Appearance {public string title {get;set;}public string subtitle {get;set;}public string accent {get;set;}}
 public sealed class ReleaseInfo {public string version {get;set;}public string url {get;set;}public string sha256 {get;set;}public string installMode {get;set;}="ask";}
 public sealed class ChannelFeed {public int schema {get;set;}public int revision {get;set;}public Availability policy {get;set;}public List<Promotion> ads {get;set;}public Appearance appearance {get;set;}public ReleaseInfo latest {get;set;}}
 public static class ChannelRules {
  public static readonly Version CurrentVersion=typeof(ChannelRules).Assembly.GetName().Version;
  public static Version NormalizeVersion(Version version){return new Version(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));}
  public const string ReleasePrefix="https://github.com/yosef-chadad/formatflow-control/releases/download/";
  static string Limit(string value,int size){value=value??"";return value.Substring(0,Math.Min(size,value.Length));}
  public static ChannelFeed Normalize(ChannelFeed feed){
   if(feed==null||feed.schema!=1)throw new InvalidDataException("Unknown publisher channel schema.");
   feed.policy=feed.policy??new Availability();feed.policy.message=Limit(feed.policy.message,500);
   // Two slots keep the conversion queue next to the file picker.
   var ads=new List<Promotion>();var slots=new HashSet<string>();
   foreach(var ad in (feed.ads??new List<Promotion>()).Take(12).OrderBy(ad=>ad!=null&&ad.placement=="bottom"?1:0)){
    if(ad==null)continue;ad.title=Limit(ad.title,100);ad.text=Limit(ad.text,700);
    if(!Uri.TryCreate(ad.link,UriKind.Absolute,out var link)||link.Scheme!="https"||!string.IsNullOrEmpty(link.UserInfo))ad.link="";
    if(!BannerImages.IsAllowedUrl(ad.image))ad.image="";
    // Legacy bottom ads share the lower slot; explicit belowSearch wins.
    if(ad.placement!="top"&&ad.placement!="belowSearch")ad.placement="belowSearch";
    if((ad.title.Length>0||ad.text.Length>0||ad.image.Length>0)&&slots.Add(ad.placement))ads.Add(ad);if(ads.Count==2)break;
   }
   feed.ads=ads;feed.appearance=feed.appearance??new Appearance();feed.appearance.title=Limit(feed.appearance.title,70);feed.appearance.subtitle=Limit(feed.appearance.subtitle,100);
   if(!Regex.IsMatch(feed.appearance.accent??"","^#[0-9a-fA-F]{6}$"))feed.appearance.accent="";
   return feed;
  }
  public static bool IsNewRelease(ReleaseInfo release){return release!=null&&Version.TryParse(release.version,out var version)&&NormalizeVersion(version)>NormalizeVersion(CurrentVersion)&&Regex.IsMatch(release.sha256??"","^[0-9a-fA-F]{64}$")&&Uri.TryCreate(release.url,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&string.IsNullOrEmpty(uri.UserInfo)&&uri.IsDefaultPort&&uri.AbsoluteUri.StartsWith(ReleasePrefix,StringComparison.Ordinal);}
  public static bool Automatic(ReleaseInfo release){return release!=null&&release.installMode=="onNextLaunch";}
  public static void ValidateUrl(Uri uri,bool asset){
   bool host=uri.Host=="github.com"||uri.Host=="raw.githubusercontent.com"||uri.Host.EndsWith(".github.io",StringComparison.OrdinalIgnoreCase)||(asset&&(uri.Host=="release-assets.githubusercontent.com"||uri.Host=="objects.githubusercontent.com"));
   if(uri.Scheme!="https"||!uri.IsDefaultPort||!string.IsNullOrEmpty(uri.UserInfo)||!host)throw new InvalidDataException("Unsupported publisher URL.");
  }
 }
}
