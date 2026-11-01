using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using FormatFlow;
static class UiDataTests {
 public static void Run(string workspace){
  var profiles=Json.Read<List<FormatProfile>>(File.ReadAllText(Path.Combine(workspace,"FormatFlowNative/Resources/formats.json")));
  var empty=Array.Empty<FileCard>();var checks=new List<object>();
  Action<string,bool> check=(name,ok)=>{checks.Add(new{name,passed=ok});Console.WriteLine(name+": "+ok);if(!ok)throw new Exception(name);};
  var all=FormatPicker.Tiles(profiles,empty,"mp4","all","");check("picker-all-41",all.Count==41&&all.All(p=>p.IsAvailable));check("picker-selected-once",all.Count(t=>t.IsChosen)==1&&all.Single(t=>t.IsChosen).Extension=="mp4");
  foreach(var group in new[]{"Video","Audio","Image"})check("picker-category-"+group,FormatPicker.Tiles(profiles,empty,"mp4",group,"").Count==12);
  check("picker-data-category",FormatPicker.Tiles(profiles,empty,"mp4","Data","").Count==5);
  check("picker-case-and-whitespace-search",FormatPicker.Tiles(profiles,empty,"mp4","all","  PNG  ").Single().Extension=="png");
  check("picker-Hebrew-search",FormatPicker.Tiles(profiles,empty,"mp4","all","שמע").Count==12);
  check("picker-no-results",FormatPicker.Tiles(profiles,empty,"mp4","all","not-a-format").Count==0);
  var image=FormatPicker.Tiles(profiles,new[]{new FileCard{Path="photo.jpg"}},"png","all","");check("picker-incompatible-visible-disabled",image.Count==41&&image.Count(t=>t.IsAvailable)==13&&!image.Single(t=>t.Extension=="mp4").IsAvailable);
  var mixed=FormatPicker.Tiles(profiles,new[]{new FileCard{Path="photo.jpg"},new FileCard{Path="song.mp3"}},"zip","all","");check("picker-mixed-only-zip",mixed.Count(t=>t.IsAvailable)==1&&mixed.Single(t=>t.IsAvailable).Extension=="zip");
  var legacy=Json.Read<Preferences>("{\"Theme\":1}");check("legacy-preference-new-defaults",legacy.Theme==1&&legacy.Animations&&legacy.SoundOnComplete&&legacy.RememberFormat);
  var prefs=new Preferences{Theme=1,Brightness=.6,Animations=false,SoundOnComplete=false,RememberFormat=false,LastFormat="png",OpenAfter=true};
  var saved=Json.Read<Preferences>(Json.Write(prefs));check("preference-round-trip",saved.Theme==1&&!saved.Animations&&!saved.SoundOnComplete&&saved.LastFormat=="png");
  var defaults=new Preferences();foreach(var prop in typeof(Preferences).GetProperties())prop.SetValue(prefs,prop.GetValue(defaults));check("restore-defaults",prefs.Theme==0&&prefs.Brightness==1&&prefs.Animations&&prefs.SoundOnComplete&&prefs.RememberFormat&&prefs.LastFormat=="mp4"&&!prefs.OpenAfter);
  var terms=Json.Read<List<TextSection>>(File.ReadAllText(Path.Combine(workspace,"FormatFlowNative/Resources/Terms.json")));check("terms-13-readable-sections",terms.Count==13&&terms.All(t=>!string.IsNullOrEmpty(t.Title)&&!string.IsNullOrEmpty(t.Body)));check("terms-open-source-exception",terms.Any(t=>t.Title.Contains("קוד פתוח")&&t.Body.Contains("FFmpeg"))&&terms.Any(t=>t.Body.Contains("הנדסה לאחור")&&t.Body.Contains("רישיונות קוד פתוח")));
  File.WriteAllText(Path.Combine(workspace,"NativeTests/ui-results.json"),Json.Write(checks));Console.WriteLine("Passed "+checks.Count+" UI-data checks.");
 }
 public static void Inspect(string workspace){
  string file=Path.Combine(workspace,"deliverables/FormatFlow.exe");var assembly=System.Reflection.Assembly.LoadFile(file);
  if(assembly.GetName().Version!=new Version(2,4,0,0))throw new Exception("Wrong application build version.");
  foreach(var name in new[]{"FormatFlow.Main.xaml","FormatFlow.Terms.json","FormatFlow.App.ico","FormatFlow.ffmpeg.gz","FormatFlow.Notices.txt","FormatFlow.Completion.wav"})if(!assembly.GetManifestResourceNames().Contains(name))throw new Exception("Missing resource: "+name);
  using(var reader=new StreamReader(assembly.GetManifestResourceStream("FormatFlow.publisher.json"))){var config=Json.Read<PublisherConfig>(reader.ReadToEnd());if(config.feed!="https://yosef-chadad.github.io/formatflow-control/feed.json"||config.fallbackFeed!="https://raw.githubusercontent.com/yosef-chadad/formatflow-control/main/feed.json")throw new Exception("Wrong publisher endpoint.");}
  string markup;using(var reader=new StreamReader(assembly.GetManifestResourceStream("FormatFlow.Main.xaml")))markup=reader.ReadToEnd();
  if(!markup.Contains("ImageSource=\"{Binding Picture}\"")||!markup.Contains("Text=\"פרסומת ↗\""))throw new Exception("Missing image banner template.");
  var doc=new System.Xml.XmlDocument();doc.LoadXml(markup);var ns=new System.Xml.XmlNamespaceManager(doc.NameTable);ns.AddNamespace("x","http://schemas.microsoft.com/winfx/2006/xaml");ns.AddNamespace("p","http://schemas.microsoft.com/winfx/2006/xaml/presentation");
  var names=new HashSet<string>();foreach(System.Xml.XmlElement element in doc.SelectNodes("//*[@x:Name or @Name]",ns)){if(element.SelectSingleNode("ancestor::p:ControlTemplate | ancestor::p:DataTemplate",ns)!=null)continue;string name=element.GetAttribute("Name","http://schemas.microsoft.com/winfx/2006/xaml");if(name.Length==0)name=element.GetAttribute("Name");if(!names.Add(name))throw new Exception("Duplicate namescope entry: "+name);}
  foreach(string name in new[]{"SplashOverlay","FormatTiles","ResetSettings","TermsButton","SoundOnComplete","Animations","RememberFormat","OpenResults","BlockedOverlay","WelcomeSubtitle"})if(!names.Contains(name))throw new Exception("Missing interface: "+name);
  var hero=doc.SelectSingleNode("//*[@x:Name='Hero']",ns);
  if(hero.FirstChild.Attributes["Name","http://schemas.microsoft.com/winfx/2006/xaml"].Value!="AdTop")throw new Exception("Top advertisement must immediately precede the greeting.");
  var panel=doc.SelectSingleNode("//*[@x:Name='ContentPanel']",ns);var children=panel.ChildNodes.Cast<System.Xml.XmlNode>().ToList();
  if(children.FindIndex(n=>n.Attributes?["Name","http://schemas.microsoft.com/winfx/2006/xaml"]?.Value=="AdSearch")<=children.FindIndex(n=>n.Attributes?["Name","http://schemas.microsoft.com/winfx/2006/xaml"]?.Value=="QueueSection"))throw new Exception("Lower advertisement must follow file cards.");
  if(names.Contains("AdBottom"))throw new Exception("Only two advertisement positions are supported.");
  using(var compressed=assembly.GetManifestResourceStream("FormatFlow.ffmpeg.gz"))using(var gzip=new GZipStream(compressed,CompressionMode.Decompress))using(var sha=System.Security.Cryptography.SHA256.Create()){string hash=BitConverter.ToString(sha.ComputeHash(gzip)).Replace("-","").ToLowerInvariant();if(hash!="2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3")throw new Exception("Embedded engine mismatch.");}
  if(assembly.GetReferencedAssemblies().Any(a=>a.Name.Contains("Automation")))throw new Exception("Unexpected shell dependency.");
  Console.WriteLine("Executable inspection passed: embedded UI, terms, icon, engine hash, namescope and framework references.");
 }

}
