using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
namespace FormatFlow {
 public static class Embedded {
  public static Stream Open(string name){return Assembly.GetExecutingAssembly().GetManifestResourceStream("FormatFlow."+name)??throw new FileNotFoundException(name);}
  public static string Text(string name){using(var reader=new StreamReader(Open(name),System.Text.Encoding.UTF8))return reader.ReadToEnd();}
  public static async Task<string> EngineAsync(){return await Task.Run(()=>{
   string folder=Path.Combine(Storage.Root,"engine","7.1"),path=Path.Combine(folder,"ffmpeg.exe");Directory.CreateDirectory(folder);
   const string hash="2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3";
   if(File.Exists(path)&&Storage.Hash(path)==hash)return path;
   string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{using(var compressed=Open("ffmpeg.gz"))using(var gzip=new GZipStream(compressed,CompressionMode.Decompress))using(var output=File.Create(temp))gzip.CopyTo(output);if(Storage.Hash(temp)!=hash)throw new InvalidDataException("Engine integrity check failed.");try{if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}catch(IOException){if(!File.Exists(path)||Storage.Hash(path)!=hash)throw;}return path;}finally{if(File.Exists(temp))File.Delete(temp);}
  }).ConfigureAwait(false);}
 }
 public static class Program {
  [STAThread] public static void Main(string[] args){
   try{
    if(args.Length==4&&args[0]=="--apply-update"){ApplyUpdate(args[1],int.Parse(args[2]),args[3]);return;}
    Directory.CreateDirectory(Storage.Root);var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
    app.DispatcherUnhandledException+=(sender,e)=>{MessageBox.Show(e.Exception.Message,"FormatFlow",MessageBoxButton.OK,MessageBoxImage.Error);e.Handled=true;};
    app.Run(new DesktopWindow().Window);
   }catch(Exception ex){try{File.WriteAllText(Path.Combine(Storage.Root,"startup-error.log"),ex.ToString());}catch{}MessageBox.Show(ex.Message,"FormatFlow",MessageBoxButton.OK,MessageBoxImage.Error);}
  }
  static void ApplyUpdate(string destination,int oldPid,string expectedHash){
   string self=Process.GetCurrentProcess().MainModule.FileName;
   if(!string.Equals(Storage.Hash(self),expectedHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Update verification failed.");
   try{using(var previous=Process.GetProcessById(oldPid)){if(!previous.WaitForExit(60000))throw new IOException("Close FormatFlow before updating.");}}catch(ArgumentException){}
   string temp=destination+"."+Guid.NewGuid().ToString("N")+".new";File.Copy(self,temp);
   try{if(File.Exists(destination)){string backups=Path.Combine(Storage.Root,"backups");Directory.CreateDirectory(backups);File.Copy(destination,Path.Combine(backups,Path.GetFileName(destination)+"."+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".bak"));File.Replace(temp,destination,null);}else File.Move(temp,destination);}finally{if(File.Exists(temp))File.Delete(temp);}
   Process.Start(new ProcessStartInfo(destination){UseShellExecute=true});
  }
 }
}
