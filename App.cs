using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("一泽 Codex 监控")]
[assembly: System.Reflection.AssemblyProduct("CodexFloat")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
namespace CodexFloat {
    static class Program {
        public static string DataDir,Home,CapturePath="",EvidenceDir="";public static bool Demo,DebugTaskbar,PreviewSettings;
        static Mutex mutex;static EventWaitHandle showEvent;
        public static string Arg(string[] args,string key,string fallback){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
        [STAThread] public static int Main(string[] args) {
            Home=Arg(args,"--home",Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"));
            DataDir=Arg(args,"--data-dir",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexFloat"));
            CapturePath=Arg(args,"--capture","");EvidenceDir=Arg(args,"--evidence-dir","");Demo=args.Contains("--demo");DebugTaskbar=args.Contains("--taskbar");PreviewSettings=args.Contains("--preview-settings");Directory.CreateDirectory(DataDir);
            if(args.Contains("--dump")) { using(var engine=new Engine(Home,DataDir)){engine.Poll();Json.Save(Arg(args,"--output",Path.Combine(DataDir,"snapshot.json")),new {health=engine.Health,sessions=engine.Snapshot()});}return 0; }
            string instance="Local\\CodexFloat-"+StableHash(Path.GetFullPath(DataDir).ToLowerInvariant());
            bool first;mutex=new Mutex(true,instance,out first);showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,instance+"-show");
            if(!first){showEvent.Set();mutex.Dispose();showEvent.Dispose();return 0;}
            try {
                var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
                app.DispatcherUnhandledException+=(o,e)=>{Log("ui",e.Exception);e.Handled=true;};
                var window=new MainWindow();app.MainWindow=window;
                ThreadPool.RegisterWaitForSingleObject(showEvent,(o,t)=>app.Dispatcher.BeginInvoke(new Action(window.ShowWidget)),null,-1,false);
                app.Run(window);return 0;
            }catch(Exception ex){Log("startup",ex);System.Windows.MessageBox.Show("启动失败："+ex.Message,"一泽 Codex 监控");return 1;}
            finally{mutex.ReleaseMutex();mutex.Dispose();showEvent.Dispose();}
        }
        static string StableHash(string s){uint hash=2166136261;foreach(char c in s)hash=(hash^c)*16777619;return hash.ToString("x");}
        public static void Log(string kind,Exception ex){try{File.AppendAllText(Path.Combine(DataDir,"errors.log"),DateTime.Now.ToString("s")+" "+kind+" "+ex+Environment.NewLine);}catch{}}
    }
    public static class Theme {
        public static SolidColorBrush B(string hex){var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));b.Freeze();return b;}
        public static readonly Brush Ink=B("#414540"),Muted=B("#969A94"),Green=B("#22844B"),Background=B("#F8F9F6"),Line=B("#E5E8E1");
        public static TextBlock Text(string value,double size,Brush color){return new TextBlock{Text=value,FontSize=size,Foreground=color,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};}
        public static Button Button(string text,string tip,double width){var b=new Button{Content=text,ToolTip=tip,Width=width,Height=28,Padding=new Thickness(0),Background=Brushes.Transparent,Foreground=Muted,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,FontSize=15};
            var t=new ControlTemplate(typeof(Button));var frame=new FrameworkElementFactory(typeof(Border));frame.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));frame.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(System.Windows.Controls.Button.BackgroundProperty));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);frame.AppendChild(content);t.VisualTree=frame;b.Template=t;
            b.MouseEnter+=(o,e)=>b.Background=B("#EBEEE7");b.MouseLeave+=(o,e)=>b.Background=Brushes.Transparent;return b;}
        public static ContextMenu Menu(){
            var menu=new ContextMenu{Width=228,Background=Background,Foreground=Ink,FontFamily=new FontFamily("Microsoft YaHei UI, Segoe UI"),FontSize=12,Padding=new Thickness(6),HasDropShadow=false};
            var template=new ControlTemplate(typeof(ContextMenu));
            var outer=new FrameworkElementFactory(typeof(Border));outer.SetValue(Border.MarginProperty,new Thickness(7));outer.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));outer.SetValue(Border.BackgroundProperty,Background);outer.SetValue(Border.BorderBrushProperty,Line);outer.SetValue(Border.BorderThicknessProperty,new Thickness(1));outer.SetValue(Border.PaddingProperty,new Thickness(5,6,5,6));outer.SetValue(Border.EffectProperty,new System.Windows.Media.Effects.DropShadowEffect{Color=Colors.Black,Opacity=.12,BlurRadius=12,ShadowDepth=2});
            outer.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));template.VisualTree=outer;menu.Template=template;
            var style=new Style(typeof(MenuItem));style.Setters.Add(new Setter(MenuItem.ForegroundProperty,Ink));style.Setters.Add(new Setter(MenuItem.FontSizeProperty,12.0));style.Setters.Add(new Setter(MenuItem.CursorProperty,Cursors.Hand));
            var rowTemplate=new ControlTemplate(typeof(MenuItem));var frame=new FrameworkElementFactory(typeof(Border),"MenuFrame");frame.SetValue(Border.BackgroundProperty,Brushes.Transparent);frame.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));frame.SetValue(Border.PaddingProperty,new Thickness(9,8,11,8));frame.SetValue(Border.MarginProperty,new Thickness(0,1,0,1));
            var dock=new FrameworkElementFactory(typeof(DockPanel));
            var mark=new FrameworkElementFactory(typeof(TextBlock),"CheckMark");mark.SetValue(TextBlock.TextProperty,"✓");mark.SetValue(TextBlock.WidthProperty,23.0);mark.SetValue(TextBlock.FontSizeProperty,13.0);mark.SetValue(TextBlock.ForegroundProperty,Green);mark.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);mark.SetValue(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center);mark.SetValue(UIElement.OpacityProperty,0.0);dock.AppendChild(mark);
            var header=new FrameworkElementFactory(typeof(ContentPresenter));header.SetValue(ContentPresenter.ContentSourceProperty,"Header");header.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);dock.AppendChild(header);frame.AppendChild(dock);rowTemplate.VisualTree=frame;
            var hover=new Trigger{Property=MenuItem.IsHighlightedProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,B("#E9EEE5"),"MenuFrame"));rowTemplate.Triggers.Add(hover);
            var check=new Trigger{Property=MenuItem.IsCheckedProperty,Value=true};check.Setters.Add(new Setter(UIElement.OpacityProperty,1.0,"CheckMark"));rowTemplate.Triggers.Add(check);
            var disabled=new Trigger{Property=MenuItem.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.5));rowTemplate.Triggers.Add(disabled);
            style.Setters.Add(new Setter(MenuItem.TemplateProperty,rowTemplate));menu.Resources[typeof(MenuItem)]=style;
            var sep=new Style(typeof(Separator));var st=new ControlTemplate(typeof(Separator));var line=new FrameworkElementFactory(typeof(Border));line.SetValue(Border.HeightProperty,1.0);line.SetValue(Border.BackgroundProperty,Line);line.SetValue(Border.MarginProperty,new Thickness(9,5,9,5));st.VisualTree=line;sep.Setters.Add(new Setter(Separator.TemplateProperty,st));menu.Resources[typeof(Separator)]=sep;
            return menu;
        }
    }
    public class StatusMark:FrameworkElement {
        readonly string state;readonly RotateTransform rotate=new RotateTransform();
        public StatusMark(string status){state=status;Width=16;Height=16;RenderTransformOrigin=new Point(.5,.5);if(status=="running"){
            RenderTransform=rotate;Loaded+=(o,e)=>rotate.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(.9)){RepeatBehavior=RepeatBehavior.Forever});
            Unloaded+=(o,e)=>rotate.BeginAnimation(RotateTransform.AngleProperty,null);}}
        protected override void OnRender(DrawingContext dc){base.OnRender(dc);
            if(state=="running"){
                dc.DrawEllipse(null,new Pen(Theme.B("#DEE2DB"),1.6),new Point(8,8),5.5,5.5);
                var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(new Point(8,2.5),false,false);c.ArcTo(new Point(2.5,8),new Size(5.5,5.5),0,true,SweepDirection.Clockwise,true,false);}g.Freeze();dc.DrawGeometry(null,new Pen(Theme.B("#758071"),1.8){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round},g);
            }else if(state=="completed"){
                dc.DrawEllipse(Theme.B("#E5F3E8"),null,new Point(8,8),8,8);var pen=new Pen(Theme.Green,1.8){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};dc.DrawLine(pen,new Point(4.3,8.1),new Point(6.9,10.6));dc.DrawLine(pen,new Point(6.9,10.6),new Point(11.8,5.4));
            }else{
                Brush color=state=="failed"?Theme.B("#BE5651"):Theme.B("#A58B5A");dc.DrawEllipse(null,new Pen(color,1.3),new Point(8,8),6.2,6.2);
                dc.DrawLine(new Pen(color,1.4),new Point(8,4.5),new Point(8,8.5));dc.DrawEllipse(color,null,new Point(8,11),.8,.8);
            }
        }
    }
    public class FolderMark:FrameworkElement {
        public FolderMark(){Width=16;Height=16;VerticalAlignment=VerticalAlignment.Center;}
        protected override void OnRender(DrawingContext dc){var g=Geometry.Parse("M 2,5 L 2,3.4 Q 2,2.4 3,2.4 L 6.7,2.4 L 8.5,4 L 13,4 Q 14,4 14,5 L 14,6 M 2,5 L 2,12 Q 2,13 3,13 L 12.4,13 Q 13,13 13.3,12 L 15,6.4 Q 15.2,5.5 14.2,5.5 L 3.7,5.5 L 2,11");dc.DrawGeometry(null,new Pen(Theme.B("#777E72"),1.1){LineJoin=PenLineJoin.Round},g);}
    }
    public class MainWindow:Window {
        readonly Settings settings;readonly Engine engine;
        readonly StackPanel list=new StackPanel();readonly TextBlock summary=Theme.Text("正在连接…",11,Theme.Muted);
        readonly ScrollViewer scroll=new ScrollViewer();readonly Border shell;readonly Button pin;
        readonly Forms.NotifyIcon tray;readonly DispatcherTimer timer;readonly CancellationTokenSource cancel=new CancellationTokenSource();
        string signature="";bool exiting;bool ready;ToastWindow toast;DateTime lastStatusExport;
        List<Session> latest=new List<Session>();readonly DateTime demoTime=DateTime.UtcNow;
        public MainWindow(){
            settings=LoadSettings();engine=new Engine(Program.Home,Program.DataDir);
            Title=Program.Demo?"一泽 Codex 样式预览（非实时）":"一泽 Codex 监控";FontFamily=new FontFamily("Microsoft YaHei UI, Segoe UI");FontSize=13;
            Width=Math.Max(330,Math.Min(640,settings.Width));SizeToContent=SizeToContent.Height;MinHeight=140;MaxHeight=620;
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=settings.AlwaysOnTop;ShowInTaskbar=Program.DebugTaskbar;
            UseLayoutRounding=true;SnapsToDevicePixels=true;TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
            var outer=new Grid{Margin=new Thickness(12)};Content=outer;
            shell=new Border{Background=Theme.Background,BorderBrush=Theme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(11),Effect=new System.Windows.Media.Effects.DropShadowEffect{Color=Colors.Black,Opacity=.12,BlurRadius=18,ShadowDepth=3}};outer.Children.Add(shell);
            var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});shell.Child=layout;
            var header=new Grid{Margin=new Thickness(16,10,10,9),Background=Brushes.Transparent};layout.Children.Add(header);
            header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var heading=new StackPanel{Orientation=Orientation.Horizontal};heading.Children.Add(new System.Windows.Shapes.Ellipse{Width=6,Height=6,Fill=Theme.Green,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center});
            var title=Theme.Text(Program.Demo?"样式预览 · 非实时":"一泽 Codex 监控",12,Theme.Ink);title.FontWeight=FontWeights.SemiBold;heading.Children.Add(title);header.Children.Add(heading);
            heading.MouseLeftButtonDown+=(o,e)=>{if(e.ClickCount==2){ToggleTopmost();return;}try{DragMove();SaveSettings();}catch{}};
            var controls=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetColumn(controls,1);header.Children.Add(controls);
            pin=Theme.Button("⌖","切换置顶",25);pin.Click+=(o,e)=>ToggleTopmost();controls.Children.Add(pin);
            var menu=Theme.Button("⋯","更多设置",26);menu.Click+=(o,e)=>OpenSettingsMenu(menu);controls.Children.Add(menu);
            var hide=Theme.Button("−","收起到系统托盘",25);hide.Click+=(o,e)=>Hide();controls.Children.Add(hide);
            scroll.Content=list;scroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;scroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;scroll.MaxHeight=440;scroll.Margin=new Thickness(10,0,10,4);Grid.SetRow(scroll,1);layout.Children.Add(scroll);
            var footer=new Grid{Margin=new Thickness(16,8,14,11)};footer.ColumnDefinitions.Add(new ColumnDefinition());footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});Grid.SetRow(footer,2);layout.Children.Add(footer);footer.Children.Add(summary);
            var clear=Theme.Button("清除完成","仅从悬浮列表移除已结束项",64);clear.FontSize=11;clear.Height=22;clear.Click+=(o,e)=>{engine.ClearFinished();Refresh();};Grid.SetColumn(clear,1);footer.Children.Add(clear);
            var grip=new Thumb{Width=10,Height=12,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Cursor=Cursors.SizeWE,Opacity=.15,Margin=new Thickness(0,0,3,3)};
            grip.DragDelta+=(o,e)=>{Width=Math.Max(330,Math.Min(640,Width+e.HorizontalChange));};grip.DragCompleted+=(o,e)=>SaveSettings();outer.Children.Add(grip);
            tray=new Forms.NotifyIcon{Icon=System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),Visible=true,Text="一泽 Codex 监控"};
            var tm=new Forms.ContextMenuStrip();tm.Items.Add("显示悬浮窗",null,(o,e)=>Dispatcher.BeginInvoke(new Action(ShowWidget)));tm.Items.Add("清除已完成",null,(o,e)=>Dispatcher.BeginInvoke(new Action(()=>{engine.ClearFinished();Refresh();})));tm.Items.Add(new Forms.ToolStripSeparator());tm.Items.Add("退出",null,(o,e)=>Dispatcher.BeginInvoke(new Action(Exit)));tray.ContextMenuStrip=tm;
            tray.DoubleClick+=(o,e)=>Dispatcher.BeginInvoke(new Action(ShowWidget));
            engine.Completed+=items=>Dispatcher.BeginInvoke(new Action(()=>OnCompleted(items)));
            Loaded+=(o,e)=>{PositionWindow();ready=true;UpdatePin();Refresh();if(!Program.Demo)StartWorker();if(Program.PreviewSettings)Dispatcher.BeginInvoke(new Action(()=>OpenSettingsMenu(menu)),DispatcherPriority.ContextIdle);};
            LocationChanged+=(o,e)=>{if(ready&&!exiting){settings.Left=Left;settings.Top=Top;}};
            Closing+=(o,e)=>{if(!exiting){e.Cancel=true;Hide();SaveSettings();}};
            timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};timer.Tick+=(o,e)=>{Refresh();if(Program.CapturePath!=""&&ready&&(!string.IsNullOrEmpty(signature))){Capture(Program.CapturePath);Program.CapturePath="";}};timer.Start();
        }
        Settings LoadSettings(){try{return Json.Serializer().Deserialize<Settings>(Json.SharedText(Path.Combine(Program.DataDir,"settings.json")))??new Settings();}catch{return new Settings();}}
        void SaveSettings(){settings.Left=Left;settings.Top=Top;settings.Width=Width;try{Json.Save(Path.Combine(Program.DataDir,"settings.json"),settings);}catch(Exception ex){Program.Log("settings",ex);}}
        void PositionWindow(){var area=SystemParameters.WorkArea;Left=settings.Left==-99999?area.Right-Width-24:settings.Left;Top=settings.Top==-99999?area.Top+60:settings.Top;
            bool reachable=Forms.Screen.AllScreens.Any(s=>{var src=PresentationSource.FromVisual(this);double scale=src!=null?src.CompositionTarget.TransformToDevice.M11:1;return s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle((int)(Left*scale),(int)(Top*scale),(int)(Width*scale),70));});
            if(!reachable){Left=area.Right-Width-24;Top=area.Top+60;}}
        void StartWorker(){Task.Run(async()=>{while(!cancel.IsCancellationRequested){try{engine.Poll();}catch(Exception ex){Program.Log("poll",ex);}try{await Task.Delay(800,cancel.Token);}catch(TaskCanceledException){break;}}});}
        void UpdatePin(){pin.Foreground=settings.AlwaysOnTop?Theme.Green:Theme.Muted;pin.ToolTip=settings.AlwaysOnTop?"已置顶 · 点击取消":"点击置顶";}
        void ToggleTopmost(){settings.AlwaysOnTop=!settings.AlwaysOnTop;Topmost=settings.AlwaysOnTop;UpdatePin();SaveSettings();}
        public void ShowWidget(){Show();WindowState=WindowState.Normal;PositionWindow();Activate();}
        void Exit(){exiting=true;cancel.Cancel();timer.Stop();SaveSettings();engine.Dispose();tray.Visible=false;tray.Dispose();if(toast!=null)toast.Close();Close();}
        void OpenSettingsMenu(Button button){button.ContextMenu=BuildMenu();button.ContextMenu.PlacementTarget=button;button.ContextMenu.Placement=PlacementMode.Bottom;button.ContextMenu.HorizontalOffset=-192;button.ContextMenu.VerticalOffset=3;
            if(Program.EvidenceDir!="")button.ContextMenu.Opened+=(o,e)=>Dispatcher.BeginInvoke(new Action(()=>{try{var cm=button.ContextMenu;cm.UpdateLayout();var bmp=new RenderTargetBitmap((int)Math.Ceiling(cm.ActualWidth*1.5),(int)Math.Ceiling(cm.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bmp.Render(cm);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));Directory.CreateDirectory(Program.EvidenceDir);using(var f=File.Create(Path.Combine(Program.EvidenceDir,"settings.png")))encoder.Save(f);}catch(Exception ex){Program.Log("menu-capture",ex);}}),DispatcherPriority.ContextIdle);
            button.ContextMenu.IsOpen=true;}
        ContextMenu BuildMenu(){var m=Theme.Menu();AddCheck(m,"始终置顶",settings.AlwaysOnTop,()=>ToggleTopmost());AddCheck(m,"完成时播放提示音",settings.Sound,()=>{settings.Sound=!settings.Sound;SaveSettings();});AddCheck(m,"完成时弹出提醒",settings.Notifications,()=>{settings.Notifications=!settings.Notifications;SaveSettings();});m.Items.Add(new Separator());
            var test=new MenuItem{Header="测试完成提醒"};test.Click+=(o,e)=>OnCompleted(new List<Session>{new Session{Title="提醒已开启",Cwd="一泽 Codex 监控",Status="completed"}});m.Items.Add(test);
            var reset=new MenuItem{Header="重置窗口位置"};reset.Click+=(o,e)=>{settings.Left=-99999;settings.Top=-99999;PositionWindow();SaveSettings();};m.Items.Add(reset);
            var startup=new MenuItem{Header="开机自动启动",IsCheckable=true,IsChecked=StartupEnabled()};startup.Click+=(o,e)=>SetStartup(startup.IsChecked);m.Items.Add(startup);
            m.Items.Add(new Separator());var about=new MenuItem{Header="关于 / 使用说明"};about.Click+=(o,e)=>MessageBox.Show(this,"一泽 Codex 监控 1.0.1\n\n新一轮开始会自动加入列表。\n绿色代表本轮结束，点击标题打开对应对话。\n中断不会标为完成；状态待确认时不会误报。\n完成项保留到手动清除。\n拖动顶部移动；右下角调整宽度。\n\n仅在本机读取会话元数据，无网络请求。\n配置与监控记录："+Program.DataDir+"\n\n项目：github.com/YizeAINative/yize-codex-monitor\n参考：github.com/LH-03/agent-monitor-hud", "关于一泽 Codex 监控");m.Items.Add(about);
            var exit=new MenuItem{Header="退出软件"};exit.Click+=(o,e)=>Exit();m.Items.Add(exit);return m;}
        void AddCheck(ContextMenu m,string name,bool check,Action action){var item=new MenuItem{Header=name,IsCheckable=true,IsChecked=check};item.Click+=(o,e)=>action();m.Items.Add(item);}
        bool StartupEnabled(){using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))return key!=null&&key.GetValue("CodexFloat")!=null;}
        void SetStartup(bool enabled){try{using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){if(enabled)key.SetValue("CodexFloat","\""+System.Reflection.Assembly.GetExecutingAssembly().Location+"\"");else key.DeleteValue("CodexFloat",false);}}catch(Exception ex){MessageBox.Show(this,ex.Message,"无法修改开机启动");}}
        List<Session> DemoSessions(){return new List<Session>{
            new Session{Id="11111111-1111-4111-8111-111111111111",Title="整理项目资料",Cwd=@"C:\Projects\research",Status="running",Visible=true,StartedAt=demoTime.AddMinutes(-8)},
            new Session{Id="22222222-2222-4222-8222-222222222222",Title="运行构建与测试",Cwd=@"C:\Projects\demo-app",Status="running",Visible=true,StartedAt=demoTime.AddMinutes(-12)},
            new Session{Id="33333333-3333-4333-8333-333333333333",Title="完成工具配置",Cwd=@"C:\Projects\assistant",Status="completed",Visible=true,StartedAt=demoTime.AddMinutes(-25),FinishedAt=demoTime}};}
        void Refresh(){latest=Program.Demo?DemoSessions():engine.Snapshot();string sig=string.Join("|",latest.OrderBy(s=>s.Id).Select(s=>s.Id+"/"+s.Label+"/"+s.Group+"/"+s.Status+"/"+s.StartedAt.Ticks));
            int running=latest.Count(s=>s.Status=="running"),done=latest.Count(s=>s.Status=="completed"),other=latest.Count-running-done;
            summary.Text=latest.Count==0?(Program.Demo?"演示界面":engine.Health):running+" 个处理中"+(done>0?" · "+done+" 个完成":"")+(other>0?" · "+other+" 个待查看":"");summary.ToolTip="开始执行自动加入 · 绿色代表本轮结束";
            tray.Text="一泽 Codex 监控 · "+running+" 处理中 · "+done+" 完成";
            if(sig!=signature||list.Children.Count==0){signature=sig;BuildList();if(Program.EvidenceDir!=""&&ready)Dispatcher.BeginInvoke(new Action(()=>Capture(Path.Combine(Program.EvidenceDir,"window.png"))),DispatcherPriority.ContextIdle);}
            if((DateTime.UtcNow-lastStatusExport).TotalSeconds>=5){try{Json.Save(Path.Combine(Program.DataDir,"status.json"),new {updatedAt=DateTime.UtcNow,health=engine.Health,running=running,completed=done,sessions=latest});}catch{}lastStatusExport=DateTime.UtcNow;}
        }
        void BuildList(){double old=scroll.VerticalOffset;list.Children.Clear();
            if(latest.Count==0){var empty=new StackPanel{Margin=new Thickness(18,27,18,32)};var line=Theme.Text("等待新的任务",14,Theme.Ink);line.HorizontalAlignment=HorizontalAlignment.Center;empty.Children.Add(line);var sub=Theme.Text("在 Codex 开始执行后，会自动出现在这里",11,Theme.Muted);sub.HorizontalAlignment=HorizontalAlignment.Center;sub.Margin=new Thickness(0,10,0,0);empty.Children.Add(sub);list.Children.Add(empty);return;}
            foreach(var group in latest.GroupBy(s=>s.Cwd+"|"+s.Group).OrderByDescending(g=>g.Max(s=>s.StartedAt))){
                var project=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(10,11,10,5)};project.Children.Add(new FolderMark());var label=Theme.Text(group.First().Group,13,Theme.Ink);label.Margin=new Thickness(8,0,0,0);label.MaxWidth=Width-96;label.ToolTip=group.First().Cwd;project.Children.Add(label);list.Children.Add(project);
                foreach(var session in group.OrderByDescending(s=>s.StartedAt))list.Children.Add(Row(session));
            }scroll.ScrollToVerticalOffset(old);
        }
        UIElement Row(Session session){
            var button=new Button{Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch,Cursor=Cursors.Hand,ToolTip=session.Label+"\n"+StateText(session.Status)+" · "+Duration(session)+"\n点击打开对应对话",Margin=new Thickness(0,1,0,1)};
            System.Windows.Automation.AutomationProperties.SetName(button,session.Label+"，"+StateText(session.Status)+"，打开对话");
            button.ToolTipOpening+=(o,e)=>button.ToolTip=session.Label+"\n"+StateText(session.Status)+" · "+Duration(session)+"\n点击打开对应对话";
            var template=new ControlTemplate(typeof(Button));var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Stretch);template.VisualTree=presenter;button.Template=template;
            var border=new Border{CornerRadius=new CornerRadius(6),Background=session.Status=="completed"?Theme.B("#EFF6EE"):Brushes.Transparent,Padding=new Thickness(34,9,10,9)};button.Content=border;
            var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});border.Child=row;
            var title=Theme.Text(session.Label,13,Theme.B("#62665F"));title.Margin=new Thickness(0,0,10,0);row.Children.Add(title);
            var state=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(state,1);row.Children.Add(state);state.Children.Add(new StatusMark(session.Status));
            if(session.Status!="running"){var t=Theme.Text(session.Status=="completed"?"完成":session.Status=="interrupted"?"已中断":session.Status=="failed"?"失败":"待确认",11,session.Status=="completed"?Theme.Green:Theme.B("#A58B5A"));t.Margin=new Thickness(5,0,0,0);state.Children.Add(t);}
            button.MouseEnter+=(o,e)=>border.Background=session.Status=="completed"?Theme.B("#E5F1E3"):Theme.B("#ECEFE8");button.MouseLeave+=(o,e)=>border.Background=session.Status=="completed"?Theme.B("#EFF6EE"):Brushes.Transparent;
            button.Click+=(o,e)=>OpenSession(session);
            if(session.Status!="running"){var cm=Theme.Menu();var remove=new MenuItem{Header="从列表移除"};remove.Click+=(o,e)=>{engine.Dismiss(session.Id);Refresh();};cm.Items.Add(remove);button.ContextMenu=cm;}
            return button;
        }
        static string StateText(string state){return state=="running"?"处理中":state=="completed"?"本轮已完成":state=="interrupted"?"已中断":state=="failed"?"执行失败":"状态待确认";}
        static string Duration(Session s){if(s.StartedAt==DateTime.MinValue)return "";var span=(s.FinishedAt==DateTime.MinValue?DateTime.UtcNow:s.FinishedAt)-s.StartedAt;return span.TotalHours>=1?((int)span.TotalHours)+" 小时 "+span.Minutes+" 分钟":Math.Max(0,(int)span.TotalMinutes)+" 分钟";}
        public void OpenSession(Session session){if(Program.Demo||string.IsNullOrEmpty(session.Id))return;try{string uri=Rollout.DeepLink(session.Id);Process.Start(new ProcessStartInfo(uri){UseShellExecute=true});Json.Save(Path.Combine(Program.DataDir,"last-open.json"),new{threadId=session.Id,uri=uri,openedAt=DateTime.UtcNow});}catch(Exception ex){MessageBox.Show(this,"无法打开 Codex 对话，请确认桌面版已安装。\n\n"+ex.Message,"打开对话");}}
        void OnCompleted(List<Session> items){Refresh();items=items.Where(s=>s.Id==""||latest.Any(current=>current.Id==s.Id&&current.TurnId==s.TurnId&&current.Status=="completed")).ToList();if(items.Count==0)return;if(settings.Sound)Task.Run(()=>System.Media.SystemSounds.Asterisk.Play());if(!settings.Notifications)return;
            if(toast!=null){toast.Close();toast=null;}toast=new ToastWindow(items,OpenSession);toast.Show();try{Json.Save(Path.Combine(Program.DataDir,"last-notification.json"),new{at=DateTime.UtcNow,sound=settings.Sound,sessions=items.Select(s=>new{id=s.Id,turnId=s.TurnId,title=s.Label}).ToArray()});}catch{}}
        void Capture(string path){try{UpdateLayout();var bmp=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*1.5),(int)Math.Ceiling(ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bmp.Render(this);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bmp));Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));using(var f=File.Create(path))enc.Save(f);}catch(Exception ex){Program.Log("capture",ex);}}
    }
    public class ToastWindow:Window {
        public ToastWindow(List<Session> items,Action<Session> open){
            Title="Codex 完成提醒";Width=350;SizeToContent=SizeToContent.Height;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;ShowActivated=false;FontFamily=new FontFamily("Microsoft YaHei UI");
            var border=new Border{Margin=new Thickness(8),Padding=new Thickness(15,12,12,13),CornerRadius=new CornerRadius(10),Background=Theme.Background,BorderBrush=Theme.B("#C9DFCA"),BorderThickness=new Thickness(1),Effect=new System.Windows.Media.Effects.DropShadowEffect{Opacity=.13,BlurRadius=14,ShadowDepth=2}};Content=border;
            var stack=new StackPanel();border.Child=stack;var head=new DockPanel();stack.Children.Add(head);var close=Theme.Button("×","关闭提醒",24);close.Click+=(o,e)=>Close();DockPanel.SetDock(close,Dock.Right);head.Children.Add(close);var title=Theme.Text("✓  "+(items.Count==1?"本轮已完成":items.Count+" 个对话本轮已完成"),13,Theme.Green);title.FontWeight=FontWeights.SemiBold;head.Children.Add(title);
            foreach(var item in items.Take(4)){var s=item;var b=new Button{Content=Theme.Text(s.Label,12,Theme.Ink),HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0,8,0,3),Cursor=Cursors.Hand};b.Click+=(o,e)=>{open(s);Close();};stack.Children.Add(b);}
            Loaded+=(o,e)=>{var a=SystemParameters.WorkArea;Left=a.Right-Width-18;Top=a.Bottom-ActualHeight-18;if(Program.EvidenceDir!="")Dispatcher.BeginInvoke(new Action(()=>{try{UpdateLayout();var bmp=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*1.5),(int)Math.Ceiling(ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bmp.Render(this);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bmp));Directory.CreateDirectory(Program.EvidenceDir);using(var f=File.Create(Path.Combine(Program.EvidenceDir,"notification.png")))enc.Save(f);}catch(Exception ex){Program.Log("toast-capture",ex);}}),DispatcherPriority.ContextIdle);};var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(9)};timer.Tick+=(o,e)=>{timer.Stop();Close();};Closed+=(o,e)=>timer.Stop();timer.Start();
        }
    }
}
