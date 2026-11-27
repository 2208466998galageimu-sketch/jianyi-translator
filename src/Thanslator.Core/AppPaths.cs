using System.IO;

namespace Thanslator.Core;

/// <summary>
/// Single source of truth for where the application keeps its files.
/// </summary>
/// <remarks>
/// Everything the app writes (settings database, logs, models) lives under one
/// <c>data</c> folder, so the repository — or a copied publish folder — is
/// self-contained and nothing lands in <c>%LOCALAPPDATA%</c>.
/// <para>
/// Resolution order for the data root:
/// <list type="number">
/// <item><c>THANSLATOR_HOME</c> environment variable (tests, portable installs);</item>
/// <item><c>&lt;repository root&gt;\data</c>, found by walking up from the executable
/// for <c>Thanslator.slnx</c> — this covers running from <c>bin\Debug</c> and from
/// <c>dist\*\</c> inside the checkout;</item>
/// <item><c>&lt;exe folder&gt;\data</c> for a copy that lives outside the repository.</item>
/// </list>
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>Optional override of the data folder; mainly for tests and portable use.</summary>
    public const string HomeEnvironmentVariable = "THANSLATOR_HOME";

    private const string DataFolderName = "data";
    private const int MaxRootSearchDepth = 10;

    private static readonly string[] RepositoryMarkers = ["Thanslator.slnx", "Thanslator.sln"];

    private static readonly Lazy<string> LazyDataRoot = new(ResolveDataRoot, isThreadSafe: true);
    private static readonly Lazy<string?> LazyRepositoryRoot =
        new(() => FindRepositoryRoot(AppContext.BaseDirectory), isThreadSafe: true);
    private static readonly Lazy<string> LazyAssetsDirectory = new(ResolveAssetsDirectory, isThreadSafe: true);

    /// <summary>Root folder for every file the app writes.</summary>
    public static string DataRoot => LazyDataRoot.Value;

    /// <summary>Repository checkout that owns the executable, when there is one.</summary>
    public static string? RepositoryRoot => LazyRepositoryRoot.Value;

    public static string SettingsDatabase => Path.Combine(DataRoot, "thanslator.db");

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    public static string ModelsDirectory => Path.Combine(DataRoot, "models");

    /// <summary>Default OPUS-MT model folder; users can override it in settings.</summary>
    public static string DefaultModelDirectory => Path.Combine(ModelsDirectory, "opus-mt-en-zh-int8");

    /// <summary>Read-only assets (dictionary, overrides) shipped with the app.</summary>
    public static string AssetsDirectory => LazyAssetsDirectory.Value;

    public static string DictionaryDatabase => Path.Combine(AssetsDirectory, "dictionary", "dict-en-zh.sqlite");

    public static string FunctionWordsOverrides => Path.Combine(AssetsDirectory, "overrides", "function-words.json");

    /// <summary>Symbol/abbreviation entries (U.S., C++, node.js...) merged over function words.</summary>
    public static string DictionaryOverrides => Path.Combine(AssetsDirectory, "overrides", "dictionary-overrides.json");

    /// <summary>Optional user dictionary overrides (data\overrides).</summary>
    public static string UserDictionaryOverrides => Path.Combine(DataRoot, "overrides", "dictionary-overrides.json");

    public static string TranslationGlossary => Path.Combine(AssetsDirectory, "overrides", "translation-glossary.json");

    /// <summary>Optional user glossary merged over the built-in one (data\overrides).</summary>
    public static string UserTranslationGlossary => Path.Combine(DataRoot, "overrides", "translation-glossary.json");

    /// <summary>Literal-mode preferred (primary/original) glosses.</summary>
    public static string PrimaryMeanings => Path.Combine(AssetsDirectory, "overrides", "primary-meanings.json");

    /// <summary>Optional user primary-gloss overrides (data\overrides).</summary>
    public static string UserPrimaryMeanings => Path.Combine(DataRoot, "overrides", "primary-meanings.json");

    /// <summary>Creates the writable directories so callers can assume they exist.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveDataRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        if (RepositoryRoot is { Length: > 0 } repository)
        {
            return Path.Combine(repository, DataFolderName);
        }

        return Path.Combine(AppContext.BaseDirectory, DataFolderName);
    }

    private static string ResolveAssetsDirectory()
    {
        // Published builds copy assets next to the executable; a development build
        // has no assets folder of its own, so fall back to the checkout.
        var besideExecutable = Path.Combine(AppContext.BaseDirectory, "assets");
        if (Directory.Exists(besideExecutable))
        {
            return besideExecutable;
        }

        if (RepositoryRoot is { Length: > 0 } repository)
        {
            var inRepository = Path.Combine(repository, "assets");
            if (Directory.Exists(inRepository))
            {
                return inRepository;
            }
        }

        return besideExecutable;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var depth = 0; directory is not null && depth < MaxRootSearchDepth; depth++)
        {
            foreach (var marker in RepositoryMarkers)
            {
                if (File.Exists(Path.Combine(directory.FullName, marker)))
                {
                    return directory.FullName;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }
}
