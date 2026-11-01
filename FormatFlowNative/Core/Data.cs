using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif
namespace FormatFlow {
 public static class Json {
  public static T Read<T>(string text) {
#if NETFRAMEWORK
   return new JavaScriptSerializer { MaxJsonLength=16*1024*1024,RecursionLimit=64 }.Deserialize<T>(text);
#else
   return JsonSerializer.Deserialize<T>(text,new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
#endif
  }
  public static string Write(object value) {
#if NETFRAMEWORK
   return new JavaScriptSerializer { MaxJsonLength=16*1024*1024 }.Serialize(value);
#else
   return JsonSerializer.Serialize(value);
#endif
  }
 }
 public static class Storage {
  public static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FormatFlow");
  public static void Atomic(string path,string text) { Directory.CreateDirectory(Path.GetDirectoryName(path));string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(tmp,text,new System.Text.UTF8Encoding(false));try {if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}finally{if(File.Exists(tmp))File.Delete(tmp);} }
  public static T Load<T>(string path,Func<T> fallback) where T:class {try{return Json.Read<T>(File.ReadAllText(path)) ?? fallback();}catch{return fallback();}}
  public static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
 }
 public sealed class Preferences {
  public int Theme {get;set;} public double Brightness {get;set;}=1;
  public string Output {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"FormatFlow Output");
  public bool SoundOnComplete {get;set;}=true; public bool RememberFormat {get;set;}=true; public bool Animations {get;set;}=true; public string LastFormat {get;set;}="mp4";
  public bool OpenAfter {get;set;} public bool KeepMetadata {get;set;} public double Width {get;set;} public double Height {get;set;}
 }
 public sealed class FormatProfile {public string ext {get;set;} public string group {get;set;} public string codec {get;set;} public string[] args {get;set;} public string[] inputs {get;set;} }
 public sealed class FormatOption {public string Label {get;set;} public string Extension {get;set;} public override string ToString(){return Label;}}
 public sealed class TextSection {public string Title {get;set;}public string Body {get;set;}}
 public sealed class FormatTile {public string Extension {get;set;}public string ExtensionUpper {get;set;}public string Category {get;set;}public bool IsAvailable {get;set;}public bool IsChosen {get;set;}public string Hint {get;set;}}
 public static class FormatPicker {
  public static List<FormatTile> Tiles(IEnumerable<FormatProfile> profiles,IEnumerable<FileCard> cards,string selected,string category,string query){
   var available=new HashSet<string>(FormatRules.Options(profiles,cards).Select(p=>p.Extension),StringComparer.OrdinalIgnoreCase);
   var labels=new Dictionary<string,string>{{"Video","וידאו"},{"Audio","שמע"},{"Image","תמונה"},{"Table","טבלה"},{"Archive","ארכיון"}};
   query=(query??"").Trim();
   return profiles.Where(p=>category=="all"||(category=="Data"&&(p.group=="Table"||p.group=="Archive"))||p.group==category)
    .Where(p=>query.Length==0||p.ext.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0||labels[p.group].IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)
    .Select(p=>new FormatTile{Extension=p.ext,ExtensionUpper=p.ext.ToUpperInvariant(),Category=labels[p.group],IsAvailable=available.Contains(p.ext),IsChosen=p.ext==selected,Hint=available.Contains(p.ext)?("המרה ל־"+p.ext.ToUpperInvariant()):"הפורמט אינו מתאים לכל הקבצים המסומנים"}).ToList();
  }
 }
 public sealed class FileCard : INotifyPropertyChanged {
  public event PropertyChangedEventHandler PropertyChanged;
  void Changed(string name){PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));}
  public string Id {get;set;}=Guid.NewGuid().ToString("N");public string Name {get;set;}public string Path {get;set;}public string Detail {get;set;}public string Icon {get;set;}
  bool selected=true,editable=true,indeterminate;double progress;string status="מוכן להמרה",output="";object thumbnail;
  public bool IsSelected{get=>selected;set{if(selected!=value){selected=value;Changed(nameof(IsSelected));}}}
  public bool IsEditable{get=>editable;set{editable=value;Changed(nameof(IsEditable));}}
  public bool IsIndeterminate{get=>indeterminate;set{indeterminate=value;Changed(nameof(IsIndeterminate));}}
  public double Progress{get=>progress;set{progress=value;Changed(nameof(Progress));}}
  public string Status{get=>status;set{status=value;Changed(nameof(Status));}}
  public string Output{get=>output;set{output=value;Changed(nameof(Output));Changed(nameof(HasOutput));}}
  public bool HasOutput=>!string.IsNullOrEmpty(output);
  public object Thumbnail{get=>thumbnail;set{thumbnail=value;Changed(nameof(Thumbnail));}}
 }
 public sealed class ConversionProgress {public double Percent {get;set;}public bool Indeterminate {get;set;} public string Message {get;set;} }
 public static class FormatRules {
  public static bool Accepts(FormatProfile profile,string source){var ext=Path.GetExtension(source).TrimStart('.').ToLowerInvariant();return profile.inputs.Contains("*")||profile.inputs.Contains(ext);}
  public static List<FormatOption> Options(IEnumerable<FormatProfile> profiles,IEnumerable<FileCard> cards){
   var paths=cards.Where(c=>c.IsSelected).Select(c=>c.Path).ToList();var labels=new Dictionary<string,string>{{"Video","וידאו"},{"Audio","שמע"},{"Image","תמונה"},{"Table","טבלה"},{"Archive","ארכיון"}};
   return profiles.Where(p=>paths.All(s=>Accepts(p,s))).Select(p=>new FormatOption{Extension=p.ext,Label=p.ext.ToUpperInvariant()+" · "+labels[p.group]}).ToList();
  }
 }
}
