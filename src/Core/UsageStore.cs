using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Threading;
using System.Security.Cryptography;
namespace Qingye.Core {
    public sealed class UsageOptions { public bool RecentFiles; public bool ReadingPositions; public bool RecoverDrafts; public long ClearedBefore; }
    public sealed class UsageEntry {
        public string Path; public int Caret; public double EditorScroll,ReaderScroll; public string View; public long Time,Updated;
        public UsageEntry Copy(){return (UsageEntry)MemberwiseClone();}
    }
    // Optional, bounded local metadata. A default instance creates no directories or files.
    public sealed class UsageStore {
        readonly string root; readonly string mutexName;
        public UsageStore(string root){this.root=System.IO.Path.GetFullPath(root);using(var sha=SHA256.Create())mutexName="Local\\QingyeUsage-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(this.root.ToUpperInvariant()))).Replace("-","").Substring(0,24);}
        public string Root{get{return root;}}
        public static T Read<T>(string path,int limit) where T:class {try{if(!File.Exists(path)||new FileInfo(path).Length>limit)return null;return new JavaScriptSerializer{MaxJsonLength=limit}.Deserialize<T>(File.ReadAllText(path,Encoding.UTF8));}catch{return null;}}
        public static void Atomic(string path,object value){Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,new JavaScriptSerializer{MaxJsonLength=12*1024*1024}.Serialize(value),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
        void Locked(Action action){using(var mutex=new Mutex(false,mutexName)){bool taken=false;try{try{taken=mutex.WaitOne(1500);}catch(AbandonedMutexException){taken=true;}if(!taken)throw new IOException("本地记录正忙，请稍后重试。");action();}finally{if(taken)mutex.ReleaseMutex();}}}
        public UsageOptions LoadOptions(){return Read<UsageOptions>(System.IO.Path.Combine(root,"usage-options.json"),16384)??new UsageOptions();}
        public List<UsageEntry> LoadEntries(){return (Read<List<UsageEntry>>(System.IO.Path.Combine(root,"usage-history.json"),256*1024)??new List<UsageEntry>()).Where(e=>e!=null&&!String.IsNullOrWhiteSpace(e.Path)).Take(40).ToList();}
        public void SaveOptions(UsageOptions options){Locked(delegate{options.ClearedBefore=LoadOptions().ClearedBefore;Atomic(System.IO.Path.Combine(root,"usage-options.json"),options);if(!options.RecentFiles&&!options.ReadingPositions)ClearFile();else if(!options.ReadingPositions){var entries=LoadEntries();foreach(var e in entries){e.Caret=0;e.EditorScroll=e.ReaderScroll=0;e.View=null;}if(entries.Count>0)Atomic(System.IO.Path.Combine(root,"usage-history.json"),entries);}});}
        void ClearFile(){string file=System.IO.Path.Combine(root,"usage-history.json");if(File.Exists(file))File.Delete(file);}
        public void Clear(){Locked(delegate{var options=LoadOptions();options.ClearedBefore=DateTime.UtcNow.Ticks;Atomic(System.IO.Path.Combine(root,"usage-options.json"),options);ClearFile();});}
        public void WithCurrentOptions(Action<UsageOptions> action){Locked(()=>action(LoadOptions()));}
        public void Remember(IEnumerable<UsageEntry> values){var incoming=values.Select(e=>e.Copy()).ToArray();Locked(delegate{var options=LoadOptions();if(!options.RecentFiles&&!options.ReadingPositions)return;var all=incoming.Concat(LoadEntries()).Where(e=>e.Path.Length<32768&&e.Time>options.ClearedBefore).GroupBy(e=>e.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.OrderByDescending(e=>Math.Max(e.Updated,e.Time)).First().Copy()).OrderByDescending(e=>e.Time).Take(40).ToList();if(all.Count==0){ClearFile();return;}if(!options.ReadingPositions)foreach(var e in all){e.Caret=0;e.EditorScroll=e.ReaderScroll=0;e.View=null;}Atomic(System.IO.Path.Combine(root,"usage-history.json"),all);});}
    }
    public sealed class DraftItem {public string Id,Title,OriginalPath,Text,EncodingName,NewLine;public bool HasBom;public string Journal;}
    public sealed class DraftStore {
        readonly string folder,id=Guid.NewGuid().ToString("N"); FileStream lease;
        public DraftStore(string root){folder=System.IO.Path.Combine(root,"drafts");}
        string FilePath{get{return System.IO.Path.Combine(folder,id+".json");}}
        public void Write(IEnumerable<DraftItem> items){var bounded=new List<DraftItem>();int total=0,jsonBytes=2;foreach(var item in items){if(item.Text==null||item.Text.Length>1024*1024)continue;int size=Encoding.UTF8.GetByteCount(item.Text);if(total+size>6*1024*1024||bounded.Count>=8)break;item.Journal=null;int encoded=Encoding.UTF8.GetByteCount(new JavaScriptSerializer{MaxJsonLength=10*1024*1024}.Serialize(item))+1;if(jsonBytes+encoded>8*1024*1024)continue;jsonBytes+=encoded;total+=size;bounded.Add(item);}if(bounded.Count==0){ClearOwn();return;}Directory.CreateDirectory(folder);if(lease==null)lease=new FileStream(System.IO.Path.Combine(folder,id+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);UsageStore.Atomic(FilePath,bounded);}
        public bool ContainsOwn(string draftId){var list=UsageStore.Read<List<DraftItem>>(FilePath,10*1024*1024);return list!=null&&list.Any(item=>item.Id==draftId);}
        public void ClearOwn(){if(File.Exists(FilePath))File.Delete(FilePath);if(lease!=null){lease.Dispose();lease=null;}string file=System.IO.Path.Combine(folder,id+".lock");if(File.Exists(file))File.Delete(file);}
        bool Valid(string journal){Guid parsed;return Guid.TryParseExact(journal,"N",out parsed)&&journal!=id;}
        FileStream TryLease(string journal){if(!Valid(journal))return null;try{return new FileStream(System.IO.Path.Combine(folder,journal+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){return null;}}
        public bool HasPending(){if(!Directory.Exists(folder))return false;foreach(string path in Directory.EnumerateFiles(folder,"*.json").Take(40))using(var held=TryLease(System.IO.Path.GetFileNameWithoutExtension(path))){if(held!=null&&File.Exists(path)&&new FileInfo(path).Length<=8*1024*1024)return true;}return false;}
        public List<DraftItem> Pending(){long bytes=0;var result=new List<DraftItem>();if(!Directory.Exists(folder))return result;foreach(string path in Directory.EnumerateFiles(folder,"*.json").Take(40)){string journal=System.IO.Path.GetFileNameWithoutExtension(path);using(var held=TryLease(journal)){if(held==null||!File.Exists(path))continue;long size=new FileInfo(path).Length;if(size>8*1024*1024||bytes+size>16*1024*1024)continue;bytes+=size;var list=UsageStore.Read<List<DraftItem>>(path,10*1024*1024);if(list==null)continue;foreach(var item in list.Where(d=>d!=null&&d.Text!=null&&d.Text.Length<=1024*1024).Take(8)){item.Journal=journal;result.Add(item);if(result.Count>=16)return result;}}}return result;}
        public void Remove(DraftItem item){if(!Valid(item.Journal))return;using(var held=TryLease(item.Journal)){if(held==null)return;string path=System.IO.Path.Combine(folder,item.Journal+".json");var list=UsageStore.Read<List<DraftItem>>(path,10*1024*1024);if(list==null)return;list.RemoveAll(d=>d.Id==item.Id);if(list.Count==0)File.Delete(path);else UsageStore.Atomic(path,list);}}
        public void ClearPending(){for(int pass=0;pass<40;pass++){var items=Pending();if(items.Count==0)break;foreach(var item in items)Remove(item);}}
    }
}
