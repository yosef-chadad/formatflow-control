using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FormatFlow;
static class ChannelTests {
 public static async Task Run(){
  var results=new List<object>();Action<string,bool> check=(name,ok)=>{results.Add(new{name,passed=ok});Console.WriteLine(name+": "+ok);if(!ok)throw new Exception(name);};
  bool bad=false;try{ChannelRules.Normalize(new ChannelFeed{schema=99});}catch(InvalidDataException){bad=true;}check("unsupported-schema-rejected",bad);
  var state=ChannelRules.Normalize(Json.Read<ChannelFeed>("{\"schema\":1,\"policy\":{\"enabled\":false,\"message\":\"Paused\"},\"ads\":[],\"appearance\":{\"accent\":\"red;script()\"}}"));check("pause-preserved",!state.policy.enabled&&state.policy.message=="Paused");check("offline-pause-roundtrip",!Json.Read<ChannelFeed>(Json.Write(state)).policy.enabled);check("invalid-color-rejected",state.appearance.accent=="");
  state.ads=new List<Promotion>{new Promotion{title="A",link="javascript:alert(1)",placement="top"},new Promotion{title="duplicate",placement="top"},new Promotion{title="B",link="https://example.com",placement="belowSearch"},new Promotion{title="C",placement="bottom"}};state=ChannelRules.Normalize(state);check("two-unique-ad-slots",state.ads.Count==2&&state.ads.Select(a=>a.placement).Distinct().Count()==2);check("unsafe-ad-link-rejected",state.ads[0].link=="");check("https-ad-link-kept",state.ads[1].link=="https://example.com");
  var release=new ReleaseInfo{version="2.4.1",url=ChannelRules.ReleasePrefix+"v2.4.1/FormatFlow.exe",sha256=new string('a',64),installMode="onNextLaunch"};check("new-own-repo-release",ChannelRules.IsNewRelease(release));check("next-launch-mode",ChannelRules.Automatic(release));release.installMode="ask";check("ask-mode",!ChannelRules.Automatic(release));release.installMode="unknown";check("unknown-mode-no-auto-install",!ChannelRules.Automatic(release));release.version="2.4.0";check("current-version-no-update",!ChannelRules.IsNewRelease(release));release.version="2.4.0.0";check("four-component-version-no-loop",!ChannelRules.IsNewRelease(release));release.version="2.4.1";release.url="https://github.com/another/repo/releases/download/v2.4.1/file.exe";check("other-repo-rejected",!ChannelRules.IsNewRelease(release));release.url=ChannelRules.ReleasePrefix+"v2.4.1/file.exe";release.sha256="invalid";check("bad-checksum-rejected",!ChannelRules.IsNewRelease(release));
  foreach(var url in new[]{"http://yosef-chadad.github.io/feed.json","https://user:password@raw.githubusercontent.com/file","https://evil.example/feed.json","https://yosef-chadad.github.io:444/feed.json"}){bool rejected=false;try{ChannelRules.ValidateUrl(new Uri(url),false);}catch(InvalidDataException){rejected=true;}check("unsafe-origin-"+results.Count,rejected);}
  using(var timeout=new CancellationTokenSource(25000)){var service=new ChannelService();var result=await service.CheckAsync(timeout.Token,false);check("live-feed-readable",result!=null&&result.State.schema==1&&result.State.revision>=1);check("live-policy-and-ad-limit",result.Policy.enabled&&result.Ads.Count<=2);check("no-update-offered",result.UpdatePath==null);check("remote-title-read",result.Appearance.title=="Welcome to Format Switcher");check("valid-feed-cached",ChannelService.ReadCache()?.revision==result.State.revision);}
  File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"NativeTests/channel-results.json"),Json.Write(results));Console.WriteLine("Passed "+results.Count+" channel checks.");
 }
}
