using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FormatFlow;
class SyncProgress<T>:IProgress<T>{readonly Action<T> handler;public SyncProgress(Action<T> handler){this.handler=handler;}public void Report(T value){handler(value);}}
class Program {
 static readonly List<object> results=new List<object>();
 static void Check(string name,bool value){results.Add(new{name,passed=value});Console.WriteLine(name+": "+value);if(!value)throw new Exception(name);}
 static async Task Main(string[] args){
  if(args.Contains("--channel-live")){await ChannelTests.Run();return;}
  if(args.Contains("--banners")){await BannerTests.Run();return;}
  if(args.Contains("--saving")){await SavingTests.Run(Directory.GetCurrentDirectory());return;}
  if(args.Contains("--inspect")){UiDataTests.Inspect(Directory.GetCurrentDirectory());return;}
  if(args.Contains("--ui-data")){UiDataTests.Run(Directory.GetCurrentDirectory());return;}
  string workspace=args.Length>0?Path.GetFullPath(args[0]):Directory.GetCurrentDirectory(),root=Path.Combine(workspace,"NativeValidation");Directory.CreateDirectory(root);
  var profiles=Json.Read<List<FormatProfile>>(File.ReadAllText(Path.Combine(workspace,"FormatFlowNative/Resources/formats.json")));var converter=new FormatFlow.Converter(Environment.GetEnvironmentVariable("FORMATFLOW_TEST_ENGINE")??"ffmpeg");
  string source=Path.Combine(workspace,"NativeTests/Fixtures/sample.mp4"),image=Path.Combine(workspace,"NativeTests/Fixtures/picture.png"),csv=Path.Combine(root,"table.csv");
  File.WriteAllText(csv,"Name,Value\r\nAlpha,123\r\n\"שלום, עולם\",456\r\n");
  foreach(var profile in profiles){string input=profile.group=="Image"?image:profile.group=="Table"?csv:source;string output=await converter.ConvertAsync(profile,input,Path.Combine(root,profile.ext),false,null,CancellationToken.None);Check("format-"+profile.ext,File.Exists(output)&&new FileInfo(output).Length>0);}
  string json=Path.Combine(root,"one.json");File.WriteAllText(json,"[{\"Name\":\"שלום\",\"Value\":123}]");string round=await converter.ConvertAsync(profiles.First(p=>p.ext=="csv"),json,Path.Combine(root,"json-roundtrip"),false,null,CancellationToken.None);Check("single-row-json",File.ReadAllText(round).Contains("שלום"));
  string nested=Path.Combine(root,"nested.json");File.WriteAllText(nested,"[{\"Name\":{\"inner\":123}}]");bool rejected=false;try{await converter.ConvertAsync(profiles.First(p=>p.ext=="csv"),nested,Path.Combine(root,"nested"),false,null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}Check("nested-json-rejected",rejected);
  var table=Table.Parse("Name,Value\r\n\"Multi\r\nline\",\"a\"\"b\"\r\n",',');Check("quoted-csv-newline-escape",table.Count==2&&table[1][0]=="Multi\r\nline"&&table[1][1]=="a\"b");
  var cards=new[]{new FileCard{Path="photo.jpg"}};Check("format-selection-images",FormatRules.Options(profiles,cards).Count==13);Check("format-selection-empty",FormatRules.Options(profiles,Array.Empty<FileCard>()).Count==41);Check("format-selection-mixed",FormatRules.Options(profiles,new[]{cards[0],new FileCard{Path="sound.mp3"}}).Single().Extension=="zip");
  string first=await converter.ConvertAsync(profiles[0],source,Path.Combine(root,"duplicates"),false,null,CancellationToken.None),second=await converter.ConvertAsync(profiles[0],source,Path.Combine(root,"duplicates"),false,null,CancellationToken.None);Check("unique-output",first!=second&&File.Exists(first));
  string export=Path.Combine(root,"all.zip");await Archive.CreateAsync(new[]{first,second},export,null,CancellationToken.None);using(var zip=ZipFile.OpenRead(export))Check("export-all",zip.Entries.Count==2);
  await Archive.CreateAsync(new[]{first,second},export,null,CancellationToken.None);using(var zip=ZipFile.OpenRead(export))Check("export-overwrite",zip.Entries.Count==2);
  string before=Storage.Hash(export);using(var stop=new CancellationTokenSource()){stop.Cancel();try{await Archive.CreateAsync(new[]{first},export,null,stop.Token);}catch(OperationCanceledException){}}Check("cancel-export-preserves-original",before==Storage.Hash(export));
  string thumb=Path.Combine(root,"preview.png");await converter.ThumbnailAsync(source,thumb,CancellationToken.None);Check("video-thumbnail",File.Exists(thumb));
  double duration=await converter.DurationAsync(source,CancellationToken.None);Check("media-duration",duration>1&&duration<2);
  string longer=Path.Combine(workspace,"NativeTests/Fixtures/long-video.mp4");bool live=false;var progress=new SyncProgress<ConversionProgress>(p=>{if(p.Percent>0&&p.Percent<100)live=true;});await converter.ConvertAsync(profiles.First(p=>p.ext=="webm"),longer,Path.Combine(root,"progress"),false,progress,CancellationToken.None);Check("live-progress",live);
  using(var stop=new CancellationTokenSource()){stop.CancelAfter(500);bool cancelled=false;try{await converter.ConvertAsync(profiles.First(p=>p.ext=="webm"),longer,Path.Combine(root,"cancel"),false,null,stop.Token);}catch(OperationCanceledException){cancelled=true;}Check("cancel-active",cancelled&&Directory.GetFiles(Path.Combine(root,"cancel")).Length==0);}
  File.WriteAllText(Path.Combine(root,"results.json"),Json.Write(results));Console.WriteLine("Passed "+results.Count+" native-core checks.");
 }
}
