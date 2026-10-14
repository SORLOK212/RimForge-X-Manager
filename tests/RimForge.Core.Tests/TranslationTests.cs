using RimForge.Core.Translation;
using RimForge.Translation;

namespace RimForge.Core.Tests;

public class TranslationTests
{
    [Theory]
    [InlineData("Adds a new faction of knights and their castles.", "en")]
    [InlineData("边缘市场 简繁中文汉化", "zh-CN")]
    [InlineData("ロボットの追加", "ja")]
    [InlineData("새로운 무기를 추가합니다", "ko")]
    [InlineData("Добавляет новую фракцию", "ru")]
    [InlineData("Додає нову фракцію і їхні замки", "uk")]
    [InlineData("Dieser Mod fügt neue Waffen für die Kolonie hinzu und ist mit der Version kompatibel", "de")]
    public void LanguageIsGuessedOffline(string text, string expected) => Assert.Equal(expected, LanguageGuess.Guess(text));

    [Theory]
    [InlineData("Добавляет новую фракцию", "ru", true)]
    [InlineData("Adds a new faction", "ru", false)]
    [InlineData("Adds a new faction", "en", true)]
    [InlineData("Fügt neue Waffen hinzu", "en", false)]
    [InlineData("边缘市场", "en", false)]
    public void TextsAlreadyInTheTargetLanguageAreSkipped(string text, string target, bool expected) =>
        Assert.Equal(expected, LanguageGuess.IsAlreadyIn(text, target));

    [Fact]
    public void OnlineAnswer_IsParsed()
    {
        const string json = """[[["Добавляет фракцию.\n","Adds a faction.\n",null,null,3],["Вторая строка","Second line",null,null,3]],null,"en",null]""";

        var (text, source) = OnlineTranslator.Parse(json);

        Assert.Equal("Добавляет фракцию.\nВторая строка", text);
        Assert.Equal("en", source);
    }

    [Fact]
    public void LongTexts_AreCutAtParagraphs()
    {
        var paragraph = new string('a', 1500) + "\n";
        var pieces = TranslationService.Split(paragraph + paragraph + paragraph + paragraph);

        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.True(p.Length <= 4000));
        Assert.Equal(paragraph + paragraph + paragraph + paragraph, string.Concat(pieces));
    }

    private sealed class FakeTranslator : ITranslator
    {
        public int Calls;

        public Task<IReadOnlyList<Translated>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Translated>>(texts.Select(t => new Translated("[" + t + "]", LanguageGuess.Guess(t))).ToList());
        }
    }

    [Fact]
    public async Task Service_GroupsByScript_SkipsOwnLanguage_AndRemembers()
    {
        var file = Path.Combine(Path.GetTempPath(), "rf-tr-" + Guid.NewGuid().ToString("N") + ".json");
        var fake = new FakeTranslator();
        var service = new TranslationService(file) { Translator = fake };

        var result = await service.TranslateAsync(["Adds knights", "Уже по-русски", "边缘市场", "More guns"], "ru");

        Assert.Equal(["[Adds knights]", "Уже по-русски", "[边缘市场]", "[More guns]"], result.Select(r => r.Text));
        Assert.Equal("zh-CN", result[2].SourceLanguage);
        Assert.Equal(2, fake.Calls); // one request for the Latin titles, one for the Chinese one

        service.Save();
        var again = new TranslationService(file) { Translator = fake };
        await again.TranslateAsync(["Adds knights"], "ru");
        Assert.Equal(2, fake.Calls); // came from the saved cache
        File.Delete(file);
    }

    /// <summary>Runs the real offline model when it is on disk (set RIMFORGE_NLLB_DIR); skipped otherwise.</summary>
    [Fact]
    public async Task OfflineModel_TranslatesEnglishAndChineseToRussian()
    {
        var dir = Environment.GetEnvironmentVariable("RIMFORGE_NLLB_DIR");
        if (dir is null || !NllbModel.IsInstalled(dir)) return;

        using var nllb = new NllbTranslator(dir);
        var result = await nllb.TranslateAsync(["Adds a new faction of knights.", "新的武器"], "ru", CancellationToken.None);

        Assert.Contains("рыцар", result[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("zh-CN", result[1].SourceLanguage);
        Assert.Contains("оруж", result[1].Text, StringComparison.OrdinalIgnoreCase);
    }
}
