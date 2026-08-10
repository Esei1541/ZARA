using System.Text;
using Zara.Infrastructure.Windows.UsagePolicy;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsEmergencyUnlockPromptCatalogTests
{
    private static readonly string[] PresetFileNames =
    [
        "gwangyeom-sonata.txt",
        "readymade-life.txt",
        "wings.txt",
    ];
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private string _presetsDirectoryPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _presetsDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "zara-emergency-unlock-prompt-catalog-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_presetsDirectoryPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_presetsDirectoryPath))
        {
            Directory.Delete(_presetsDirectoryPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task SelectDistinctAsyncReturnsRequestedDistinctPromptsFromAllPresetFiles()
    {
        string[] firstPreset =
        [
            "오늘은 사용 시간을 확인하고 필요한 일을 차분히 마칩니다.",
            "예약된 시간에는 필요한 작업을 마친 뒤 바로 쉬어야 합니다.",
        ];
        string[] secondPreset =
        [
            "긴급 해제 문장은 화면에 보이는 내용과 정확히 같아야 합니다.",
            "정해진 시간이 끝나면 다시 사용 규칙을 따라야 합니다.",
        ];
        string[] thirdPreset =
        [
            "지금 할 일을 마치고 다음 순서를 천천히 확인합니다.",
            "문장을 모두 입력한 뒤에만 긴급 해제를 시작합니다.",
        ];
        WriteRequiredPresetFiles(firstPreset, secondPreset, thirdPreset);
        var catalog = CreateCatalog();
        var validPrompts = new HashSet<string>(
            firstPreset.Concat(secondPreset).Concat(thirdPreset),
            StringComparer.Ordinal);

        IReadOnlyList<string> actual = await catalog.SelectDistinctAsync(5);

        Assert.HasCount(5, actual);
        Assert.AreEqual(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(actual.All(validPrompts.Contains));
    }

    [TestMethod]
    public async Task ZeroPromptRequestReturnsEmptyWithoutReadingPresetFiles()
    {
        var catalog = CreateCatalog();

        IReadOnlyList<string> actual = await catalog.SelectDistinctAsync(0);

        Assert.IsEmpty(actual);
    }

    [TestMethod]
    public async Task PreCancelledRequestFailsBeforeReadingPresetFiles()
    {
        var catalog = CreateCatalog();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => catalog.SelectDistinctAsync(0, cancellation.Token));
    }

    [TestMethod]
    public async Task MissingRequiredPresetFileFails()
    {
        WritePresetFile(PresetFileNames[0], "오늘은 사용 시간을 확인하고 필요한 일을 차분히 마칩니다.");
        WritePresetFile(PresetFileNames[1], "예약된 시간에는 필요한 작업을 마친 뒤 바로 쉬어야 합니다.");
        var catalog = CreateCatalog();

        FileNotFoundException exception = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => catalog.SelectDistinctAsync(1));

        StringAssert.Contains(exception.Message, PresetFileNames[2]);
    }

    [TestMethod]
    public async Task PromptWithDisallowedCharacterFails()
    {
        WriteRequiredPresetFiles(
            ["오늘은 사용 시간을 확인하고 필요한 일을 차분히 마칩니다."],
            ["예약된 시간에는 필요한 작업을 마친 뒤 바로 쉬어야 합니다."],
            ["영문 A가 포함된 문장은 긴급 해제에 사용하지 않습니다."]);
        var catalog = CreateCatalog();

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => catalog.SelectDistinctAsync(1));

        StringAssert.Contains(exception.Message, PresetFileNames[2]);
        StringAssert.Contains(exception.Message, "line 1");
    }

    [TestMethod]
    public async Task InvalidUtf8PresetFileFails()
    {
        WritePresetFile(PresetFileNames[0], "오늘은 사용 시간을 확인하고 필요한 일을 차분히 마칩니다.");
        File.WriteAllBytes(
            GetPresetFilePath(PresetFileNames[1]),
            [0xC3, 0x28]);
        WritePresetFile(PresetFileNames[2], "긴급 해제 문장은 화면에 보이는 내용과 정확히 같아야 합니다.");
        var catalog = CreateCatalog();

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => catalog.SelectDistinctAsync(1));

        StringAssert.Contains(exception.Message, PresetFileNames[1]);
    }

    [TestMethod]
    public async Task RequestLargerThanDistinctCatalogFails()
    {
        const string sharedPrompt = "오늘은 사용 시간을 확인하고 필요한 일을 차분히 마칩니다.";
        WriteRequiredPresetFiles(
            [sharedPrompt],
            [sharedPrompt],
            ["긴급 해제 문장은 화면에 보이는 내용과 정확히 같아야 합니다."]);
        var catalog = CreateCatalog();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => catalog.SelectDistinctAsync(3));

        StringAssert.Contains(exception.Message, "2 distinct prompts");
    }

    [TestMethod]
    public async Task NegativePromptRequestFails()
    {
        var catalog = CreateCatalog();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => catalog.SelectDistinctAsync(-1));
    }

    private WindowsEmergencyPromptCatalog CreateCatalog() =>
        new(_presetsDirectoryPath, new Random(20260811));

    private void WriteRequiredPresetFiles(
        IReadOnlyList<string> firstPreset,
        IReadOnlyList<string> secondPreset,
        IReadOnlyList<string> thirdPreset)
    {
        WritePresetFile(PresetFileNames[0], firstPreset);
        WritePresetFile(PresetFileNames[1], secondPreset);
        WritePresetFile(PresetFileNames[2], thirdPreset);
    }

    private void WritePresetFile(string presetFileName, params string[] prompts) =>
        WritePresetFile(presetFileName, (IReadOnlyList<string>)prompts);

    private void WritePresetFile(string presetFileName, IReadOnlyList<string> prompts) =>
        File.WriteAllLines(GetPresetFilePath(presetFileName), prompts, Utf8WithoutBom);

    private string GetPresetFilePath(string presetFileName) =>
        Path.Combine(_presetsDirectoryPath, presetFileName);
}
