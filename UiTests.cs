using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodexFloat;

class UiTests {
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
    static IEnumerable<Button> Buttons(DependencyObject root){
        var button=root as Button;if(button!=null)yield return button;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in Buttons(VisualTreeHelper.GetChild(root,i)))yield return item;
    }
    static Button Find(MainWindow window,string prefix){Layout(window);return Buttons((DependencyObject)window.Content).FirstOrDefault(b=>AutomationProperties.GetName(b).StartsWith(prefix,StringComparison.Ordinal));}
    static void Layout(MainWindow window){var content=(FrameworkElement)window.Content;content.Measure(new Size(window.Width,double.PositiveInfinity));content.Arrange(new Rect(0,0,window.Width,content.DesiredSize.Height));content.UpdateLayout();}
    static void Click(Button button){if(button==null)throw new Exception("Button missing");button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));}
    [STAThread] static int Main(){try{
        string root=Path.Combine(Path.GetTempPath(),"YizeUiTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var program=typeof(MainWindow).Assembly.GetType("CodexFloat.Program");Action<string,object> set=(key,value)=>program.GetField(key,BindingFlags.Static|BindingFlags.Public).SetValue(null,value);
        set("Home",Path.Combine(root,"codex"));set("WorkBuddyHome",Path.Combine(root,"workbuddy"));set("ClaudeHome",Path.Combine(root,"claude"));set("DataDir",root);set("Demo",true);set("RenderOnly",true);set("InitialRecent",true);
        var app=new Application();var window=new MainWindow{Left=100,Top=100};window.RenderPreview(Path.Combine(root,"expanded.png"));
        Check(Find(window,"整理项目资料，")!=null&&Find(window,"整理历史资料，")!=null,"recent folder initially shows its conversations");
        Click(Find(window,"research，"));Check(Find(window,"整理项目资料，")==null&&Find(window,"整理历史资料，")==null,"clicking folder header hides its conversation rows");
        Check(Find(window,"运行构建与测试，")!=null,"collapsing one folder leaves other folders visible");
        Check(AutomationProperties.GetName(Find(window,"research，")).Contains("已折叠"),"collapsed header exposes the correct accessible state");
        window.RenderPreview(Path.Combine(root,"collapsed.png"));Check(Find(window,"整理项目资料，")==null,"rerender keeps folder collapsed");
        Click(Find(window,"research，"));Check(Find(window,"整理项目资料，")!=null&&Find(window,"整理历史资料，")!=null,"clicking collapsed header expands its rows");
        Click(Find(window,"research，"));var second=new MainWindow{Left=100,Top=100};second.RenderPreview(Path.Combine(root,"restarted.png"));Check(Find(second,"整理项目资料，")==null&&AutomationProperties.GetName(Find(second,"research，")).Contains("已折叠"),"new window restores persisted collapsed folder");
        set("InitialRecent",false);var currentDemo=new MainWindow{Left=100,Top=100};currentDemo.RenderPreview(Path.Combine(root,"current.png"));
        var runningMenu=Find(currentDemo,"整理项目资料，").ContextMenu.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="加入完成");Check(!runningMenu.IsEnabled,"running task cannot be manually marked complete");
        string manual=Path.Combine(root,"manual-archive");Directory.CreateDirectory(manual);set("DataDir",manual);set("Demo",false);
        var a=new Session{Id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",TurnId="a1",Platform="codex",Title="Done A",Cwd=root,Visible=true,Status="completed",StartedAt=DateTime.UtcNow.AddMinutes(-2),FinishedAt=DateTime.UtcNow.AddMinutes(-1)};
        var b=a.Copy();b.Id="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";b.TurnId="b1";b.Title="Done B";
        Json.Save(Path.Combine(manual,"current-tasks.json"),new CurrentTasksDocument{SavedAt=DateTime.UtcNow,Sessions=new List<Session>{a,b}});
        var manualWindow=new MainWindow{Left=100,Top=100};var refresh=typeof(MainWindow).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic);refresh.Invoke(manualWindow,null);Layout(manualWindow);
        var archive=Find(manualWindow,"Done A，").ContextMenu.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="加入完成");Check(archive.IsEnabled,"completed conversation exposes join-completed action");archive.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(Find(manualWindow,"Done A，")==null&&Find(manualWindow,"Done B，")!=null,"right-click action removes only selected completed conversation");
        var hub=(MonitoringHub)typeof(MainWindow).GetField("engine",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(manualWindow);Check(hub.RecentCompleted().Single().Id==a.Id,"right-click action saves selected conversation in recent completed");
        var reopened=new MainWindow{Left=100,Top=100};refresh.Invoke(reopened,null);Check(Find(reopened,"Done A，")==null&&Find(reopened,"Done B，")!=null,"single-row removal survives new window restore");
        Console.WriteLine("ALL "+passed+" UI CHECKS PASSED\nEvidence: "+root);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
