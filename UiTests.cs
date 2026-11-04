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
        Console.WriteLine("ALL "+passed+" UI CHECKS PASSED\nEvidence: "+root);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
