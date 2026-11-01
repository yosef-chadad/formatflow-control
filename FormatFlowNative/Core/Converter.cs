using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace FormatFlow {
 public sealed class ProcessResult {public int ExitCode; public string Error;}
 public static class ProcessRunner {
  public static string Quote(string text){return "\""+Regex.Replace(Regex.Replace(text,@"(\\*)""","$1$1\\\""),@"(\\+)$","$1$1")+"\"";}
  public static async Task<ProcessResult> RunAsync(string executable,IEnumerable<string> arguments,CancellationToken token,Action<string> line=null){
   var start=new ProcessStartInfo(executable,string.Join(" ",arguments.Select(Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
   using(var process=new Process{StartInfo=start}){
    token.ThrowIfCancellationRequested();process.Start();
    var errors=process.StandardError.ReadToEndAsync();
    var output=Task.Run(async()=>{string value;while((value=await process.StandardOutput.ReadLineAsync().ConfigureAwait(false))!=null)line?.Invoke(value);});
    using(token.Register(()=>{try{if(!process.HasExited)process.Kill();}catch{}})){
     await Task.Run(()=>process.WaitForExit()).ConfigureAwait(false);await output.ConfigureAwait(false);var message=await errors.ConfigureAwait(false);token.ThrowIfCancellationRequested();return new ProcessResult{ExitCode=process.ExitCode,Error=message};
    }
   }
  }
 }
 public sealed class Converter {
  readonly string engine;
  public Converter(string enginePath){engine=enginePath;}
  public async Task<double> DurationAsync(string source,CancellationToken token){
   using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
    timeout.CancelAfter(8000);
    try{var result=await ProcessRunner.RunAsync(engine,new[]{"-hide_banner","-nostdin","-protocol_whitelist","file,pipe","-i",source},timeout.Token).ConfigureAwait(false);var m=Regex.Match(result.Error,@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");if(m.Success)return double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*3600+double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)*60+double.Parse(m.Groups[3].Value,CultureInfo.InvariantCulture);}catch(OperationCanceledException){token.ThrowIfCancellationRequested();}
   }
   return 0;
  }
  public async Task ThumbnailAsync(string source,string output,CancellationToken token){
   using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)){
    timeout.CancelAfter(12000);
    var result=await ProcessRunner.RunAsync(engine,new[]{"-hide_banner","-loglevel","error","-nostdin","-y","-protocol_whitelist","file,pipe","-i",source,"-map","0:v:0","-frames:v","1","-vf","scale=410:240:force_original_aspect_ratio=decrease","-threads","1",output},timeout.Token).ConfigureAwait(false);
    if(result.ExitCode!=0)throw new IOException("No preview available.");
   }
  }
  public async Task<string> ConvertAsync(FormatProfile profile,string source,string directory,bool keepMetadata,IProgress<ConversionProgress> progress,CancellationToken token){
   if(!FormatRules.Accepts(profile,source))throw new InvalidOperationException("הקובץ אינו תואם לפורמט שבחרת.");
   if(!File.Exists(source))throw new FileNotFoundException("קובץ המקור לא נמצא.",source);
   token.ThrowIfCancellationRequested();Directory.CreateDirectory(directory);
   string destination=Reserve(directory,Path.GetFileNameWithoutExtension(source),profile.ext);
   string partial=Path.Combine(directory,".formatflow-"+Guid.NewGuid().ToString("N")+"."+profile.ext);
   bool committed=false;
   try{
    progress?.Report(new ConversionProgress{Indeterminate=true,Message="ממיר..."});
    if(profile.group=="Archive")await Archive.CreateAsync(new[]{source},partial,progress,token).ConfigureAwait(false);
    else if(profile.group=="Table")await Task.Run(()=>Table.Convert(source,partial,profile.ext,token),token).ConfigureAwait(false);
    else{
     double duration=profile.group=="Image"?0:await DurationAsync(source,token).ConfigureAwait(false);
     var args=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-y","-progress","pipe:1","-stats_period","0.2","-nostats","-protocol_whitelist","file,pipe","-i",source};args.AddRange(profile.args);
     if(!keepMetadata)args.AddRange(new[]{"-map_metadata","-1"});args.Add(partial);
     progress?.Report(new ConversionProgress{Indeterminate=duration<=0,Message="ממיר..."});
     var result=await ProcessRunner.RunAsync(engine,args,token,line=>{
      if(duration>0&&line.StartsWith("out_time_us=",StringComparison.Ordinal)&&double.TryParse(line.Substring(12),NumberStyles.Any,CultureInfo.InvariantCulture,out double micros))progress?.Report(new ConversionProgress{Percent=Math.Max(0,Math.Min(99,100*micros/1000000/duration)),Message="ממיר..."});
     }).ConfigureAwait(false);
     if(result.ExitCode!=0)throw new IOException("ההמרה נכשלה: "+result.Error.Trim());
    }
    token.ThrowIfCancellationRequested();if(!File.Exists(partial)||new FileInfo(partial).Length==0)throw new IOException("לא נוצר קובץ פלט תקין.");
    if(new FileInfo(destination).Length!=0)throw new IOException("שם הפלט נתפס במהלך ההמרה.");
    File.Replace(partial,destination,null);committed=true;progress?.Report(new ConversionProgress{Percent=100,Message="הושלם"});return destination;
   }finally{
    if(File.Exists(partial)){try{File.Delete(partial);}catch{}}
    if(!committed&&File.Exists(destination)&&new FileInfo(destination).Length==0){try{File.Delete(destination);}catch{}}
   }
  }
  static string Reserve(string directory,string name,string extension){for(int n=0;;n++){string path=Path.Combine(directory,name+(n==0?"":" ("+n+")")+"."+extension);try{using(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){}return path;}catch(IOException){if(!File.Exists(path))throw;}}}
 }
 public static class Archive {
  public static async Task CreateAsync(IEnumerable<string> sources,string destination,IProgress<ConversionProgress> progress,CancellationToken token){
   var files=sources.ToList();if(files.Count==0)throw new InvalidOperationException("אין תוצאות לשמירה.");
   if(files.Any(p=>string.Equals(Path.GetFullPath(p),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("הארכיון אינו יכול להחליף את אחד הקבצים שנבחרו.");
   string partial=Path.Combine(Path.GetDirectoryName(destination),".formatflow-archive-"+Guid.NewGuid().ToString("N")+".zip");
   long total=files.Sum(p=>new FileInfo(p).Length),copied=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var clock=Stopwatch.StartNew();
   try{
    using(var zip=ZipFile.Open(partial,ZipArchiveMode.Create)){
     foreach(string source in files){
      token.ThrowIfCancellationRequested();string original=Path.GetFileName(source),name=original;int n=1;while(!names.Add(name)){name=Path.GetFileNameWithoutExtension(original)+" ("+(n++)+")"+Path.GetExtension(original);}
      var entry=zip.CreateEntry(name,CompressionLevel.Optimal);
      using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true))using(var output=entry.Open()){
       var buffer=new byte[65536];int read;while((read=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0){await output.WriteAsync(buffer,0,read,token).ConfigureAwait(false);copied+=read;if(clock.ElapsedMilliseconds>=100){progress?.Report(new ConversionProgress{Percent=total==0?0:Math.Min(99,100.0*copied/total),Message="שומר תוצאות..."});clock.Restart();}}
      }
     }
    }
    token.ThrowIfCancellationRequested();if(File.Exists(destination))File.Replace(partial,destination,null);else File.Move(partial,destination);
    progress?.Report(new ConversionProgress{Percent=100,Message="כל התוצאות נשמרו"});
   }finally{if(File.Exists(partial)){try{File.Delete(partial);}catch{}}}
  }
 }
 public static class Table {
  public static void Convert(string source,string output,string target,CancellationToken token){
   token.ThrowIfCancellationRequested();var extension=Path.GetExtension(source).ToLowerInvariant();var rows=new List<Dictionary<string,object>>();string[] headers;
   if(extension==".json"){
    if(!File.ReadAllText(source).TrimStart().StartsWith("[",StringComparison.Ordinal))throw new InvalidDataException("JSON חייב להיות מערך של שורות.");
    rows=Json.Read<List<Dictionary<string,object>>>(File.ReadAllText(source));if(rows==null||rows.Count==0)throw new InvalidDataException("הטבלה ריקה.");headers=rows[0].Keys.ToArray();if(headers.Length==0)throw new InvalidDataException("לא נמצאו עמודות.");
    foreach(var row in rows){if(row==null||!new HashSet<string>(row.Keys).SetEquals(headers))throw new InvalidDataException("לכל שורות JSON חייבות להיות אותן עמודות.");foreach(var key in headers.ToArray())row[key]=Normalize(row[key]);}
   }else{
    char delimiter=extension==".tsv"?'\t':',';var parsed=Parse(File.ReadAllText(source),delimiter);if(parsed.Count<2)throw new InvalidDataException("לא נמצאו שורות נתונים בטבלה.");headers=parsed[0];if(headers.Any(string.IsNullOrWhiteSpace)||headers.Distinct().Count()!=headers.Length)throw new InvalidDataException("נדרשים שמות עמודות ייחודיים שאינם ריקים.");
    foreach(var values in parsed.Skip(1)){token.ThrowIfCancellationRequested();if(values.Length!=headers.Length)throw new InvalidDataException("מספר עמודות שונה בין שורות הטבלה.");var row=new Dictionary<string,object>();for(int i=0;i<headers.Length;i++)row[headers[i]]=values[i];rows.Add(row);}
   }
   token.ThrowIfCancellationRequested();if(target=="json"){File.WriteAllText(output,Json.Write(rows),new UTF8Encoding(false));return;}
   var result=new StringBuilder();if(target=="html"){
    result.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>FormatFlow</title><style>body{font:16px Segoe UI;padding:32px}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:10px}</style></head><body><table><thead><tr>");foreach(var h in headers)result.Append("<th>"+WebUtility.HtmlEncode(h)+"</th>");result.Append("</tr></thead><tbody>");
    foreach(var row in rows){token.ThrowIfCancellationRequested();result.Append("<tr>");foreach(var h in headers)result.Append("<td>"+WebUtility.HtmlEncode(Cell(row[h]))+"</td>");result.Append("</tr>");}result.Append("</tbody></table></body></html>");
   }else{char delimiter=target=="tsv"?'\t':',';Action<IEnumerable<string>> line=values=>result.AppendLine(string.Join(delimiter.ToString(),values.Select(v=>"\""+v.Replace("\"","\"\"")+"\"")));line(headers);foreach(var row in rows){token.ThrowIfCancellationRequested();line(headers.Select(h=>Cell(row[h])));}}
   File.WriteAllText(output,result.ToString(),new UTF8Encoding(target!="html"));
  }
  static object Normalize(object value){
#if !NETFRAMEWORK
   if(value is System.Text.Json.JsonElement e){switch(e.ValueKind){case System.Text.Json.JsonValueKind.Null:return null;case System.Text.Json.JsonValueKind.String:return e.GetString();case System.Text.Json.JsonValueKind.Number:return e.GetDecimal();case System.Text.Json.JsonValueKind.True:return true;case System.Text.Json.JsonValueKind.False:return false;default:throw new InvalidDataException("JSON חייב להיות טבלה שטוחה.");}}
#endif
   if(value==null||value is string||value is ValueType)return value;throw new InvalidDataException("JSON חייב להיות טבלה שטוחה.");
  }
  static string Cell(object value){if(value==null)return "";if(value is bool b)return b?"true":"false";return System.Convert.ToString(value,CultureInfo.InvariantCulture);}
  public static List<string[]> Parse(string text,char delimiter){
   var result=new List<string[]>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false,afterQuote=false;
   for(int i=0;i<text.Length;i++){
    char c=text[i];if(quoted){if(c=='"'){if(i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else{quoted=false;afterQuote=true;}}else cell.Append(c);continue;}
    if(c=='"'){if(cell.Length!=0||afterQuote)throw new InvalidDataException("מרכאות לא תקינות בטבלה.");quoted=true;continue;}
    if(c==delimiter){row.Add(cell.ToString());cell.Clear();afterQuote=false;continue;}
    if(c=='\r'||c=='\n'){if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;row.Add(cell.ToString());result.Add(row.ToArray());row.Clear();cell.Clear();afterQuote=false;continue;}
    if(afterQuote)throw new InvalidDataException("תווים לא תקינים אחרי מרכאות.");cell.Append(c);
   }
   if(quoted)throw new InvalidDataException("מרכאות לא סגורות בטבלה.");if(cell.Length>0||row.Count>0||afterQuote){row.Add(cell.ToString());result.Add(row.ToArray());}return result;
  }
 }
}
