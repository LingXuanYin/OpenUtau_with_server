using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Controllers;
using Serilog;

namespace OpenUtau.App {
    public class Program {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitLogging();
            string processName = Process.GetCurrentProcess().ProcessName;
            if (processName != "dotnet") {
                var exists = Process.GetProcessesByName(processName).Count() > 1;
                if (exists) {
                    Log.Information($"Process {processName} already open. Exiting.");
                    return;
                }
            }
            Log.Information($"{Environment.OSVersion}");
            Log.Information($"{RuntimeInformation.OSDescription} " +
                $"{RuntimeInformation.OSArchitecture} " +
                $"{RuntimeInformation.ProcessArchitecture}");
            Log.Information($"OpenUtau v{Assembly.GetEntryAssembly()?.GetName().Version} " +
                $"{RuntimeInformation.RuntimeIdentifier}");
            Log.Information($"Data path = {PathManager.Inst.DataPath}");
            Log.Information($"Cache path = {PathManager.Inst.CachePath}");

            try {
                if (args.Contains("--server")) {
                    Console.WriteLine("Starting in HTTP server mode");
                    int port = 5000;
                    var portIndex = Array.IndexOf(args, "--port");
                    if (portIndex != -1 && portIndex + 1 < args.Length) {
                        if (int.TryParse(args[portIndex + 1], out int parsedPort)) {
                            port = parsedPort;
                        } else {
                            Console.WriteLine($"Invalid port number: {args[portIndex + 1]}, using default port 5000");
                        }
                    }

                    // 初始化必要的组件
                    Log.Information("Initializing OpenUtau HTTP Server.");
                    Classic.ToolsManager.Inst.Initialize();
                    SingerManager.Inst.Initialize();

                    Console.WriteLine("&&&&&&&&&&&&&&&");
                    Console.WriteLine(Thread.CurrentThread.ManagedThreadId);
                    Console.WriteLine(Thread.CurrentThread.ThreadState);
                    Console.WriteLine(Thread.CurrentThread.Name);
                    Console.WriteLine("&&&&&&&&&&&&&&&");
                    DocManager.Inst.Initialize(Thread.CurrentThread, TaskScheduler.Current);
                    DocManager.Inst.PostOnUIThread = action => action(); // 在服务器模式下直接执行
                    InitAudio();

                    var server = new HttpServer(port);
                    Console.WriteLine($"Server is running on port {port}");
                    Console.WriteLine("Type 'exit' and press Enter to stop the server...");

                    // 在新线程中启动服务器
                    //var serverThread = new Thread(() => {

                        server.Start();
                    //});
                    //serverThread.Start();

                    // 主线程等待用户输入
                    while (true) {
                        var input = Console.ReadLine();
                        if (input?.ToLower() == "exit") {
                            break;
                        }
                        Console.WriteLine("Type 'exit' and press Enter to stop the server...");
                        Console.WriteLine($"current server \n{server.ToString()}");
                        Thread.Sleep(1000);
                    }

                    // 停止服务器
                    server.Stop();
                    //serverThread.Join();
                } else {
                    Log.Information("Starting in GUI mode");
                    Run(args);
                }
                Log.Information($"Exiting.");
            } finally {
                if (!OS.IsMacOS()) {
                    NetMQ.NetMQConfig.Cleanup(/*block=*/false);
                    // Cleanup() hangs on macOS https://github.com/zeromq/netmq/issues/1018
                }
            }
            Log.Information($"Exited.");
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp() {
            FontManagerOptions fontOptions = new();
            if (OS.IsLinux()) {
                using Process process = Process.Start(new ProcessStartInfo("fc-match")
                {
                    ArgumentList = { "-f", "%{family}" },
                    RedirectStandardOutput = true
                })!;
                process.WaitForExit();

                string fontFamily = process.StandardOutput.ReadToEnd();
                if (!string.IsNullOrEmpty(fontFamily)) {
                    string [] fontFamilies = fontFamily.Split(',');
                    fontOptions.DefaultFamilyName = fontFamilies[0];
                }
            }
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI()
                .With(fontOptions)
                .With(new X11PlatformOptions {EnableIme = true});
        }

        public static void Run(string[] args)
            => BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(
                    args, ShutdownMode.OnMainWindowClose);

        public static void InitLogging() {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Debug()
                .WriteTo.Logger(lc => lc
                    .MinimumLevel.Information()
                    .WriteTo.File(PathManager.Inst.LogFilePath, rollingInterval: RollingInterval.Day, encoding: Encoding.UTF8))
                .WriteTo.Logger(lc => lc
                    .MinimumLevel.ControlledBy(DebugViewModel.Sink.Inst.LevelSwitch)
                    .WriteTo.Sink(DebugViewModel.Sink.Inst))
                .CreateLogger();
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler((sender, args) => {
                Log.Error((Exception)args.ExceptionObject, "Unhandled exception");
            });
            Log.Information("Logging initialized.");
        }
        private static void InitAudio() {
            Log.Information("Initializing audio.");
            if (!OS.IsWindows() || Core.Util.Preferences.Default.PreferPortAudio) {
                try {
                    PlaybackManager.Inst.AudioOutput = new Audio.MiniAudioOutput();
                } catch (Exception e1) {
                    Log.Error(e1, "Failed to init MiniAudio");
                }
            } else {
                try {
                    PlaybackManager.Inst.AudioOutput = new Audio.NAudioOutput();
                } catch (Exception e2) {
                    Log.Error(e2, "Failed to init NAudio");
                }
            }
            Log.Information("Initialized audio.");
        }


    }
}
