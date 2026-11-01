using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using FormatFlow;
static class SavingTests {
 public static async Task Run(string workspace){
  var checks=new List<object>();Action<string,bool> check=(name,ok)=>{checks.Add(new{name,passed=ok});Console.WriteLine(name+": "+ok);if(!ok)throw new Exception(name);};
  string root=Path.Combine(workspace,"NativeValidation","saving-"+Guid.NewGuid().ToString("N"));string output=Path.Combine(root,"configured-output");Directory.CreateDirectory(root);
  string first=Path.Combine(workspace,"NativeTests/Fixtures/sample.mp4");string originalHash=Storage.Hash(first);
  var profiles=Json.Read<List<FormatProfile>>(File.ReadAllText(Path.Combine(workspace,"FormatFlowNative/Resources/formats.json")));var profile=profiles.Single(p=>p.ext=="mp3");
  var converter=new FormatFlow.Converter(Environment.GetEnvironmentVariable("FORMATFLOW_TEST_ENGINE")??"ffmpeg");
  string result=await converter.ConvertAsync(profile,first,output,false,null,CancellationToken.None);
  check("mp4-to-mp3-uses-configured-directory",Path.GetDirectoryName(result)==output);
  check("single-conversion-creates-one-mp3-only",Directory.GetFiles(output).Length==1&&Path.GetExtension(result)==".mp3"&&new FileInfo(result).Length>0);
  check("no-zip-or-extra-copy",!Directory.GetFiles(root,"*.zip",SearchOption.AllDirectories).Any()&&!Directory.GetDirectories(output).Any());
  check("input-unchanged",Storage.Hash(first)==originalHash);
  string other=Path.Combine(root,"second clip.mp4");File.Copy(first,other);
  string result2=await converter.ConvertAsync(profile,other,output,false,null,CancellationToken.None);
  check("batch-two-inputs-two-results",Directory.GetFiles(output).Length==2&&Directory.GetFiles(output,"*.mp3").Length==2);
  check("batch-results-in-same-configured-directory",Path.GetDirectoryName(result2)==output);
  string before=Storage.Hash(result);string result3=await converter.ConvertAsync(profile,first,output,false,null,CancellationToken.None);
  check("existing-output-protected",result3!=result&&File.Exists(result)&&Storage.Hash(result)==before);
  check("no-leftover-temporary-files",Directory.GetFiles(output).All(p=>Path.GetExtension(p)==".mp3"));
  // Verify the real output codec, not merely its file extension.
  var probe=await ProcessRunner.RunAsync(Environment.GetEnvironmentVariable("FORMATFLOW_TEST_ENGINE")??"ffmpeg",new[]{"-hide_banner","-i",result},CancellationToken.None);
  check("real-mp3-audio-output",probe.Error.Contains("Audio: mp3"));
  File.WriteAllText(Path.Combine(workspace,"NativeTests/saving-results.json"),Json.Write(checks));Console.WriteLine("Passed "+checks.Count+" saving checks.");
 }
}
