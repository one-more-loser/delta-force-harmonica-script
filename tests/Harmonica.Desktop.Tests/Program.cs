using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Harmonica.Core;
using Harmonica.Desktop;
using Control=Harmonica.Core.Control;

static class FixTests
{
 static string Root=>Environment.GetEnvironmentVariable("HARMONICA_FIX_DATA")!;
 static string PreviewRoot=>Path.Combine(Root,"previews-v1");
 static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
 static readonly BindingFlags StaticFlags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
 static object? Call(object o,string n,params object?[] a)=>o.GetType().GetMethod(n,Flags)!.Invoke(o,a);
 static T Get<T>(object o,string n)=>(T)o.GetType().GetField(n,Flags)!.GetValue(o)!;
 static void Set(object o,string n,object? v)=>o.GetType().GetField(n,Flags)!.SetValue(o,v);
 static void Log(string n,object? v)=>Console.WriteLine(JsonSerializer.Serialize(new{name=n,value=v}));
 static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
 static int Pending(){var value=typeof(Storage).GetField("pendingCleanup",StaticFlags)!.GetValue(null)!;return(int)value.GetType().GetProperty("Count")!.GetValue(value)!;}
 static async Task DeleteFile(string path){var m=typeof(Storage).GetMethod("CleanupPreviewFileAsync",StaticFlags)??typeof(Storage).GetMethod("CleanupPreviewFile",StaticFlags)!;if(m.Invoke(null,new object[]{path}) is Task task)await task;}
 static async Task DeleteAll(){var m=typeof(Storage).GetMethod("CleanupAllPreviewFilesAsync",StaticFlags)??typeof(Storage).GetMethod("CleanupAllPreviewFiles",StaticFlags)!;if(m.Invoke(null,null)is Task task)await task;}
 static async Task Sweep(){var m=typeof(Storage).GetMethod("CleanupOldPreviewFilesAsync",StaticFlags);if(m!=null)await(Task)m.Invoke(null,null)!;else Storage.Initialize();}
 static string NewPath(){var m=typeof(Storage).GetMethod("CreatePreviewPath",StaticFlags);return m!=null?(string)m.Invoke(null,null)!:Path.Combine(Root,"preview-"+Guid.NewGuid().ToString("N")+".wav");}
 static readonly HashSet<string> fixtures=[];
 static string FileAt(string p,string text="test fixture"){Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text);fixtures.Add(p);return p;}
 static string OwnFile()=>FileAt(NewPath());
 static Score Song()=>new("test",new[]{new Note(60,0,5000)},new Dictionary<int,string>{{1,"x"}},new());
 static MainWindow Window(){var w=new MainWindow(true);Set(w,"settings",new StudioSettings{Volume=0});var sink=Get<NativeInputSink>(w,"sink");Set(sink,"api",(Func<WinInput[],uint>)(_=>1));return w;}
 static async Task Close(MainWindow w){var done=new TaskCompletionSource();w.Closed+=(_,_)=>done.TrySetResult();w.Close();await done.Task.WaitAsync(TimeSpan.FromSeconds(10));}
 [STAThread] static int Main()
 {
  if(string.IsNullOrWhiteSpace(Root)||!string.Equals(Storage.DataRoot,Root,StringComparison.OrdinalIgnoreCase)){Console.WriteLine("REFUSED: isolated Storage data root required");return 2;}
  Directory.CreateDirectory(Root);Storage.Initialize();
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  int unhandled=0;app.DispatcherUnhandledException+=(_,e)=>{unhandled++;Log("UNHANDLED",e.Exception.ToString());e.Handled=true;};
  app.Startup+=async(_,_)=>{int passed=0,failed=0;var tests=new List<(string,Func<Task>)>();
   void Test(string name,Func<Task> run)=>tests.Add((name,run));
   foreach(var tracks in new[]{"absent","empty","9999"}){
    var kind=tracks;Test("F2 "+kind+" accepts final 10000",()=>{Assert(ScoreLoader.FromJson(Json(kind,10000)).Tracks.Count==10000,"10000 not accepted");return Task.CompletedTask;});
    Test("F2 "+kind+" rejects final 10001",()=>{try{ScoreLoader.FromJson(Json(kind,10001));}catch(FormatException e){Assert(e.Message.Contains("音轨"),"wrong diagnostic");return Task.CompletedTask;}throw new Exception("accepted 10001 unique tracks");});
   }
   Test("F2 explicit 10001 rejected",()=>{try{ScoreLoader.FromJson(Json("10001",1));}catch(FormatException){return Task.CompletedTask;}throw new Exception("explicit over-limit accepted");});
   Test("F2 repeated IDs do not consume capacity",()=>{var s=ScoreLoader.FromJson(Json("10000",2));Assert(s.Tracks.Count==10000,"duplicates counted");return Task.CompletedTask;});
   Test("F2 duplicate explicit properties retain last name",()=>{var text=Json("10000",1).Replace("\"tracks\":{","\"tracks\":{\"1\":\"old\",");var s=ScoreLoader.FromJson(text);Assert(s.Tracks.Count==10000&&s.Tracks[1]=="x","duplicate property override changed");return Task.CompletedTask;});
   Test("F5 legacy DataRoot files retained",async()=>{var a=FileAt(Path.Combine(Root,"preview-user.wav"));var b=FileAt(Path.Combine(Root,"preview-"+Guid.NewGuid().ToString("N")+".wav"));File.SetLastWriteTimeUtc(a,DateTime.UtcNow.AddHours(-25));File.SetLastWriteTimeUtc(b,DateTime.UtcNow.AddHours(-25));await Sweep();Assert(File.Exists(a)&&File.Exists(b),"startup deleted unowned legacy WAV");});
   Test("F5 outside and parent paths refused",async()=>{var p=FileAt(Path.Combine(Root,"external.wav"));await DeleteFile(p);Assert(File.Exists(p),"deleted outside dedicated preview directory");var traversal=Path.Combine(PreviewRoot,"..","external.wav");await DeleteFile(traversal);Assert(File.Exists(p),"accepted parent traversal");});
   Test("F5 unknown dedicated-directory files retained",async()=>{var p=FileAt(Path.Combine(PreviewRoot,"preview-user.wav"));File.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddHours(-25));await Sweep();Assert(File.Exists(p),"deleted unknown dedicated file");});
   Test("F5 persistent lock keeps UI dispatch responsive",async()=>{var p=OwnFile();using(var hold=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)){var sw=Stopwatch.StartNew();var seen=new TaskCompletionSource<double>();_=Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>seen.SetResult(sw.Elapsed.TotalMilliseconds)),DispatcherPriority.Input);var task=DeleteFile(p);var delay=await seen.Task;await task;Log("lock_ui_delay",new{delay,fileExists=File.Exists(p),pending=Pending()});Assert(delay<100,"cleanup blocked Dispatcher for "+delay+"ms");Assert(File.Exists(p),"locked file disappeared");}await DeleteAll();Assert(!File.Exists(p),"unlock-before-close cleanup failed");});
   Test("F5 bounded pending and overflow rediscovery",async()=>{var paths=Enumerable.Range(0,80).Select(_=>OwnFile()).ToArray();try{foreach(var p in paths){File.SetAttributes(p,FileAttributes.ReadOnly);await DeleteFile(p);}int count=Pending();foreach(var p in paths)File.SetAttributes(p,FileAttributes.Normal);await DeleteAll();Log("queue_bound",new{count,remaining=paths.Count(File.Exists)});Assert(count<=64,"pending grew beyond 64: "+count);Assert(paths.All(p=>!File.Exists(p)),"overflow files were not rediscovered");}finally{foreach(var p in paths)if(File.Exists(p))File.SetAttributes(p,FileAttributes.Normal);}});
   Test("F5 batch with 80 locked files does not multiply retry delays",async()=>{var paths=Enumerable.Range(0,80).Select(_=>OwnFile()).ToArray();var locks=paths.Select(p=>new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)).ToArray();try{var sw=Stopwatch.StartNew();await DeleteAll();var ms=sw.Elapsed.TotalMilliseconds;Log("batch_locked_ms",ms);Assert(ms<1000,"per-file retry delays multiplied: "+ms);}finally{foreach(var h in locks)h.Dispose();}await DeleteAll();});
   Test("F5 real preview uses dedicated directory and awaits cleanup",PreviewLifecycle);
   Test("F5 creation error always ends operation",CreationError);
   Test("F5 other-session recent and locked-old files survive",async()=>{Directory.CreateDirectory(PreviewRoot);var other=FileAt(Path.Combine(PreviewRoot,"preview-"+Guid.NewGuid().ToString("N")+"-"+Guid.NewGuid().ToString("N")+".wav"));await DeleteAll();Assert(File.Exists(other),"close removed another session recent file");File.SetLastWriteTimeUtc(other,DateTime.UtcNow.AddHours(-25));using(var hold=new FileStream(other,FileMode.Open,FileAccess.Read,FileShare.Read)){await Sweep();Assert(File.Exists(other),"startup deleted another session locked file");}await Sweep();Assert(!File.Exists(other),"eligible unlocked old session file not recovered");});
   Test("F3 startup cleanup owns busy and operationTask through queued close",StartupLifecycle);
   foreach(var(name,run)in tests){try{await run();passed++;Log("PASS",name);}catch(Exception e){failed++;Log("FAIL",new{name,error=e.GetBaseException().Message});}finally{foreach(var p in fixtures.ToArray())if(File.Exists(p)){try{File.SetAttributes(p,FileAttributes.Normal);File.Delete(p);}catch(IOException){}}}}
   Log("RESULT",new{passed,failed,total=tests.Count,unhandled});app.Shutdown(failed==0&&unhandled==0?0:1);
  };return app.Run();
 }
 static string Json(string mode,int noteTracks){string tracks=mode=="absent"?"":",\"tracks\":{"+string.Join(',',Enumerable.Range(1,mode=="empty"?0:int.Parse(mode)).Select(i=>$"\"{i}\":\"x\""))+"}";return "{\"format\":\"gtiartist-score-v1\""+tracks+",\"notes\":["+string.Join(',',Enumerable.Range(1,noteTracks).Select(i=>$"{{\"pitch\":60,\"start\":0,\"duration\":1,\"track\":{i}}}"))+"]}";}
 static async Task PreviewLifecycle()
 {
  var before=Directory.GetFiles(Root,"*.wav",SearchOption.AllDirectories).ToHashSet();var w=Window();var closed=new TaskCompletionSource();w.Closed+=(_,_)=>closed.TrySetResult();FileStream? hold=null;Task? task=null;
  try
  {
   task=(Task)Call(w,"PreviewAsync",ScoreCompiler.Compile(Song(),new()))!;Set(w,"operationTask",task);string? path=null;
   for(int i=0;i<200&&hold==null;i++){await Task.Delay(5);path=Directory.GetFiles(Root,"*.wav",SearchOption.AllDirectories).FirstOrDefault(x=>!before.Contains(x));if(path!=null)try{hold=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);}catch(IOException){}}
   Assert(hold!=null,"preview not lockable");fixtures.Add(path!);bool correctDirectory=Path.GetDirectoryName(path)==PreviewRoot;Call(w,"StopOperation");var queued=Stopwatch.StartNew();bool busy=false,completed=false;double delay=0;
   await w.Dispatcher.InvokeAsync(()=>{busy=Get<bool>(w,"busy");completed=task.IsCompleted;delay=queued.Elapsed.TotalMilliseconds;w.Close();},DispatcherPriority.Input);await task;await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));Log("preview_lifecycle",new{correctDirectory,busyDuringCleanup=busy,completedDuringCleanup=completed,uiDelay=delay,cts=Get<CancellationTokenSource?>(w,"operation")!=null});Assert(correctDirectory,"preview still stored at DataRoot root");Assert(busy&&!completed,"cleanup was not part of busy operation");Assert(delay<100,"UI blocked during preview cleanup");Assert(Get<CancellationTokenSource?>(w,"operation")==null,"CTS leaked");
  }
  finally {hold?.Dispose();Call(w,"StopOperation");if(task!=null)await task.WaitAsync(TimeSpan.FromSeconds(10));if(!closed.Task.IsCompleted){w.Close();await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));}}
 }
 static async Task CreationError()
 {
  if(Directory.Exists(PreviewRoot)){Assert(!Directory.EnumerateFileSystemEntries(PreviewRoot).Any(),"creation-error fixture directory not empty");Directory.Delete(PreviewRoot);}
  FileAt(PreviewRoot);var w=Window();try{var task=(Task)Call(w,"PreviewAsync",ScoreCompiler.Compile(Song(),new()))!;Set(w,"operationTask",task);await task.WaitAsync(TimeSpan.FromSeconds(10));bool busy=Get<bool>(w,"busy");var message=((System.Windows.Controls.TextBlock)w.FindName("Detail")).Text;Assert(!busy&&Get<CancellationTokenSource?>(w,"operation")==null,"path creation failure stranded operation");Assert(!string.IsNullOrWhiteSpace(message)&&!message.Contains("试听结束"),"creation failure not reported");Log("creation_error",message);}finally{await Close(w);File.Delete(PreviewRoot);}
 }
 static async Task StartupLifecycle()
 {
  var w=Window();var method=w.GetType().GetMethod("InitializeLibraryAsync",Flags);Assert(method!=null,"startup lifecycle entry missing");
  ThreadPool.GetMaxThreads(out int maxWorkers,out int maxIo);ThreadPool.GetMinThreads(out int minWorkers,out int minIo);
  using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var closed=new TaskCompletionSource();w.Closed+=(_,_)=>closed.TrySetResult();Task? startup=null;
  try
  {
   Assert(ThreadPool.SetMinThreads(1,1)&&ThreadPool.SetMaxThreads(1,maxIo),"cannot constrain pool for deterministic queue test");
   ThreadPool.QueueUserWorkItem(_=>{entered.Set();release.Wait();});Assert(entered.Wait(TimeSpan.FromSeconds(5)),"pool blocker did not start");
   startup=(Task)method!.Invoke(w,null)!;Set(w,"operationTask",startup);Assert(!startup.IsCompleted,"cleanup did not yield to worker");
   await w.Dispatcher.InvokeAsync(()=>{bool busy=Get<bool>(w,"busy");Call(w,"PreviewClick",w,new RoutedEventArgs());Call(w,"PlayClick",w,new RoutedEventArgs());Call(w,"ToggleFromHotkey");var data=new DataObject(DataFormats.FileDrop,new[]{Path.Combine(Root,"no-import.json")});var drop=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),Flags,null,new object[]{data,DragDropKeyStates.None,DragDropEffects.Copy,w,new Point()},null)!;drop.RoutedEvent=UIElement.DropEvent;w.RaiseEvent(drop);bool unchanged=ReferenceEquals(startup,Get<Task>(w,"operationTask"));Log("startup_queue",new{busy,unchanged,cts=Get<CancellationTokenSource?>(w,"operation")!=null});Assert(busy&&unchanged,"startup task overwritten by queued input");w.Close();release.Set();},DispatcherPriority.Input);
   await startup.WaitAsync(TimeSpan.FromSeconds(10));await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert(Get<bool>(w,"closeRequested")&&!Get<bool>(w,"busy"),"startup-close lifecycle not ended");
  }
  finally{release.Set();ThreadPool.SetMaxThreads(maxWorkers,maxIo);ThreadPool.SetMinThreads(minWorkers,minIo);if(startup!=null)await startup.WaitAsync(TimeSpan.FromSeconds(10));if(!closed.Task.IsCompleted){w.Close();await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));}}
 }
}
