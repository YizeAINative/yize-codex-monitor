using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace CodexFloat {
    static class Tests {
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2(byte[] filename,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_exec(IntPtr db,byte[] sql,IntPtr callback,IntPtr arg,IntPtr error);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close(IntPtr db);
        static void Db(string path,string sql){IntPtr db;int code=sqlite3_open_v2(Encoding.UTF8.GetBytes(path+"\0"),out db,6,IntPtr.Zero);try{if(code!=0)throw new Exception("fixture open "+code);code=sqlite3_exec(db,Encoding.UTF8.GetBytes(sql+"\0"),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);if(code!=0)throw new Exception("fixture SQL "+code);}finally{sqlite3_close(db);}}
        static long Stamp(){return (long)(DateTime.UtcNow-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;}
        static string CcLine(string id,string type,string uuid,object message,bool side=false,string subtype="",bool meta=false,bool error=false){return Json.Serializer().Serialize(new{sessionId=id,type=type,uuid=uuid,cwd=@"C:\Projects\fixture",timestamp=DateTime.UtcNow.ToString("O"),isSidechain=side,isMeta=meta,isApiErrorMessage=error,subtype=subtype,message=message})+"\n";}
        static void ClaudeChecks(){
            string id="cccccccc-cccc-4ccc-8ccc-cccccccccccc";var s=new Session{Id=id,Platform="claude"};
            Func<string,string> user=turn=>CcLine(id,"user",turn,new{role="user",content="Fixture input"});
            Func<string> end=()=>CcLine(id,"assistant","a-final",new{role="assistant",content=new object[0],stop_reason="end_turn"});
            ClaudeSource.Apply(user("turn1"),s);Check(s.Status=="running"&&s.Visible,"Claude human prompt begins a timed round");DateTime started=s.StartedAt;
            ClaudeSource.Apply(CcLine(id,"assistant","a1",new{role="assistant",content=new[]{new{type="thinking",thinking="fixture"}},stop_reason=(string)null}),s);Check(s.Status=="running","Claude thinking is not completion");
            ClaudeSource.Apply(CcLine(id,"user","result",new{role="user",content=new[]{new{type="tool_result",tool_use_id="tool1",content="fixture"}}}),s);Check(s.StartedAt==started&&s.TurnId=="turn1","Claude tool result does not reset timer");
            ClaudeSource.Apply(CcLine(id,"assistant","ask",new{role="assistant",content=new[]{new{type="tool_use",name="AskUserQuestion"}},stop_reason="tool_use"}),s);Check(s.Status=="waiting","Claude question shows waiting");
            ClaudeSource.Apply(CcLine(id,"user","answer",new{role="user",content=new[]{new{type="tool_result",tool_use_id="question",content="fixture"}}}),s);Check(s.Status=="running"&&s.StartedAt==started,"Claude answered question continues same round");
            ClaudeSource.Apply(CcLine(id,"assistant","child",new{role="assistant",stop_reason="end_turn"},true),s);Check(s.Status=="running","Claude child completion cannot finish root");
            ClaudeSource.Apply(end(),s);Check(s.Status=="completed","Claude end_turn marks completed");string frozen=s.RuntimeText(DateTime.UtcNow);Check(s.RuntimeText(DateTime.UtcNow.AddHours(1))==frozen,"Claude completed timer freezes");
            ClaudeSource.Apply(user("turn2"),s);Check(s.Status=="running"&&s.TurnId=="turn2"&&s.StartedAt>=started,"Claude follow-up resets round");
            ClaudeSource.Apply(CcLine(id,"user","cancel",new{role="user",content="[Request interrupted by user]"},false,"",true),s);ClaudeSource.Apply(CcLine(id,"system","duration",null,false,"turn_duration"),s);Check(s.Status=="interrupted","Claude interrupt and duration never turn green");
            ClaudeSource.Apply(user("turn3"),s);ClaudeSource.Apply(CcLine(id,"assistant","err",new{role="assistant",stop_reason=(string)null},false,"",false,true),s);Check(s.Status=="failed","Claude API error is failure");
            ClaudeSource.Apply(Json.Serializer().Serialize(new{type="custom-title",sessionId=id,customTitle="My named session"}),s);ClaudeSource.Apply(Json.Serializer().Serialize(new{type="summary",sessionId=id,summary="Auto summary"}),s);Check(s.Title=="My named session","Claude custom name overrides automatic summary");

            string home=Path.Combine(root,"claude"),dir=Path.Combine(home,"projects","C--fixture"),state=Path.Combine(root,"claude-state");Directory.CreateDirectory(dir);string file=Path.Combine(dir,id+".jsonl");File.WriteAllText(file,user("old")+end(),new UTF8Encoding(false));
            int alerts=0;using(var e=new ClaudeTranscriptEngine(home,state,()=>true)){e.Completed+=items=>alerts+=items.Count;e.Poll();Check(e.Snapshot().Count==0&&alerts==0,"Claude history does not flood initial list");
                Append(file,user("round-1"));e.Poll();Check(e.Snapshot().Single().Status=="running","Claude resumed file automatically joins");Append(file,end());e.Poll();Check(e.Snapshot().Single().Status=="completed"&&alerts==1,"Claude completion emits one alert");
                Append(file,end());e.Poll();Check(alerts==1,"Claude repeated terminal message is deduplicated");Append(file,user("round-2"));e.Poll();Check(e.Snapshot().Single().Status=="running","Claude completed row resumes spinner");Append(file,end());e.Poll();Check(alerts==2,"Claude next round alerts again");e.ClearFinished();Check(e.Snapshot().Count==0&&File.Exists(file),"Claude clear does not delete transcript");
                string childDir=Path.Combine(dir,id,"subagents");Directory.CreateDirectory(childDir);File.WriteAllText(Path.Combine(childDir,"dddddddd-dddd-4ddd-8ddd-dddddddddddd.jsonl"),user("child"));Thread.Sleep(100);e.Poll();Check(e.Snapshot().Count==0,"Claude nested subagent file is excluded");}
            using(var e=new ClaudeTranscriptEngine(home,state,()=>true)){e.Completed+=items=>alerts+=items.Count;e.Poll();Check(e.Snapshot().Count==0&&alerts==2,"Claude dismissal and alert keys survive restart");Append(file,user("round-3"));e.Poll();Append(file,end());e.Poll();Check(alerts==3,"Claude resumed after restart alerts normally");}
            Check(Json.Serializer().Deserialize<Settings>("{\"MonitorCodex\":true}").MonitorClaude,"existing settings enable new Claude option");
        }
        static void ClaudeDesktopChecks(){
            string desktop=Path.Combine(root,"desktop"),home=Path.Combine(root,"desktop-transcripts"),state=Path.Combine(root,"desktop-state");
            string cli="11111111-1111-4111-8111-111111111111",id="local_22222222-2222-4222-8222-222222222222",standalone="33333333-3333-4333-8333-333333333333";
            string metadata=Path.Combine(desktop,"claude-code-sessions","account","org",id+".json"),dir=Path.Combine(home,"projects","fixture");Directory.CreateDirectory(dir);
            Json.Save(metadata,new{sessionId=id,cliSessionId=cli,title="Desktop title",cwd=@"C:\Projects\desktop",isArchived=false});
            string file=Path.Combine(dir,cli+".jsonl"),other=Path.Combine(dir,standalone+".jsonl");
            Func<string,string> user=turn=>CcLine(cli,"user",turn,new{role="user",content="fixture"});
            Func<string> end=()=>CcLine(cli,"assistant","final",new{role="assistant",stop_reason="end_turn"});
            File.WriteAllText(file,user("old")+end());File.WriteAllText(other,CcLine(standalone,"user","cli-only",new{role="user",content="fixture"}));
            Check(ClaudeDesktopSource.DeepLink(new Session{Id=id})=="claude://claude.ai/epitaxy/"+id,"desktop link opens exact Code page");
            bool invalid=false;try{ClaudeDesktopSource.DeepLink(new Session{Id="local_x/../../new?q=bad"});}catch(ArgumentException){invalid=true;}Check(invalid,"desktop link rejects malformed identifiers");
            int notices=0;using(var engine=new ClaudeEngine(desktop,state,()=>true,home)){
                engine.Completed+=items=>{notices+=items.Count;Check(items.All(s=>s.Id==id),"notification uses desktop identity");};
                engine.Poll();Check(engine.Snapshot().Count==0&&notices==0,"desktop skips history and standalone command-line task");
                Append(file,user("round1"));engine.Poll();var session=engine.Snapshot().Single();
                Check(session.Id==id&&session.Title=="Desktop title"&&session.Cwd==@"C:\Projects\desktop"&&session.Status=="running","desktop metadata maps active transcript to desktop task");
                Append(file,end());engine.Poll();Check(notices==1&&engine.Snapshot().Single().Status=="completed","desktop completion notifies once");engine.Poll();Check(notices==1,"desktop repeat poll does not repeat notification");
                engine.Dismiss(id);Check(engine.Snapshot().Count==0&&File.Exists(file)&&File.Exists(metadata),"desktop dismiss maps ID without deleting source");
                Append(file,user("round2"));engine.Poll();Check(engine.Snapshot().Single().Status=="running","desktop next round reappears");
                Append(file,CcLine(cli,"user","interrupt",new{role="user",content="[Request interrupted by user]"}));engine.Poll();Check(engine.Snapshot().Single().Status=="interrupted"&&notices==1,"desktop interruption is not a success notification");engine.ClearFinished();
            }
            using(var engine=new ClaudeEngine(desktop,state,()=>true,home)){engine.Poll();Check(engine.Snapshot().Count==0,"desktop dismissal survives restart");}
            string oldHome=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");try{
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR",home);
                using(var hub=new MonitoringHub(Path.Combine(root,"none"),Path.Combine(root,"none"),Path.Combine(root,"desktop-hub"),false,false,desktop,true)){
                    int alerts=0;hub.Completed+=items=>alerts+=items.Count;hub.Poll();Append(file,user("hub"));hub.Poll();Check(hub.Snapshot().Single().Id==id,"hub monitors desktop Code independently");
                    hub.Configure(false,false,false);Append(file,end());hub.Poll();Check(hub.Snapshot().Count==0&&alerts==0,"unchecked desktop stops showing and notifying");
                    hub.Configure(false,false,true);hub.Poll();hub.Poll();Check(alerts==0,"desktop re-enable does not replay paused completions");
                }
            }finally{Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR",oldHome);}
            Json.Save(metadata,new{sessionId=id,cliSessionId=cli,isArchived=true});Check(ClaudeDesktopSource.Read(metadata)==null,"archived desktop records excluded");
            // Reproduce the actual local 17–40 MB history files: an old user
            // event in chunk one must stay hidden until the final event is read.
            Json.Save(metadata,new{sessionId=id,cliSessionId=cli,title="Large history"});
            using(var writer=new StreamWriter(file,false,new UTF8Encoding(false))){writer.Write(user("large-history"));string padding=Json.Serializer().Serialize(new{type="ignored",data=new string('x',1024*1024)});for(int i=0;i<17;i++)writer.WriteLine(padding);writer.Write(end());}
            using(var engine=new ClaudeEngine(desktop,Path.Combine(root,"large-desktop-state"),()=>true,home)){
                int alerts=0;engine.Completed+=items=>alerts+=items.Count;engine.Poll();Check(engine.BytesRead<=256*1024,"startup reads at most 256 KiB instead of full history");Check(engine.Snapshot().Count==0,"large desktop history stays hidden on startup");engine.Poll();Check(engine.Snapshot().Count==0&&alerts==0,"large historic completion does not notify on repeat poll");
            }
            // The tail can begin exactly on a newline: do not discard a valid
            // first record. Rewrites must seek to the new tail, not byte zero.
            WriteExactTail(file,1024*1024,user("tail-boundary"));
            using(var engine=new ClaudeEngine(desktop,Path.Combine(root,"boundary-state"),()=>true,home)){
                int alerts=0;engine.Completed+=items=>alerts+=items.Count;engine.Poll();Check(engine.Snapshot().Single().TurnId=="tail-boundary","exact tail boundary keeps the first complete user record");
                long read=engine.BytesRead;Append(file,end());engine.Poll();Check(alerts==1&&engine.BytesRead-read<8192,"tail tracker reads only new completion bytes");
                WriteExactTail(file,512*1024,user("rewritten-tail"));engine.Poll();Check(engine.Snapshot().Single().Status=="running"&&engine.Snapshot().Single().TurnId=="rewritten-tail","truncated transcript seeks to new tail and resets state");
                Check(engine.BytesRead-read<270000,"truncation recovery has bounded IO");
            }
            long userAt=Stamp()-30000;Json.Save(metadata,new{sessionId=id,cliSessionId=cli,title="Long tool run",latestUserFrameAt=userAt});
            WriteExactTail(file,1024*1024,CcLine(cli,"progress","tool-progress",null));
            using(var engine=new ClaudeEngine(desktop,Path.Combine(root,"progress-only-state"),()=>true,home)){
                int alerts=0;engine.Completed+=items=>alerts+=items.Count;engine.Poll();var s=engine.Snapshot().Single();Check(s.Status=="running"&&s.TimerEstimated,"desktop prompt metadata seeds progress-only tail with estimated timer");
                Append(file,end());engine.Poll();Check(alerts==1&&engine.Snapshot().Single().Status=="completed","progress-only tail transitions to completed once");
            }
            long freshAt=Stamp();Json.Save(metadata,new{sessionId=id,cliSessionId=cli,title="New prompt",latestUserFrameAt=freshAt});
            string staleEnd=Json.Serializer().Serialize(new{sessionId=cli,type="assistant",timestamp=DateTime.UtcNow.AddSeconds(-10).ToString("O"),message=new{stop_reason="end_turn"}})+"\n";
            WriteExactTail(file,1024*1024,staleEnd);
            using(var engine=new ClaudeEngine(desktop,Path.Combine(root,"stale-terminal-state"),()=>true,home)){int alerts=0;engine.Completed+=items=>alerts+=items.Count;engine.Poll();Check(engine.Snapshot().Single().Status=="unknown"&&alerts==0,"older terminal event cannot finish a newer desktop prompt");}
            File.WriteAllText(file,"{\"type\":\"ignored\",\"data\":\""+new string('x',600000),new UTF8Encoding(false));
            using(var engine=new ClaudeEngine(desktop,Path.Combine(root,"partial-tail-state"),()=>true,home)){
                int alerts=0;engine.Completed+=items=>alerts+=items.Count;engine.Poll();Check(engine.Snapshot().Single().Status=="unknown","partial final line does not invent completion");
                Append(file,"\"}\n"+end());engine.Poll();Check(alerts==1&&engine.Snapshot().Single().Status=="completed","partial tail is skipped cleanly before appended complete event");
            }
        }
        static void WriteExactTail(string file,int prefixBytes,string record){
            const string start="{\"type\":\"ignored\",\"data\":\"",finish="\"}\n";
            int padding=256*1024-Encoding.UTF8.GetByteCount(record+start+finish);
            using(var writer=new StreamWriter(file,false,new UTF8Encoding(false))){writer.Write(new string('p',prefixBytes-1));writer.Write('\n');writer.Write(record);writer.Write(start);writer.Write(new string('x',padding));writer.Write(finish);}
        }
        static void WorkBuddyChecks(){
            string wb=Path.Combine(root,"workbuddy"),db=Path.Combine(wb,"workbuddy.db"),state=Path.Combine(root,"workbuddy-state"),id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";Directory.CreateDirectory(wb);long stamp=Stamp();
            Db(db,"CREATE TABLE sessions(id TEXT PRIMARY KEY,cwd TEXT,title TEXT,custom_title TEXT,status TEXT,created_at INTEGER,updated_at INTEGER,last_activity_at INTEGER,deleted_at INTEGER); INSERT INTO sessions VALUES('"+id+"','C:\\Projects\\demo','Default title','Custom title','completed',"+stamp+","+stamp+",0,NULL)");
            Check(WorkBuddySource.State("Planning")=="running"&&WorkBuddySource.State("working")=="running","WorkBuddy planning and working map to spinner");
            Check(WorkBuddySource.State("pending")=="waiting"&&WorkBuddySource.State("terminated")=="interrupted","WorkBuddy waiting and interruption are distinct");
            Check(WorkBuddySource.State("error")=="failed"&&WorkBuddySource.State("unexpected")=="unknown","WorkBuddy unknown state never becomes completed");
            var rows=WorkBuddySource.Read(db);Check(rows.Count==1&&rows[0].Title=="Custom title","native read-only SQLite returns WorkBuddy custom title");
            bool readOnly=false;try{ReadOnlySqlite.Query(db,"DELETE FROM sessions");}catch(IOException){readOnly=true;}Check(readOnly&&WorkBuddySource.Read(db).Count==1,"SQLite reader rejects writes to source");
            int alerts=0;using(var e=new WorkBuddyEngine(wb,state)){e.Completed+=items=>alerts+=items.Count;e.Poll();Check(e.Snapshot().Count==0&&alerts==0,"WorkBuddy startup does not replay historic completions");
                Db(db,"UPDATE sessions SET status='pending',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Count==0,"unstarted pending draft is not monitored");
                Db(db,"UPDATE sessions SET status='planning',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().Status=="running","WorkBuddy draft becomes running and autojoins");string turn=e.Snapshot().Single().TurnId;
                Db(db,"UPDATE sessions SET status='working',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().TurnId==turn,"planning to working preserves same timer");
                Db(db,"UPDATE sessions SET status='pending',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().Status=="waiting","in-flight permission wait remains visible");
                Db(db,"UPDATE sessions SET status='working',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().TurnId==turn,"resolving input does not reset round");
                Db(db,"UPDATE sessions SET status='completed',updated_at="+(++stamp));e.Poll();Check(alerts==1&&e.Snapshot().Single().Status=="completed","WorkBuddy completion turns green and alerts");
                Db(db,"UPDATE sessions SET custom_title='Renamed',updated_at="+(++stamp));e.Poll();Check(alerts==1&&e.Snapshot().Single().Title=="Renamed","renaming completed task does not repeat alert");
                Db(db,"UPDATE sessions SET status='planning',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().TurnId!=turn&&e.Snapshot().Single().Status=="running","WorkBuddy follow-up resets round and spinner");
                Db(db,"UPDATE sessions SET status='terminated',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().Status=="interrupted"&&alerts==1,"WorkBuddy stop does not alert success");
                Db(db,"UPDATE sessions SET status='working',updated_at="+(++stamp));e.Poll();Db(db,"UPDATE sessions SET status='completed',updated_at="+(++stamp));e.Poll();Check(alerts==2,"WorkBuddy later round alerts again");
                string previous=e.Snapshot().Single().TurnId;Db(db,"UPDATE sessions SET status='pending',updated_at="+(++stamp));e.Poll();Db(db,"UPDATE sessions SET status='working',updated_at="+(++stamp));e.Poll();Check(e.Snapshot().Single().TurnId!=previous&&e.Snapshot().Single().FinishedAt==DateTime.MinValue,"WorkBuddy completed-pending-running resets completed round");
                Db(db,"UPDATE sessions SET status='completed',updated_at="+(++stamp));e.Poll();Check(alerts==3,"completion after pending transition not suppressed as duplicate");
            }
            using(var e=new WorkBuddyEngine(wb,state)){e.Completed+=items=>alerts+=items.Count;e.Poll();Check(alerts==3&&e.Snapshot().Single().Status=="completed","WorkBuddy restart preserves completed row without duplicate");e.ClearFinished();Check(e.Snapshot().Count==0&&WorkBuddySource.Read(db).Count==1,"WorkBuddy clear affects only widget");}
            string ch=Path.Combine(root,"hub-codex"),cd=Path.Combine(ch,"sessions","2026","01","01");MakeSession(cd,id,false,"task_started");Db(db,"UPDATE sessions SET status='working',updated_at="+(++stamp));
            using(var hub=new MonitoringHub(ch,wb,Path.Combine(root,"hub-state"),true,true)){int hubAlerts=0;hub.Completed+=items=>hubAlerts+=items.Count;hub.Poll();Check(hub.Snapshot().Count==2&&hub.Snapshot().Select(s=>s.Key).Distinct().Count()==2,"same session ID across platforms stays separate");hub.Configure(true,false);hub.Poll();Check(hub.Snapshot().Count==1&&hub.Snapshot()[0].Platform=="codex","unchecked WorkBuddy hides its sessions");Db(db,"UPDATE sessions SET status='completed',updated_at="+(++stamp));hub.Poll();Check(hubAlerts==0,"unchecked platform cannot notify");hub.Configure(true,true);hub.Poll();Check(hubAlerts==0,"re-enabling platform does not replay paused notifications");hub.Configure(false,false);hub.Poll();Check(hub.Snapshot().Count==0&&hub.Health.Contains("平台"),"all platforms unchecked gives clear empty state");}
            Check(WorkBuddySource.DeepLink(id)=="workbuddy://chat/"+id,"WorkBuddy opens correct local chat URI");
            bool invalid=false;try{WorkBuddySource.DeepLink("id?prompt=bad");}catch(ArgumentException){invalid=true;}Check(invalid,"WorkBuddy rejects unsafe link identifiers");
            var now=DateTime.UtcNow;var running=new Session{StartedAt=now.AddSeconds(-208),Status="running"};Check(running.RuntimeText(now)=="03:28","running timer displays minutes and seconds");running.StartedAt=now.AddSeconds(-3808);Check(running.RuntimeText(now)=="01:03:28","timer displays total hours");running.Status="completed";running.FinishedAt=now;Check(running.RuntimeText(now.AddHours(4))=="01:03:28","completed timer freezes");running.StartedAt=now.AddSeconds(1);running.FinishedAt=DateTime.MinValue;running.Status="running";Check(running.RuntimeText(now)=="00:00","clock skew cannot show negative timer");
            Check(Json.Serializer().Deserialize<Settings>("{\"Sound\":false}").MonitorWorkBuddy,"old settings inherit new platform defaults");
        }
        static int passed; static string root;
        static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
        static string Event(string kind,string turn){return Json.Serializer().Serialize(new{timestamp=DateTime.UtcNow.ToString("O"),type="event_msg",payload=new{type=kind,turn_id=turn,started_at=DateTime.UtcNow.AddMinutes(-1).ToString("O")}});}
        static string MakeSession(string dir,string id,bool child,string kind){Directory.CreateDirectory(dir);string p=Path.Combine(dir,"rollout-2026-01-01T00-00-00-"+id+".jsonl");string meta=Json.Serializer().Serialize(new{type="session_meta",payload=new{id=id,cwd=@"D:\项目\测试",parent_thread_id=child?"parent":"",source="vscode",originator="Codex Desktop"}});File.WriteAllText(p,meta+"\n"+Event(kind,"t1")+"\n",new UTF8Encoding(false));return p;}
        static void Append(string file,string text){File.AppendAllText(file,text,new UTF8Encoding(false));Thread.Sleep(100);}
        public static int Main(){try{Console.OutputEncoding=Encoding.UTF8;root=Path.Combine(Path.GetTempPath(),"CodexFloatTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var s=new Session();Lifecycle.Parse(Event("task_started","a")).Apply(s);Check(s.Status=="running"&&s.Visible,"start activates session");
            Lifecycle.Parse(Event("task_complete","old")).Apply(s);Check(s.Status=="running","old turn completion cannot finish current turn");
            Lifecycle.Parse(Event("task_complete","a")).Apply(s);Check(s.Status=="completed","matching completion becomes green");
            Lifecycle.Parse(Event("task_started","a")).Apply(s);Check(s.Status=="completed","duplicate start cannot resurrect completed turn");
            Lifecycle.Parse(Event("task_started","b")).Apply(s);Check(s.Status=="running"&&s.TurnId=="b","follow-up restarts same session");
            Lifecycle.Parse(Event("turn_aborted","b")).Apply(s);Check(s.Status=="interrupted","stop is not completion");
            Check(Lifecycle.Parse("not json")==null&&Lifecycle.Parse("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\"}}") == null,"unrelated output ignored");
            string valid="11111111-1111-4111-8111-111111111111";Check(Rollout.DeepLink(valid)=="codex://threads/"+valid,"validated Codex deep link");bool rejected=false;try{Rollout.DeepLink("a?evil=1");}catch(ArgumentException){rejected=true;}Check(rejected,"reject malformed deep link ID");
            string stream=Path.Combine(root,"incremental.jsonl");byte[] data=Encoding.UTF8.GetBytes("中文\nsecond\n");File.WriteAllBytes(stream,data.Take(2).ToArray());var reader=new IncrementalReader();var lines=new List<string>();reader.Read(stream,lines.Add,10000);Check(lines.Count==0,"partial UTF-8 retained");using(var f=new FileStream(stream,FileMode.Append))f.Write(data,2,data.Length-2);reader.Read(stream,lines.Add,10000);Check(lines.SequenceEqual(new[]{"中文","second"}),"split UTF-8 and newline reconstructed");reader.Read(stream,lines.Add,10000);Check(lines.Count==2,"no duplicate append reads");File.WriteAllText(stream,"new\n");reader.Read(stream,lines.Add,10000);Check(lines.Last()=="new","truncated file resets offset");
            string home=Path.Combine(root,"home"),dir=Path.Combine(home,"sessions","2025","01","01"),state=Path.Combine(root,"state");Directory.CreateDirectory(state);
            string p1=MakeSession(dir,valid,false,"task_started"),p2=MakeSession(dir,"22222222-2222-4222-8222-222222222222",true,"task_started");
            string p3=MakeSession(dir,"33333333-3333-4333-8333-333333333333",false,"task_complete");
            var hydrate=Rollout.Identity(p1);Rollout.Hydrate(hydrate);Check(hydrate.Status=="running"&&!hydrate.IsChild,"hydrate active old-date session");Check(Rollout.Identity(p2).IsChild,"exclude internal subagent");
            File.WriteAllText(Path.Combine(home,"session_index.jsonl"),Json.Serializer().Serialize(new{id=valid,thread_name="真实中文标题"})+"\n",new UTF8Encoding(false));
            int notices=0;using(var engine=new Engine(home,state)){engine.Completed+=items=>notices+=items.Count;engine.Poll();Check(engine.Snapshot().Count==1&&engine.Snapshot()[0].Title=="真实中文标题","discover running title; exclude child and historic complete");Check(notices==0,"startup does not replay old completion notifications");
                Append(p1,Event("task_complete","t1")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="completed"&&notices==1,"live completion emits one notification");engine.Poll();Append(p1,Event("task_complete","t1")+"\n");engine.Poll();Check(notices==1,"duplicate completion notification suppressed");
                engine.ClearFinished();Check(engine.Snapshot().Count==0,"clear finished leaves source intact");Check(File.Exists(p1),"clear does not delete Codex log");
                Append(p1,Event("task_started","t2")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="running","dismissed session auto-rejoins on next turn");
                Append(p1,Event("turn_aborted","t2")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="interrupted"&&notices==1,"interruption shown without completion notification");
                string p4=MakeSession(dir,"44444444-4444-4444-8444-444444444444",false,"task_started");Thread.Sleep(100);engine.Poll();Check(engine.Snapshot().Any(x=>x.FilePath==p4&&x.Status=="running"),"watcher discovers new file in an old date folder");
                Append(p4,Event("task_complete","t1")+"\n");engine.Poll();Check(notices==2,"newly discovered session completion");}
            using(var engine=new Engine(home,state)){engine.Completed+=items=>notices+=items.Count;engine.Poll();Check(engine.Snapshot().Any(x=>x.Status=="completed")&&notices==2,"restart preserves completed rows without duplicate alert");}
            string repeatHome=Path.Combine(root,"repeat-home"),repeatDir=Path.Combine(repeatHome,"sessions","2024","01","01"),repeatState=Path.Combine(root,"repeat-state");
            string repeatFile=MakeSession(repeatDir,valid,false,"task_started");int repeatNotices=0;
            using(var engine=new Engine(repeatHome,repeatState)){
                engine.Completed+=items=>repeatNotices+=items.Count;engine.Poll();
                Append(repeatFile,Event("task_complete","t1")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="completed"&&repeatNotices==1,"round 1 completes and alerts");
                Append(repeatFile,Event("task_started","t2")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="running"&&engine.Snapshot().Single().TurnId=="t2","completed row returns to spinner without clearing");
                Append(repeatFile,Event("task_complete","t1")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="running"&&repeatNotices==1,"late prior-round end cannot stop new spinner");
                Append(repeatFile,Event("task_complete","t2")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="completed"&&repeatNotices==2,"round 2 completes and alerts again");
            }
            using(var engine=new Engine(repeatHome,repeatState)){
                engine.Completed+=items=>repeatNotices+=items.Count;engine.Poll();
                Append(repeatFile,Event("task_started","t3")+"\n");engine.Poll();Check(engine.Snapshot().Single().Status=="running","continued question detected after widget restart");
                Append(repeatFile,Event("task_complete","t3")+"\n");engine.Poll();Check(repeatNotices==3,"third round alerts after restart");
            }
            var partial=Rollout.Identity(p3);Append(p3,"{\"type\":");long offset=Rollout.Hydrate(partial);Check(partial.Status=="completed"&&offset<new FileInfo(p3).Length,"hydration ignores partial trailing JSON");
            WorkBuddyChecks();ClaudeChecks();ClaudeDesktopChecks();Console.WriteLine("ALL "+passed+" CHECKS PASSED\nFixtures: "+root);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    }
}
