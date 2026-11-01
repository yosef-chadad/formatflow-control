using System;
using System.IO;
using System.Runtime.InteropServices;
namespace FormatFlow {
 // Own PCM audio, pinned for asynchronous playback. No Windows sound-scheme alias
 // and SND_NODEFAULT prevents a fallback system beep if audio is unavailable.
 public sealed class CompletionSound : IDisposable {
  [DllImport("winmm.dll",EntryPoint="PlaySoundW",ExactSpelling=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool PlaySound(IntPtr sound,IntPtr module,uint flags);
  GCHandle pinned;bool initialized,disposed;
  public void Play(){
   if(disposed)return;
   if(!initialized){using(var resource=Embedded.Open("Completion.wav"))using(var memory=new MemoryStream()){resource.CopyTo(memory);pinned=GCHandle.Alloc(memory.ToArray(),GCHandleType.Pinned);initialized=true;}}
   const uint Async=0x0001,NoDefault=0x0002,Memory=0x0004;
   PlaySound(pinned.AddrOfPinnedObject(),IntPtr.Zero,Async|NoDefault|Memory);
  }
  public void Dispose(){if(disposed)return;disposed=true;if(initialized){try{PlaySound(IntPtr.Zero,IntPtr.Zero,0);}catch{}pinned.Free();}}
 }
}
