namespace Thanslator.Core;

public static class TranslationLanguages
{
    public static string Label(string code) => code switch {
        "auto"=>"自动识别","en"=>"英语","zh-Hans"=>"简体中文","ja"=>"日语","ko"=>"韩语","fr"=>"法语","de"=>"德语","es"=>"西班牙语",_=>code };
}
