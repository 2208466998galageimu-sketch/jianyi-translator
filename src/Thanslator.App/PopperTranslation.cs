using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Thanslator.Core;
using Thanslator.Translation;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Thanslator.App;

public partial class PopperWindow
{
    internal Func<Task<List<JsonObject>>>? LoadTranslationModels { get; set; }
    internal Func<string, Task>? SelectTranslationEngine { get; set; }
    internal Func<string, string, Task>? SelectTranslationLanguages { get; set; }

    private (string Source, string Target)[] _languagePairs = [];
    private bool _selectingTranslation;
    private ContextMenu? _translationEngineMenu;

    internal void UpdateTranslationButtons()
    {
        var languages = LanguageLabel(_settings.SourceLang, _settings.TargetLang);
        var engine = _settings.TranslationMode switch
        {
            SentenceTranslationMode.LlmLocal => AiLabel(_settings.LlmLocal.Model, "本机 AI"),
            SentenceTranslationMode.LlmApi => AiLabel(_settings.LlmApi.Model, "云端 AI"),
            _ => _settings.LocalMtEnabled ? "离线翻译" : "离线翻译未配置"
        };
        TranslationLanguageLabel.Text = languages;
        TranslationEngineLabel.Text = engine;
        TranslationLanguageButton.ToolTip = languages;
        TranslationEngineButton.ToolTip = engine + " · 用于通序翻译";
        AutomationProperties.SetName(TranslationLanguageButton, "翻译语言：" + languages);
        AutomationProperties.SetName(TranslationEngineButton, "翻译引擎：" + engine);
        KnowledgeComboPicker.Visibility = !_settings.LiteralMode && _settings.TranslationMode != SentenceTranslationMode.LocalMt
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string LanguageLabel(string source, string target) =>
        $"{TranslationLanguages.Label(source)} → {TranslationLanguages.Label(target)}";

    private static string AiLabel(string name, string kind) => name.Length == 0 ? kind : $"{name} · {kind}";

    private async void TranslationEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingTranslation || LoadTranslationModels is null || SelectTranslationEngine is null) return;
        TranslationLanguagePopup.IsOpen = false;
        if (_translationEngineMenu is { IsOpen: true } openMenu)
        {
            openMenu.IsOpen = false;
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = TranslationEngineButton,
            Placement = PlacementMode.Bottom,
            MinWidth = 182,
            MaxHeight = 320
        };
        _translationEngineMenu = menu;
        var feedback = TranslationMenuItem("正在读取已配置的模型…");
        feedback.IsEnabled = false;
        menu.Items.Add(feedback);
        menu.IsOpen = true;

