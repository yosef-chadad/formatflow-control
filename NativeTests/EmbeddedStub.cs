using System.IO;
namespace FormatFlow { public static class Embedded {public static string Text(string name){return File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"FormatFlowNative","Resources",name));}} }
