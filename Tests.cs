using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
namespace CodexFloat {
    static class Tests {
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
            Console.WriteLine("ALL "+passed+" CHECKS PASSED\nFixtures: "+root);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    }
}