        try
        {
            var models = (await LoadTranslationModels())
                .Where(model => MoreSettingsKnowledge.Text(model, "kind") is "mt" or "llm" or "api")
                .OrderBy(model => MoreSettingsKnowledge.Text(model, "kind") switch { "mt" => 0, "llm" => 1, _ => 2 }).ToArray();
            if (!menu.IsOpen) return;
            menu.Items.Clear();
            var current = models.FirstOrDefault(model => TranslationSelection.IsCurrent(model, _settings));
            var offlineCount = models.Count(model => MoreSettingsKnowledge.Text(model, "kind") == "mt");
            foreach (var model in models)
            {
                var kind = MoreSettingsKnowledge.Text(model, "kind");
                var id = MoreSettingsKnowledge.Text(model, "id");
                var active = ReferenceEquals(model, current);
                var label = kind == "mt" ? "离线翻译" :
                    AiLabel(MoreSettingsKnowledge.Text(model, "model"), kind == "llm" ? "本机 AI" : "云端 AI");
                if (kind == "mt")
                {
                    var pairs = TranslationSelection.OfflineLanguages(model);
                    if (pairs.Length == 0) continue;
                    var (source, target) = TranslationSelection.OfflineDirection(model, _settings);
                    if (offlineCount > 1)
                    {
                        var languages = pairs.SelectMany(pair => new[] { pair.Source, pair.Target }).Distinct().ToArray();
                        label += " · " + (languages.Length == 2
                            ? $"{TranslationLanguages.Label(languages[0])} ↔ {TranslationLanguages.Label(languages[1])}"
                            : $"{languages.Length} 种语言");
                    }
                    if (!active && (source, target) != (_settings.SourceLang, _settings.TargetLang))
                        label += $" · 切换为 {LanguageLabel(source, target)}";
                }
                else if (models.Count(row => MoreSettingsKnowledge.Text(row, "kind") == kind &&
                    MoreSettingsKnowledge.Text(row, "model") == MoreSettingsKnowledge.Text(model, "model")) > 1 &&
                    Uri.TryCreate(MoreSettingsKnowledge.Text(model, "address"), UriKind.Absolute, out var address))
                    label += " · " + address.Authority;

                var blocked = kind == "api" && _settings.OfflineMode;
                if (blocked) label += "（离线模式已开启）";
                var item = TranslationMenuItem((active ? "✓  " : "") + label);
                item.Tag = id;
                if (kind == "mt" && offlineCount > 1) item.ToolTip = MoreSettingsKnowledge.Text(model, "path");
                item.IsEnabled = !blocked;
                if (blocked) item.Opacity = 0.6;
                item.StaysOpenOnClick = !active;
                if (!active && !blocked)
                    item.Click += async (_, _) => await ChooseTranslationEngineAsync(menu, feedback, id);
                menu.Items.Add(item);
            }
            feedback.Visibility = menu.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            feedback.Header = TranslationMenuText("尚未配置翻译引擎");
            menu.Items.Add(feedback);
        }
        catch (Exception error)
        {
            feedback.Header = TranslationMenuText(TranslationSelectionError(error));
        }

