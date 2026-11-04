using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Management;
using System.Collections.Generic;

namespace CodexFloat {
    public class ClaudeDesktopRecord {
        public string Id, CliId, Title, Cwd;
        public DateTime Written, UserAt;
    }
    public static class ClaudeDesktopSource {
        public static string DefaultHome(){return Environment.GetEnvironmentVariable("CLAUDE_DESKTOP_HOME")??"";}
        public static bool ValidId(string id){Guid g;return id!=null&&id.StartsWith("local_",StringComparison.Ordinal)&&Guid.TryParseExact(id.Substring(6),"D",out g);}
        public static string DeepLink(Session session){if(!ValidId(session.Id))throw new ArgumentException("无效的 Claude 桌面端会话 ID");return "claude://claude.ai/epitaxy/"+session.Id;}
        public static List<string> Roots(string root){
            if(!string.IsNullOrWhiteSpace(root))return new List<string>{root};
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots=new List<string>{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Claude"),Path.Combine(local,"Claude-3p")};
            string profiles=Path.Combine(local,"Claude-Data"),packages=Path.Combine(local,"Packages");
            if(Directory.Exists(profiles))roots.AddRange(Directory.GetDirectories(profiles,"claude_*"));
            if(Directory.Exists(packages))roots.AddRange(Directory.GetDirectories(packages,"Claude_*").Select(p=>Path.Combine(p,@"LocalCache\Roaming\Claude")));
            return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static bool RuntimeRunning(){
            var desktop=new HashSet<int>();
            foreach(var process in Process.GetProcessesByName("claude"))using(process){try{
                string path=process.MainModule.FileName;
                if(File.Exists(Path.Combine(Path.GetDirectoryName(path),@"resources\app.asar")))desktop.Add(process.Id);
            }catch{}}
            // An open Electron window does not prove a Code task is executing.
            // Require a stream-JSON worker descended from the desktop process.
            if(desktop.Count==0)return false;
            try{var parents=new Dictionary<int,int>();var workers=new List<int>();
                using(var query=new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,CommandLine,Name FROM Win32_Process"))using(var found=query.Get())foreach(ManagementObject row in found)using(row){
                    int id=Convert.ToInt32(row["ProcessId"]);parents[id]=Convert.ToInt32(row["ParentProcessId"]);string cmd=Convert.ToString(row["CommandLine"]);
                    string name=Convert.ToString(row["Name"]);
                    if((name.Equals("claude.exe",StringComparison.OrdinalIgnoreCase)||name.Equals("node.exe",StringComparison.OrdinalIgnoreCase))&&cmd.Contains("--input-format")&&cmd.Contains("stream-json"))workers.Add(id);
                }
                foreach(int worker in workers){int current=worker;var seen=new HashSet<int>();while(seen.Add(current)&&parents.ContainsKey(current)){current=parents[current];if(desktop.Contains(current))return true;}}
            }catch{}
            return false;
        }
        static DateTime Milliseconds(Dictionary<string,object> data,string key){object value;double ms;if(data.TryGetValue(key,out value)&&double.TryParse(Convert.ToString(value),out ms)&&ms>0)try{return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(ms);}catch{}return DateTime.MinValue;}
        public static ClaudeDesktopRecord Read(string path){
            var data=Json.Read(Json.SharedText(path));if(data==null)return null;
            string id=Json.Text(data,"sessionId"),cli=Json.Text(data,"cliSessionId");Guid guid;
            if(!ValidId(id)||!Guid.TryParseExact(cli,"D",out guid))return null;
            object archived;if(data.TryGetValue("isArchived",out archived)&&archived is bool&&(bool)archived)return null;
            // Side sessions are subordinate work, not separate user conversations.
            var parent=Json.Obj(data,"spawnedFrom");object side;
            if(parent!=null&&parent.TryGetValue("sideSession",out side)&&side is bool&&(bool)side)return null;
            return new ClaudeDesktopRecord{Id=id,CliId=guid.ToString("D"),Title=Json.Text(data,"title"),Cwd=Json.Text(data,"cwd"),Written=File.GetLastWriteTimeUtc(path),UserAt=Milliseconds(data,"latestUserFrameAt")};
        }
    }
    // Desktop metadata supplies identity/title; only its linked transcripts supply
    // lifecycle events. Standalone command-line sessions never enter this engine.
    public class ClaudeEngine:IDisposable {
        readonly object gate=new object();readonly string root;readonly ClaudeTranscriptEngine transcripts;
        Dictionary<string,ClaudeDesktopRecord> records=new Dictionary<string,ClaudeDesktopRecord>(StringComparer.OrdinalIgnoreCase);
        DateTime scanned;public string Health="等待 Claude 桌面端 Code";
        public event Action<List<Session>> Completed;
        public ClaudeEngine(string home,string dataDir,Func<bool> runtimeCheck=null,string transcriptHome=null){
            root=home;
            transcripts=new ClaudeTranscriptEngine(transcriptHome??ClaudeSource.DefaultHome(),Path.Combine(dataDir,"claude-desktop-code"),runtimeCheck??ClaudeDesktopSource.RuntimeRunning,id=>records.ContainsKey(id),session=>{
                ClaudeDesktopRecord record;if(records.TryGetValue(session.Id,out record)&&record.UserAt!=DateTime.MinValue){session.StartedAt=record.UserAt;session.LastActivity=record.UserAt;session.TurnId="desktop-"+record.UserAt.Ticks;session.HasLifecycle=true;session.TimerEstimated=true;}
            });
            transcripts.Completed+=items=>{var mapped=Map(items);if(mapped.Count>0&&Completed!=null)Completed(mapped);};
        }
        void Scan(){
            var found=new Dictionary<string,ClaudeDesktopRecord>(StringComparer.OrdinalIgnoreCase);
            foreach(string profile in ClaudeDesktopSource.Roots(root)){
                string dir=Path.GetFileName(profile).Equals("claude-code-sessions",StringComparison.OrdinalIgnoreCase)?profile:Path.Combine(profile,"claude-code-sessions");
                if(!Directory.Exists(dir))continue;
                foreach(string path in Directory.EnumerateFiles(dir,"local_*.json",SearchOption.AllDirectories))try{
                    var record=ClaudeDesktopSource.Read(path);if(record==null)continue;
                    ClaudeDesktopRecord previous;if(!found.TryGetValue(record.CliId,out previous)||record.Written>previous.Written)found[record.CliId]=record;
                }catch(IOException){}catch(UnauthorizedAccessException){}
            }
            records=found;scanned=DateTime.UtcNow;
        }
        List<Session> Map(IEnumerable<Session> source){
            var result=new List<Session>();foreach(var item in source){ClaudeDesktopRecord record;if(!records.TryGetValue(item.Id,out record))continue;
                // Resuming the same transcript from a terminal must not turn it
                // into a desktop task. Allow a small persistence/timestamp lag.
                if(record.UserAt!=DateTime.MinValue&&item.StartedAt>record.UserAt.AddSeconds(5))continue;
                var session=item.Copy();session.Id=record.Id;session.Platform="claude";
                session.Title=string.IsNullOrWhiteSpace(record.Title)?"Code 对话 · "+record.Id.Substring(6,8):record.Title;
                session.Cwd=record.Cwd;result.Add(session);
            }return result;
        }
        public void Poll(){lock(gate){try{
            if((DateTime.UtcNow-scanned).TotalSeconds>=2)Scan();
            transcripts.Poll();Health=records.Count==0?"等待 Claude 桌面端 Code 会话":"Claude 桌面端 Code 已连接";
        }catch{Health="Claude 桌面端 Code 读取暂不可用，正在重试";}}}
        public List<Session> Snapshot(){lock(gate)return Map(transcripts.Snapshot());}
        public long BytesRead {get{return transcripts.BytesRead;}}
        public void ClearFinished(){lock(gate)transcripts.ClearFinished();}
        public void Dismiss(string id){lock(gate){foreach(var record in records.Values.Where(r=>r.Id==id))transcripts.Dismiss(record.CliId);}}
        public void Dispose(){lock(gate)transcripts.Dispose();}
    }
}
