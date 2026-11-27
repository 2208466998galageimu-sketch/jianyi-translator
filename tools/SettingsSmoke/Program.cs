
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Thanslator.App;
using Thanslator.Core;
using Thanslator.Storage;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length==2 && args[0]=="startup-seed")
        {
            var repository=new SettingsRepository(args[1]);repository.InitializeAsync().GetAwaiter().GetResult();
            repository.SaveAsync(new AppSettings{Enabled=false,SourceLang="ja",TargetLang="en",TranslationMode=SentenceTranslationMode.LlmLocal,
                LocalMtEnabled=true,LocalMtModelPath=Path.Combine(Path.GetDirectoryName(args[1])!,"missing-local-model")}).GetAwaiter().GetResult();return;
        }
        if (args.Length == 3 && args[0] == "llm-check") { LlmChecks.RunAsync(args[1],args[2]).GetAwaiter().GetResult(); return; }
        if (args.Length == 4 && args[0] == "mt-matrix") { LocalMtMatrix.RunAsync(args[1],args[2],args[3]).GetAwaiter().GetResult(); return; }
        if (args.Length == 3 && args[0] == "audit-db") { DatabaseAudit.RunAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
        if (args.Length == 4 && args[0] == "ocr-compare") { OcrComparison.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult(); return; }
        if (args.Length == 1 && args[0] == "serve") { using var server = new ModelFixture(int.Parse(Environment.GetEnvironmentVariable("OLLAMA_HOST")!.Split(':').Last()), Environment.GetEnvironmentVariable("OLLAMA_MODELS")); Thread.Sleep(Timeout.Infinite); return; }
        Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(Environment.ProcessPath));
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("THANSLATOR_HOME", Path.Combine(output, "data"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Exit += (_, _) => Thanslator.Translation.ManagedOllama.Shared.Dispose();
        app.Startup += async (_, _) =>
        {
            MoreSettingsWindow? window = null;
            try
            {
                using var fixture = new ModelFixture();
                var repository = new SettingsRepository(AppPaths.SettingsDatabase);
                AppPaths.EnsureDirectories();
                await repository.InitializeAsync();
                await new KnowledgeRepository(AppPaths.SettingsDatabase).InitializeAsync();
                var settings = await repository.LoadAsync();
                settings.OllamaRuntimePath = Path.Combine(AppContext.BaseDirectory, "SettingsSmoke.exe");

                if (typeof(MoreSettingsWindow).Assembly.GetType("Thanslator.App.KnowledgeWindow") is not null ||
                    typeof(MoreSettingsWindow).Assembly.GetType("Thanslator.App.LocalMtSettingsWindow") is not null)
                    throw new Exception("Unreachable legacy windows are still included.");
                var appDocument = System.Xml.Linq.XDocument.Load(Path.Combine(AppPaths.RepositoryRoot!, "src/Thanslator.App/App.xaml"));
                System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resourceXml = new System.Xml.Linq.XElement(presentation + "ResourceDictionary",
                    new System.Xml.Linq.XAttribute("xmlns", presentation.NamespaceName),
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    appDocument.Root!.Element(presentation + "Application.Resources")!.Elements());
                using (var xmlReader = resourceXml.CreateReader())
                    app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(xmlReader);
                var popper = new PopperWindow(settings, draft => repository.SaveAsync(draft));
                var settingsButton = (System.Windows.Controls.Button)popper.FindName("SettingsButton");
                var dockButton = (System.Windows.Controls.Button)popper.FindName("DockButton");
                if (System.Windows.Controls.Grid.GetColumn(settingsButton) != 2 || System.Windows.Controls.Grid.GetColumn(dockButton) != 3)
                    throw new Exception("Control bar button positions were not swapped.");
                settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var scroll = (System.Windows.Controls.ScrollViewer)popper.FindName("SettingsScroll");
                if (scroll.Visibility != Visibility.Visible) throw new Exception("Settings button did not open compact settings.");
                var settingsPage = (System.Windows.Controls.StackPanel)popper.FindName("SettingsPage");
                var more = settingsPage.Children.OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "更多设置"));
                var opened = false; popper.MoreSettingsRequested += (_, _) => opened = true;
                more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (!opened || popper.FindName("OcrShortWordsInput") is not null)
                    throw new Exception("More settings entry or old settings replacement failed.");
                popper.Close();
                if (args.Contains("quick-translation"))
                {
                    await QuickTranslationChecks.RunAsync(output, fixture.Address, repository);
                    app.Shutdown(0);
                    return;
                }
                File.WriteAllText(Path.Combine(output, "popper-passed.txt"), "Settings and dock positions, Settings > More Settings entry, and legacy control removal passed.");

                using var settingsGate = new SemaphoreSlim(1, 1);
                window = new MoreSettingsWindow(settings,
                    async (draft, _) => { await repository.SaveAsync(draft); settings.LlmApi = draft.LlmApi; settings.LlmLocal = draft.LlmLocal; settings.TranslationMode = draft.TranslationMode; settings.SourceLang = draft.SourceLang; settings.TargetLang = draft.TargetLang; settings.ManagedLlmStorageRoot = draft.ManagedLlmStorageRoot; settings.ModelDownloadDirectory = draft.ModelDownloadDirectory; settings.OllamaRuntimePath = draft.OllamaRuntimePath; settings.LocalMtEnabled=draft.LocalMtEnabled; settings.LocalMtModelPath=draft.LocalMtModelPath; settings.OcrComponentPath=draft.OcrComponentPath; },
                    draft => repository.SaveAsync(draft), () => Task.CompletedTask, async commit =>
                    {
                        var loaded = await commit();
                        settings.ModelDownloadDirectory = loaded.ModelDownloadDirectory;
                        settings.ManagedLlmStorageRoot = loaded.ManagedLlmStorageRoot;
                        settings.DwellMs = loaded.DwellMs;
                        settings.TranslationMode = loaded.TranslationMode;
                        settings.SourceLang = loaded.SourceLang;
                        settings.TargetLang = loaded.TargetLang;
                        settings.LlmLocal = loaded.LlmLocal;
                        settings.OllamaRuntimePath = loaded.OllamaRuntimePath;
                        settings.LocalMtEnabled = loaded.LocalMtEnabled;
                        settings.LocalMtModelPath = loaded.LocalMtModelPath;
                        settings.OcrComponentPath = loaded.OcrComponentPath;
                    }, gate: settingsGate);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Left = -20000;
                window.Top = 0;

                var view = (WebView2)typeof(MoreSettingsWindow).GetField("_view", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                view.CoreWebView2InitializationCompleted += (_, _) => {
                    view.CoreWebView2.NavigationStarting += (_, args) => File.AppendAllText(Path.Combine(output, "navigation.txt"), "start " + args.Uri + " cancel=" + args.Cancel + "\n");
                    view.CoreWebView2.NavigationCompleted += (_, args) => File.AppendAllText(Path.Combine(output, "navigation.txt"), "done " + args.IsSuccess + " " + args.WebErrorStatus + "\n");
                };
                window.Show();
                for (var i = 0; i < 150; i++)
                {
                    if (view.CoreWebView2 is not null &&
                        await view.ExecuteScriptAsync("typeof nativeBusy!=='undefined' && !nativeBusy") == "true") break;
                    await Task.Delay(100);
                }
                if (view.CoreWebView2 is null) throw new Exception("WebView did not initialize.");
                File.WriteAllText(Path.Combine(output, "window.txt"), window.Content?.GetType().FullName + " " + (window.Content as System.Windows.Controls.TextBlock)?.Text + " " + string.Join(",",typeof(MoreSettingsWindow).Assembly.GetManifestResourceNames()));
                File.WriteAllText(Path.Combine(output, "document.json"), await view.ExecuteScriptAsync("JSON.stringify({url:location.href,html:document.documentElement.outerHTML,busy:typeof nativeBusy})"));
                if (args.Length > 1 && args[1] == "drag-check")
                {
                    window.Title = "简译 · 拖动检查";
                    window.Left = 80; window.Top = 80;
                    File.WriteAllText(Path.Combine(output, "drag-position.json"), JsonSerializer.Serialize(new { window.Left, window.Top }));
                    window.LocationChanged += (_, _) => File.WriteAllText(Path.Combine(output, "drag-position.json"), JsonSerializer.Serialize(new { window.Left, window.Top }));
                    window.ShowInTaskbar = true;
                    window.Activate();
                    var finished = new TaskCompletionSource();
                    window.Closed += (_, _) => finished.TrySetResult();
                    await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromMinutes(4)));
                    if (!finished.Task.IsCompleted) window.Close();
                    app.Shutdown(0); return;
                }
                await OverlayChecks.RunAsync(output);
                if(args.Contains("real-models"))await NativeImportChecks.RunAsync(window,output,settings);
                await view.ExecuteScriptAsync("window.realModelCase="+(args.Contains("real-models")&&!args.Contains("ocr-only")?"true":"false"));
                await view.ExecuteScriptAsync("window.fixtureAddress=" + JsonSerializer.Serialize(fixture.Address));
                await view.ExecuteScriptAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflow.js")));
                string? result = null;
                for (var i = 0; i < 400; i++)
                {
                    var value = await view.ExecuteScriptAsync("window.smokeResult||null");
                    if (value != "null") { result = value; break; }
                    await Task.Delay(100);
                }
                File.WriteAllText(Path.Combine(output, "result.json"), result ?? "{\"error\":\"timeout\"}");
                if(result is not null&&JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean())await NativeDataChecks.RunAsync(window,output,settings);
                await WindowFrameChecks.RunAsync(window, view, output);
                await view.ExecuteScriptAsync("openPage('preferences')");await Task.Delay(100);
                using(var preferenceImage=File.Create(Path.Combine(output,"preferences.png")))
                    await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,preferenceImage);
                foreach(var page in new[]{"knowledge","combinations","models/translation","models/ocr","storage","backup","retention"})
                {
                    await view.ExecuteScriptAsync("openNativePage("+JsonSerializer.Serialize(page)+")");await Task.Delay(150);
                    using var pageImage=File.Create(Path.Combine(output,page.Replace('/','-')+".png"));
                    await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,pageImage);
                }
                using var screenshot = File.Create(Path.Combine(output, "more-settings.png"));
                await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                if (result is null || !JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean())
                    throw new Exception(result ?? "workflow timeout");

                var originalRoot = AppPaths.ModelsDirectory;
                if (!File.Exists(Path.Combine(originalRoot, "llm", "fixture-model.txt")))
                    throw new Exception("Default model download did not use the app data folder.");
                var customRoot = Path.Combine(output, "自选下载位置");
                settings.ModelDownloadDirectory = customRoot;
                await repository.SaveAsync(settings);
                var customAddress = await Thanslator.Translation.ManagedOllama.Shared.EnsureAsync(customRoot, settings.OllamaRuntimePath, CancellationToken.None);
                using (var http = Thanslator.Translation.LlmTranslationClient.CreateHttpClient(true))
                    await new Thanslator.Translation.LocalModelDownloads(http).DownloadAsync(customAddress, "qwen2.5:1.5b", null, CancellationToken.None);
                if (!File.Exists(Path.Combine(customRoot, "llm", "fixture-model.txt")) ||
                    !File.Exists(Path.Combine(originalRoot, "llm", "fixture-model.txt")) ||
                    (await repository.LoadAsync()).ModelDownloadDirectory != customRoot)
                    throw new Exception("Custom directory or existing download preservation failed.");
                File.WriteAllText(Path.Combine(output, "download-location-passed.txt"), "Default and custom download directories, persisted choice, and original files preserved.");

                var closed = false;
                window.Closed += (_, _) => closed = true;
                var savedShortSeconds = (await repository.LoadAsync()).OcrShortSeconds;
                await settingsGate.WaitAsync();
                try
                {
                    var unsavedSeconds = savedShortSeconds == 42 ? 43 : 42;
                    await view.ExecuteScriptAsync($$$"""
                        nativeCall('retention',{values:{shortWords:10,mediumWords:50,shortSeconds:{{{unsavedSeconds}}},mediumSeconds:300,longSeconds:600}}).catch(()=>{});
                        setTimeout(()=>document.querySelector('[data-window=close]').click(),100);
                        """);
                    for (var i = 0; i < 100 && !closed; i++) await Task.Delay(20);
                }
                finally { settingsGate.Release(); }
                if (!closed) throw new Exception("Native close button failed.");
                if (!await settingsGate.WaitAsync(TimeSpan.FromSeconds(2)))
                    throw new Exception("Closing the window left a settings operation stuck.");
                settingsGate.Release();
                if ((await repository.LoadAsync()).OcrShortSeconds != savedShortSeconds)
                    throw new Exception("A queued setting was saved after the window closed.");
                File.WriteAllText(Path.Combine(output, "close-passed.txt"), "The in-page close button closed the native window and discarded the queued save.");
                File.WriteAllText(Path.Combine(output, "passed.txt"), "Native WebView settings workflow passed.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
                window?.Close();
                app.Shutdown(1);
            }
        };
        app.Run();
    }
}