        if (!menu.IsOpen) return;
        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });
        var manage = TranslationMenuItem("管理翻译模型…");
        manage.Click += LocalMtMenuItem_Click;
        menu.Items.Add(manage);
    }

    private async Task ChooseTranslationEngineAsync(ContextMenu menu, MenuItem feedback, string id)
    {
        if (_selectingTranslation) return;
        SetTranslationSelecting(true);
        var options = menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).ToArray();
        foreach (var item in options) item.IsEnabled = false;
        feedback.Header = TranslationMenuText("正在切换…");
        feedback.Visibility = Visibility.Visible;
        try
        {
            await SelectTranslationEngine!(id);
            UpdateTranslationButtons();
            menu.IsOpen = false;
        }
        catch (Exception error)
        {
            var message = TranslationSelectionError(error);
            feedback.Header = TranslationMenuText(message);
            if (!menu.IsOpen) SetStatus(message);
        }
        finally
        {
            foreach (var item in options) item.IsEnabled = true;
            SetTranslationSelecting(false);
        }
    }

    private MenuItem TranslationMenuItem(string label) => new()
    {
        Style = (Style)FindResource("DarkMenuItem"),
        Header = TranslationMenuText(label)
    };

    private static TextBlock TranslationMenuText(string label) => new()
    {
        Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 250
    };

    private async void TranslationLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingTranslation || LoadTranslationModels is null || SelectTranslationLanguages is null) return;
        if (_translationEngineMenu is { } menu) menu.IsOpen = false;
        if (TranslationLanguagePopup.IsOpen)
        {
            TranslationLanguagePopup.IsOpen = false;
            return;
        }
        TranslationLanguageFeedback.Visibility = Visibility.Collapsed;
        TranslationLanguagePopup.IsOpen = true;
        SetTranslationSelecting(true);
        try
        {
            if (_settings.TranslationMode == SentenceTranslationMode.LocalMt)
            {
                var offline = (await LoadTranslationModels()).FirstOrDefault(model => TranslationSelection.IsCurrent(model, _settings));
                _languagePairs = offline is null ? [] : TranslationSelection.OfflineLanguages(offline);
            }
            else
            {
                var codes = TranslationSelection.AiLanguages.Concat([_settings.SourceLang, _settings.TargetLang])
                    .Where(code => code != "auto" && MoreSettingsKnowledge.ValidLanguage(code)).Distinct().ToArray();
                _languagePairs = codes.Prepend("auto").SelectMany(source => codes.Where(target => target != source)
                    .Select(target => (source, target))).ToArray();
            }

            var sources = _languagePairs.Select(pair => pair.Source).Distinct().ToArray();
            TranslationSourcePicker.ItemsSource = sources.Select(code => new LanguageOption(code)).ToArray();
            var picked = sources.Contains(_settings.SourceLang) ? _settings.SourceLang : sources.FirstOrDefault();
            TranslationSourcePicker.SelectedValue = picked;
            SetTranslationTargets(_settings.TargetLang);

            if (sources.Length == 0)
                SetLanguageFeedback("请先在「管理翻译模型」中启用离线语言包。");
        }
        catch (Exception error)
        {
            _languagePairs = [];
            TranslationSourcePicker.ItemsSource = null;
            TranslationTargetPicker.ItemsSource = null;
            SetLanguageFeedback(TranslationSelectionError(error));
        }
        finally { SetTranslationSelecting(false); }
        if (TranslationLanguagePopup.IsOpen) TranslationSourcePicker.Focus();
    }

    private sealed record LanguageOption(string Code)
    {
        public string Name => TranslationLanguages.Label(Code);
    }

    private void TranslationSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        SetTranslationTargets(TranslationTargetPicker.SelectedValue as string ?? _settings.TargetLang);
        if (!_selectingTranslation) TranslationLanguageFeedback.Visibility = Visibility.Collapsed;
    }

    private void SetTranslationTargets(string preferred)
    {
        var targets = _languagePairs.Where(pair => pair.Source == (TranslationSourcePicker.SelectedValue as string))
            .Select(pair => pair.Target).Distinct().ToArray();
        TranslationTargetPicker.ItemsSource = targets.Select(code => new LanguageOption(code)).ToArray();
        TranslationTargetPicker.SelectedValue = targets.Contains(preferred) ? preferred : targets.FirstOrDefault();
        TranslationLanguageApply.IsEnabled = !_selectingTranslation && targets.Length > 0;
    }

    private async void ApplyTranslationLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingTranslation || SelectTranslationLanguages is null ||
            TranslationSourcePicker.SelectedValue is not string source || TranslationTargetPicker.SelectedValue is not string target) return;
        SetTranslationSelecting(true);
        SetLanguageFeedback("正在应用…");
        try
        {
            await SelectTranslationLanguages(source, target);
            UpdateTranslationButtons();
            TranslationLanguagePopup.IsOpen = false;
        }
        catch (Exception error)
        {
            var message = TranslationSelectionError(error);
            SetLanguageFeedback(message);
            if (!TranslationLanguagePopup.IsOpen) SetStatus(message);
        }
        finally { SetTranslationSelecting(false); }
    }

    private void TranslationLanguage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || TranslationSourcePicker.IsDropDownOpen || TranslationTargetPicker.IsDropDownOpen) return;
        TranslationLanguagePopup.IsOpen = false;
        TranslationLanguageButton.Focus();
        e.Handled = true;
    }

    private void SetTranslationSelecting(bool selecting)
    {
        _selectingTranslation = selecting;
        TranslationLanguageButton.IsEnabled = TranslationEngineButton.IsEnabled = !selecting;
        TranslationSourcePicker.IsEnabled = TranslationTargetPicker.IsEnabled = !selecting;
        TranslationLanguageApply.IsEnabled = !selecting && TranslationTargetPicker.SelectedValue is string;
    }

    private void SetLanguageFeedback(string message)
    {
        TranslationLanguageFeedback.Text = message;
        TranslationLanguageFeedback.Visibility = Visibility.Visible;
    }

    private static string TranslationSelectionError(Exception error) => error switch
    {
        ArgumentException or LlmTranslationException or InvalidDataException => error.Message,
        OperationCanceledException => "操作已取消，请重试。",
        _ => "操作失败，请检查模型配置后重试。"
    };
}
