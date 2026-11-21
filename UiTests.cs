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
    static void Click(ButtonBase button){if(button==null)throw new Exception("Button missing");button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));}
    static T Field<T>(MainWindow window,string name){
        return (T)typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
    }
    static void PlatformFilterChecks(MainWindow window){
        var bar=Field<WrapPanel>(window,"platformFilterBar");
        var buttons=bar.Children.OfType<ToggleButton>().ToDictionary(b=>(string)b.Content);
        Check(buttons.Keys.OrderBy(c=>c).SequenceEqual(new[]{"全部","Antigravity","Claude","Codex","WorkBuddy"}.OrderBy(c=>c)) &&
            buttons.Values.All(b=>b.Visibility==Visibility.Visible) && buttons["全部"].IsChecked==true,
            "current tasks show all enabled platform buttons and select All initially");
        Check(buttons.Values.Select(b=>b.TranslatePoint(new Point(0,0),bar).Y).Distinct().Count()==1,
            "all five filters fit on one row at the minimum window width");
        Click(buttons["Claude"]);
        Check(Find(window,"完成工具配置，")!=null&&Find(window,"整理项目资料，")==null&&Find(window,"检查页面布局，")==null,
            "platform filter shows only the chosen provider");
        Click(Field<ToggleButton>(window,"recentTab"));
        Check(bar.Visibility==Visibility.Collapsed&&Find(window,"检查页面布局，")!=null&&Find(window,"运行构建与测试，")!=null,
            "completion history stays unfiltered and hides the current-task controls");
        Click(Field<ToggleButton>(window,"currentTab"));
        Check(buttons["Claude"].IsChecked==true&&Find(window,"整理项目资料，")==null,
            "returning to current tasks keeps the selected platform");
        var buildMenu=typeof(MainWindow).GetMethod("BuildMenu",BindingFlags.Instance|BindingFlags.NonPublic);
        var settings=(ContextMenu)buildMenu.Invoke(window,null);
        var claude=settings.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="Claude 桌面端 Code");
        claude.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(buttons["Claude"].Visibility==Visibility.Collapsed&&buttons["全部"].IsChecked==true&&
            Find(window,"完成工具配置，")==null&&Find(window,"整理项目资料，")!=null,
            "disabling the selected platform removes its button and returns to All");
        claude.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(buttons["Claude"].Visibility==Visibility.Visible&&Find(window,"完成工具配置，")!=null,
            "enabling a platform restores its button and current tasks");
        Click(buttons["WorkBuddy"]);
        Click(buttons["全部"]);
        Check(Find(window,"整理项目资料，")!=null&&Find(window,"运行构建与测试，")!=null,
            "All restores the full list");
    }
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
        set("InitialRecent",false);var currentDemo=new MainWindow{Left=100,Top=100,Width=330};currentDemo.RenderPreview(Path.Combine(root,"current.png"));
        PlatformFilterChecks(currentDemo);
        var runningMenu=Find(currentDemo,"整理项目资料，").ContextMenu.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="加入完成");Check(!runningMenu.IsEnabled,"running task cannot be manually marked complete");
        string manual=Path.Combine(root,"manual-archive");Directory.CreateDirectory(manual);set("DataDir",manual);set("Demo",false);
        var a=new Session{Id="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",TurnId="a1",Platform="codex",Title="Done A",Cwd=root,Visible=true,Status="completed",StartedAt=DateTime.UtcNow.AddMinutes(-2),FinishedAt=DateTime.UtcNow.AddMinutes(-1)};
        var b=a.Copy();b.Id="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";b.TurnId="b1";b.Title="Done B";b.Platform="workbuddy";
        Json.Save(Path.Combine(manual,"current-tasks.json"),new CurrentTasksDocument{SavedAt=DateTime.UtcNow,Sessions=new List<Session>{a,b}});
        var manualWindow=new MainWindow{Left=100,Top=100};var refresh=typeof(MainWindow).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic);refresh.Invoke(manualWindow,null);Layout(manualWindow);
        var archive=Find(manualWindow,"Done A，").ContextMenu.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="加入完成");Check(archive.IsEnabled,"completed conversation exposes join-completed action");archive.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(Find(manualWindow,"Done A，")==null&&Find(manualWindow,"Done B，")!=null,"right-click action removes only selected completed conversation");
        var hub=(MonitoringHub)typeof(MainWindow).GetField("engine",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(manualWindow);Check(hub.RecentCompleted().Single().Id==a.Id,"right-click action saves selected conversation in recent completed");
        var reopened=new MainWindow{Left=100,Top=100};refresh.Invoke(reopened,null);Check(Find(reopened,"Done A，")==null&&Find(reopened,"Done B，")!=null,"single-row removal survives new window restore");
        var c=a.Copy();c.Id="cccccccc-cccc-4ccc-8ccc-cccccccccccc";c.TurnId="c1";c.Title="Done C";
        string bulk=Path.Combine(root,"filtered-clear");set("DataDir",bulk);
        Json.Save(Path.Combine(bulk,"current-tasks.json"),new CurrentTasksDocument{SavedAt=DateTime.UtcNow,Sessions=new List<Session>{b,c}});
        var bulkWindow=new MainWindow{Left=100,Top=100};refresh.Invoke(bulkWindow,null);
        var filters=Field<Dictionary<string,ToggleButton>>(bulkWindow,"platformFilters");
        Click(filters["codex"]);
        Click(Field<Button>(bulkWindow,"clear"));
        Click(filters[""]);
        Check(Find(bulkWindow,"Done C，")==null&&Find(bulkWindow,"Done B，")!=null,
            "Clear completed while filtered leaves completed tasks on other platforms");
        Console.WriteLine("ALL "+passed+" UI CHECKS PASSED\nEvidence: "+root);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
