using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Thanslator.App;
using Thanslator.Core;
using Thanslator.Storage;
using Thanslator.Translation;

internal static class QuickTranslationChecks
{
    internal static async Task RunAsync(string output, string fixtureAddress, SettingsRepository repository)
    {
        var selection = typeof(PopperWindow).Assembly.GetType("Thanslator.App.TranslationSelection")!;
        var store = new MoreSettingsRepository(AppPaths.SettingsDatabase);
        var offline = Path.Combine(output, "offline-fixture");
        Directory.CreateDirectory(offline);
        foreach (var name in LocalMtModelInstaller.RequiredFiles)
            File.WriteAllText(Path.Combine(offline, name), name == "config.json" ? "{}" : "fixture");
        var settings = new AppSettings { LocalMtEnabled = true, LocalMtModelPath = offline, SourceLang = "en", OfflineMode = true };
        await repository.SaveAsync(settings);
        await store.SaveModelAsync("fixture-local", new() { ["id"] = "fixture-local", ["kind"] = "llm",
            ["address"] = fixtureAddress, ["model"] = "fixture-translation" });
        await store.SaveModelAsync("fixture-invalid", new() { ["id"] = "fixture-invalid", ["kind"] = "llm",
            ["address"] = "https://invalid.example", ["model"] = "fixture-unavailable" });
        await store.SaveModelAsync("fixture-cloud", new() { ["id"] = "fixture-cloud", ["kind"] = "api",
            ["address"] = "https://invalid.example", ["model"] = "fixture-cloud" });
        await store.SaveModelAsync("fixture-ocr", new() { ["id"] = "fixture-ocr", ["kind"] = "ocr" });

        var popper = new PopperWindow(settings, draft => repository.SaveAsync(draft));
        Func<Task<List<JsonObject>>> load = () => (Task<List<JsonObject>>)Invoke("LoadModelsAsync", store, settings, CancellationToken.None);
        async Task Save(AppSettings draft)
        {
            await repository.SaveAsync(draft);
            settings.TranslationMode = draft.TranslationMode;
            settings.LlmLocal = draft.LlmLocal;
            settings.SourceLang = draft.SourceLang;
            settings.TargetLang = draft.TargetLang;
            popper.SetTranslationContext([], "");
        }
        Set("LoadTranslationModels", load);
        Set("SelectTranslationEngine", (Func<string, Task>)(async id =>
        {
            var model = (await load()).Single(row => MoreSettingsKnowledge.Text(row, "id") == id);
            await Save(await (Task<AppSettings>)Invoke("ActivateAsync", model, settings, CancellationToken.None));
        }));
        Set("SelectTranslationLanguages", (Func<string, string, Task>)(async (source, target) =>
            await Save((AppSettings)Invoke("ChangeLanguages", settings, await load(), source, target))));

        popper.Show();
        popper.SetTranslationContext([], "");
        try
        {
            var engine = Find<Button>("TranslationEngineButton");
            Click(engine);
            await WaitAsync(() => Rows().Any(row => Equals(row.Tag, "fixture-local")));
            var rows = Rows();
            if (!rows.Any(row => Equals(row.Tag, "legacy-mt") && ((TextBlock)row.Header).Text.StartsWith("✓")) ||
                rows.Any(row => Equals(row.Tag, "fixture-ocr")) ||
                rows.Single(row => Equals(row.Tag, "fixture-cloud")).IsEnabled)
                throw new Exception("Engine menu did not show the active choice or respect the offline/data filters.");

            rows.Single(row => Equals(row.Tag, "fixture-invalid")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitAsync(() => Rows().Any(row => row.Visibility == Visibility.Visible &&
                row.Header is TextBlock label && label.Text.Contains("本机模式仅允许")));
            if (settings.TranslationMode != SentenceTranslationMode.LocalMt || !Menu().IsOpen)
                throw new Exception("Failed activation changed the engine or dismissed its error.");

            Rows().Single(row => Equals(row.Tag, "fixture-local")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitAsync(() => engine.IsEnabled && !Menu().IsOpen);
            if ((await repository.LoadAsync()).TranslationMode != SentenceTranslationMode.LlmLocal ||
                !Find<TextBlock>("TranslationEngineLabel").Text.Contains("本机 AI"))
                throw new Exception("The selected engine was not reflected in the WPF control and database.");

            Click(Find<Button>("TranslationLanguageButton"));
            var source = Find<ComboBox>("TranslationSourcePicker");
            await WaitAsync(() => Find<Popup>("TranslationLanguagePopup").IsOpen && source.IsEnabled && source.HasItems);
            source.SelectedValue = "ja";
            Find<ComboBox>("TranslationTargetPicker").SelectedValue = "en";
            Click(Find<Button>("TranslationLanguageApply"));
            await WaitAsync(() => !Find<Popup>("TranslationLanguagePopup").IsOpen);
            var saved = await repository.LoadAsync();
            if (saved.SourceLang != "ja" || saved.TargetLang != "en" || saved.TranslationMode != SentenceTranslationMode.LlmLocal)
                throw new Exception("The language picker did not save an independent direction.");
            File.WriteAllText(Path.Combine(output, "quick-translation-passed.txt"),
                "WPF selection: active check, hidden OCR, blocked cloud, retained engine on failure, local protocol activation, persistent language selection.");
        }
        finally { popper.Close(); }

        object Invoke(string name, params object[] arguments) =>
            selection.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
        void Set(string name, object value) =>
            typeof(PopperWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(popper, value);
        T Find<T>(string name) => (T)popper.FindName(name);
        ContextMenu Menu() => (ContextMenu)typeof(PopperWindow).GetField("_translationEngineMenu",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popper)!;
        MenuItem[] Rows() => Menu().Items.OfType<MenuItem>().ToArray();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Quick translation UI did not reach the expected state.");
    }
}
