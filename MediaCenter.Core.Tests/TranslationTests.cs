using System.Net;
using System.Text;
using System.Text.Json;
using MediaCenter.Core;

namespace MediaCenter.Core.Tests;

public sealed class TranslationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mc-tr-" + Guid.NewGuid().ToString("N"));

    public TranslationTests() => Directory.CreateDirectory(_base);

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static IReadOnlyList<SrtCue> Cues(params string[] texts) =>
        texts.Select((t, i) => new SrtCue(i + 1, TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 2), t)).ToList();

    /// <summary>Ein Modell, das „übersetzt“, indem es ein Präfix voranstellt; <paramref name="tweak"/> darf Antworten verfälschen.</summary>
    private sealed class FakeModel(Func<int, List<(int Id, string Text)>, List<(int Id, string Text)>>? tweak = null) : IChatModel
    {
        public string Name => "fake:1";

        public List<JsonElement> Requests { get; } = new();

        public string System { get; private set; } = "";

        public Task<string> ChatJsonAsync(string system, string user, string jsonSchema, CancellationToken ct)
        {
            System = system;
            using var doc = JsonDocument.Parse(user);
            Requests.Add(doc.RootElement.Clone());
            var items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => (e.GetProperty("id").GetInt32(), "DE " + e.GetProperty("text").GetString())).ToList();
            if (tweak is not null) items = tweak(Requests.Count, items);
            return Task.FromResult(JsonSerializer.Serialize(new { items = items.Select(i => new { id = i.Item1, text = i.Item2 }) }));
        }
    }

    // --- SRT ---------------------------------------------------------------------------------------------

    [Fact]
    public void Srt_round_trips_times_and_multiline_text()
    {
        var text = "﻿1\r\n00:00:01,500 --> 00:00:03,250\r\n<i>Hallo</i>\r\nWelt\r\n\r\n2\r\n01:02:03,004 --> 01:02:04,000\r\nZweite\r\n";
        var cues = Srt.Parse(text);

        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3250), cues[0].End);
        Assert.Equal("<i>Hallo</i>\nWelt", cues[0].Text);
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 4), cues[1].Start);

        var again = Srt.Parse(Srt.Format(cues));
        Assert.Equal(cues, again);
    }

    [Fact]
    public void Srt_accepts_dots_missing_numbers_and_short_milliseconds()
    {
        var cues = Srt.Parse("00:00:01.5 --> 00:00:02.25\nA\n\n00:00:03,000 --> 00:00:04,000\nB");

        Assert.Equal(TimeSpan.FromMilliseconds(1500), cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2250), cues[0].End);
        Assert.Equal(2, cues[1].Index);
    }

    [Fact]
    public void Srt_keeps_an_entry_when_the_blank_line_before_the_next_number_is_missing()
    {
        var cues = Srt.Parse("1\n00:00:01,000 --> 00:00:02,000\nA\n2\n00:00:03,000 --> 00:00:04,000\nB\n");

        Assert.Equal(new[] { "A", "B" }, cues.Select(c => c.Text));
    }

    [Fact]
    public void Srt_throws_with_the_line_for_a_block_without_a_timing_and_for_empty_files()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Srt.Parse("1\n00:00:01,000 --> 00:00:02,000\nA\n\nkaputt\nzeile\n"));
        Assert.Contains("Zeile 5", ex.Message);
        Assert.Throws<InvalidDataException>(() => Srt.Parse("  \n\n"));
    }

    // --- Übersetzen --------------------------------------------------------------------------------------

    [Fact]
    public async Task Translate_keeps_times_and_numbers_and_leaves_entries_without_words_alone()
    {
        var model = new FakeModel();
        var cues = Cues("Hello", "♪ ♪", "<i>Bye</i>", "...");

        var result = await new SubtitleTranslator(model).TranslateAsync(cues, TranslationTarget.German);

        Assert.Equal(cues.Select(c => (c.Index, c.Start, c.End)), result.Select(c => (c.Index, c.Start, c.End)));
        Assert.Equal(new[] { "DE Hello", "♪ ♪", "DE <i>Bye</i>", "..." }, result.Select(c => c.Text));
        Assert.Equal(2, model.Requests[0].GetProperty("items").GetArrayLength()); // nur die zwei mit Text gehen ans Modell
    }

    [Fact]
    public async Task Translate_sends_no_times_and_names_the_target_in_the_prompt()
    {
        var model = new FakeModel();

        await new SubtitleTranslator(model).TranslateAsync(Cues("Hello"), TranslationTarget.TraditionalChinese,
            new TranslationOptions(SourceLanguage: "English"));

        Assert.DoesNotContain("-->", model.Requests[0].ToString());
        Assert.Contains("Traditional Chinese", model.System);
        Assert.Contains("from English", model.System);
        Assert.Contains("Never use Simplified", model.System);
    }

    [Fact]
    public async Task Translate_splits_long_files_into_blocks_and_gives_the_next_block_the_previous_translations_as_context()
    {
        var model = new FakeModel();
        var cues = Cues(Enumerable.Range(1, 7).Select(i => $"Line {i}").ToArray());

        var result = await new SubtitleTranslator(model).TranslateAsync(cues, TranslationTarget.German, new TranslationOptions(BlockCues: 3, ContextCues: 2));

        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(7, result.Count);
        Assert.Empty(model.Requests[0].GetProperty("previous").EnumerateArray());
        var previous = model.Requests[1].GetProperty("previous").EnumerateArray().ToList();
        Assert.Equal(2, previous.Count);
        Assert.Equal("Line 2", previous[0].GetProperty("source").GetString());
        Assert.Equal("DE Line 3", previous[1].GetProperty("translation").GetString());
        // innerhalb des Blocks zählen die Nummern ab 1
        Assert.Equal(1, model.Requests[1].GetProperty("items")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Translate_asks_again_with_the_problem_when_an_item_is_missing()
    {
        var model = new FakeModel((call, items) => call == 1 ? items[..1] : items);

        var result = await new SubtitleTranslator(model).TranslateAsync(Cues("A one", "B two"), TranslationTarget.German);

        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("1 items instead of 2", model.Requests[1].GetProperty("problem").GetString());
        Assert.Equal("DE B two", result[1].Text);
    }

    [Fact]
    public async Task Translate_rejects_changed_formatting_tags()
    {
        var model = new FakeModel((call, items) => call == 1 ? items.Select(i => (i.Id, i.Text.Replace("<i>", "").Replace("</i>", ""))).ToList() : items);

        var result = await new SubtitleTranslator(model).TranslateAsync(Cues("<i>Hello there</i>"), TranslationTarget.German);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("DE <i>Hello there</i>", result[0].Text);
    }

    [Fact]
    public async Task Translate_to_traditional_chinese_rejects_simplified_characters_and_German_rejects_chinese()
    {
        var simplified = new FakeModel((call, items) => call == 1 ? items.Select(i => (i.Id, "你们好")).ToList() : items.Select(i => (i.Id, "你們好")).ToList());
        var result = await new SubtitleTranslator(simplified).TranslateAsync(Cues("Hello you"), TranslationTarget.TraditionalChinese);
        Assert.Equal("你們好", result[0].Text);
        Assert.Contains("vereinfachte Schriftzeichen („们“)", TranslationTarget.TraditionalChinese.Check("你们好"));

        var chinese = new FakeModel((_, items) => items.Select(i => (i.Id, "你好")).ToList());
        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            new SubtitleTranslator(chinese).TranslateAsync(Cues("Hello"), TranslationTarget.German));
        Assert.Contains("chinesische Zeichen", ex.Message);
    }

    [Fact]
    public async Task Translate_halves_a_block_that_never_validates_and_names_the_entry_that_cannot_be_translated()
    {
        // Entry 3 ("bad") ist als Einzelanfrage nie gültig; die übrigen gehen durch.
        var model = new FakeModel((_, items) => items.Select(i => (i.Id, i.Text.Contains("bad") ? "" : i.Text)).ToList());
        var cues = Cues("fine one", "fine two", "bad entry", "fine four");

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            new SubtitleTranslator(model).TranslateAsync(cues, TranslationTarget.German, new TranslationOptions(MaxAttempts: 2)));

        Assert.Contains("Eintrag 3", ex.Message);
        Assert.Contains("bad entry", ex.Message);
        Assert.Contains("empty", ex.Message);
        Assert.True(model.Requests.Count > 2); // mehrfach gefragt, dann halbiert
    }

    [Fact]
    public async Task Translate_fails_loud_on_answers_that_are_not_json()
    {
        var model = new RawModel("das ist kein JSON");

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            new SubtitleTranslator(model).TranslateAsync(Cues("Hello"), TranslationTarget.German));

        Assert.Contains("JSON", ex.Message);
    }

    private sealed class RawModel(string answer) : IChatModel
    {
        public string Name => "raw";

        public Task<string> ChatJsonAsync(string system, string user, string jsonSchema, CancellationToken ct) => Task.FromResult(answer);
    }

    [Fact]
    public async Task Translate_propagates_errors_of_the_backend_without_retrying()
    {
        var calls = 0;
        var model = new ThrowingModel(() => { calls++; throw new OllamaException("Ollama ist nicht erreichbar."); });

        await Assert.ThrowsAsync<OllamaException>(() => new SubtitleTranslator(model).TranslateAsync(Cues("Hello"), TranslationTarget.German));
        Assert.Equal(1, calls);
    }

    private sealed class ThrowingModel(Action act) : IChatModel
    {
        public string Name => "throwing";

        public Task<string> ChatJsonAsync(string system, string user, string jsonSchema, CancellationToken ct)
        {
            act();
            return Task.FromResult("");
        }
    }

    [Fact]
    public async Task Translate_resumes_from_the_checkpoint_after_a_cancel_and_deletes_it_when_done()
    {
        var checkpoint = Path.Combine(_base, "state", "job.json");
        var cues = Cues(Enumerable.Range(1, 6).Select(i => $"Line {i}").ToArray());
        var options = new TranslationOptions(BlockCues: 2);

        using var cts = new CancellationTokenSource();
        var first = new FakeModel((call, items) =>
        {
            if (call == 2) cts.Cancel(); // nach dem zweiten Block abbrechen
            return items;
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new SubtitleTranslator(first).TranslateAsync(cues, TranslationTarget.German, options, null, checkpoint, cts.Token));
        Assert.True(File.Exists(checkpoint));
        Assert.Equal(2, first.Requests.Count);

        var second = new FakeModel();
        var result = await new SubtitleTranslator(second).TranslateAsync(cues, TranslationTarget.German, options, null, checkpoint);

        Assert.Single(second.Requests); // nur der dritte Block fehlte noch
        Assert.Equal("Line 5", second.Requests[0].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"DE Line {i}"), result.Select(c => c.Text));
        Assert.False(File.Exists(checkpoint));
    }

    [Fact]
    public async Task Translate_ignores_a_checkpoint_of_another_job_but_fails_loud_on_a_corrupt_one()
    {
        var checkpoint = Path.Combine(_base, "other.json");
        var options = new TranslationOptions(BlockCues: 1);
        using var cts = new CancellationTokenSource();
        var partial = new FakeModel((_, items) => { cts.Cancel(); return items; });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new SubtitleTranslator(partial).TranslateAsync(Cues("One", "Two"), TranslationTarget.German, options, null, checkpoint, cts.Token));

        var model = new FakeModel();
        await new SubtitleTranslator(model).TranslateAsync(Cues("Other", "Text"), TranslationTarget.German, options, null, checkpoint);
        Assert.Equal(2, model.Requests.Count); // anderer Inhalt: von vorn

        File.WriteAllText(checkpoint, "{kaputt");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SubtitleTranslator(new FakeModel()).TranslateAsync(Cues("One"), TranslationTarget.German, null, null, checkpoint));
        Assert.Contains("beschädigt", ex.Message);
    }

    [Fact]
    public async Task Translate_reports_progress_up_to_the_total()
    {
        var reports = new List<TranslationProgress>();
        var progress = new SyncProgress<TranslationProgress>(reports.Add);

        await new SubtitleTranslator(new FakeModel()).TranslateAsync(Cues("A one", "♪", "B two", "C three"), TranslationTarget.German,
            new TranslationOptions(BlockCues: 2), progress);

        Assert.Equal(new TranslationProgress(1, 4), reports[0]); // der Eintrag ohne Text zählt schon als fertig
        Assert.Equal(new TranslationProgress(4, 4), reports[^1]);
        Assert.Equal(reports.Select(r => r.DoneCues).Order(), reports.Select(r => r.DoneCues));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // --- Zeilenumbruch -----------------------------------------------------------------------------------

    [Fact]
    public void Wrap_splits_a_long_single_line_at_the_space_nearest_the_middle_and_keeps_tags_whole()
    {
        var text = "Das ist ein ziemlich langer deutscher Satz, der nicht auf eine Zeile passt.";
        var wrapped = SubtitleTranslator.Wrap(text, TranslationTarget.German);

        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(text, string.Join(" ", lines));
        Assert.All(lines, l => Assert.InRange(l.Length, 25, 50));

        Assert.Equal("Kurz", SubtitleTranslator.Wrap("Kurz", TranslationTarget.German));
        Assert.Equal("A\nB", SubtitleTranslator.Wrap("A\nB", TranslationTarget.German)); // vorhandene Umbrüche bleiben
        Assert.DoesNotContain('\n', SubtitleTranslator.Wrap("<i>" + string.Concat(Enumerable.Repeat("x", 60)) + "</i>", TranslationTarget.German)); // keine Stelle ohne Tag
    }

    [Fact]
    public void Wrap_splits_chinese_at_punctuation()
    {
        var text = "我不知道你在說什麼，但是我們必須現在就離開這個地方。";
        var wrapped = SubtitleTranslator.Wrap(text, TranslationTarget.TraditionalChinese);

        Assert.Equal("我不知道你在說什麼，\n但是我們必須現在就離開這個地方。", wrapped);
    }

    [Fact]
    public void Wrap_chinese_without_punctuation_never_cuts_a_latin_word_or_leaves_punctuation_at_the_line_start()
    {
        var wrapped = SubtitleTranslator.Wrap("我們必須馬上告訴 Jackson 他的計畫已經失敗了，不是嗎", TranslationTarget.TraditionalChinese);

        Assert.Equal(2, wrapped.Split('\n').Length);
        Assert.Contains("Jackson", wrapped);
        Assert.Equal("我們必須馬上告訴Jackson他的計畫已經失敗了，不是嗎", wrapped.Replace("\n", "").Replace(" ", ""));

        // Ohne Leerzeichen und ohne chinesische Satzzeichen: Trennstelle trotzdem außerhalb lateinischer Wörter und nicht vor „,“.
        var mixed = SubtitleTranslator.Wrap("Line number 1, this is a test 我們必須馬上離開這裡", TranslationTarget.TraditionalChinese);
        Assert.Equal(2, mixed.Split('\n').Length);
        Assert.Contains("number", mixed);
        Assert.Contains("test", mixed);
        Assert.DoesNotContain("\n,", mixed);
    }

    // --- Ollama ------------------------------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static OllamaClient Client(FakeHandler handler) => new(new HttpClient(handler), new Uri("http://localhost:11434"));

    [Fact]
    public async Task Ollama_chat_sends_schema_temperature_zero_and_returns_the_message_without_thinking()
    {
        var handler = new FakeHandler((_, _) => Json("""{"message":{"role":"assistant","content":"<think>hm</think>{\"items\":[]}"},"done":true}"""));

        var answer = await Client(handler).Model("qwen3:14b").ChatJsonAsync("sys", "usr", """{"type":"object"}""", default);

        Assert.Equal("""{"items":[]}""", answer);
        var (method, path, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/chat", path);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("qwen3:14b", sent.RootElement.GetProperty("model").GetString());
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(sent.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("object", sent.RootElement.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(0, sent.RootElement.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal("usr", sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Ollama_reports_a_missing_model_with_the_pull_command_and_other_errors_loud()
    {
        var missing = Client(new FakeHandler((_, _) => Json("""{"error":"model 'x' not found"}""", HttpStatusCode.NotFound)));
        var ex = await Assert.ThrowsAsync<OllamaException>(() => missing.Model("x").ChatJsonAsync("s", "u", "{}", default));
        Assert.Contains("ollama pull x", ex.Message);

        var broken = Client(new FakeHandler((_, _) => Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)));
        ex = await Assert.ThrowsAsync<OllamaException>(() => broken.Model("x").ChatJsonAsync("s", "u", "{}", default));
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);

        var down = Client(new FakeHandler((_, _) => throw new HttpRequestException("Connection refused")));
        ex = await Assert.ThrowsAsync<OllamaException>(() => down.ListModelsAsync());
        Assert.Contains("nicht erreichbar", ex.Message);
    }

    [Fact]
    public async Task Ollama_lists_installed_models_sorted()
    {
        var client = Client(new FakeHandler((_, _) => Json("""{"models":[{"name":"qwen3:14b"},{"name":"gemma3:27b"}]}""")));

        Assert.Equal(new[] { "gemma3:27b", "qwen3:14b" }, await client.ListModelsAsync());
    }

    [Theory]
    [InlineData(0, PlacementKind.Cpu, 0)]
    [InlineData(1000, PlacementKind.Gpu, 100)]
    [InlineData(600, PlacementKind.Mixed, 60)]
    public async Task Ollama_tells_where_the_model_runs(long vram, PlacementKind kind, int percent)
    {
        var client = Client(new FakeHandler((_, _) => Json($$"""{"models":[{"name":"qwen3:latest","size":1000,"size_vram":{{vram}}}]}""")));

        Assert.Equal(new ModelPlacement(kind, percent), await client.GetPlacementAsync("qwen3"));
    }

    [Fact]
    public async Task Ollama_placement_is_not_loaded_when_the_model_is_not_running()
    {
        var client = Client(new FakeHandler((_, _) => Json("""{"models":[]}""")));

        Assert.Equal(PlacementKind.NotLoaded, (await client.GetPlacementAsync("qwen3")).Kind);
    }

    [Fact]
    public void Ollama_base_url_keeps_a_path_prefix()
    {
        var client = new OllamaClient(new HttpClient(), new Uri("http://host:1234/ollama"));

        Assert.Equal("http://host:1234/ollama/", client.BaseUrl.ToString());
    }

    // --- Extraktor (echtes ffmpeg) -----------------------------------------------------------------------

    private static string Ffmpeg => Environment.GetEnvironmentVariable("MEDIACENTER_FFMPEG") ?? "ffmpeg";

    [Fact]
    public async Task Extractor_reads_srt_directly_and_converts_ass_with_ffmpeg()
    {
        var srt = Path.Combine(_base, "a.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:02,000\nHallo Welt\n", new UTF8Encoding(false));
        var extractor = new SubtitleExtractor(Ffmpeg);
        Assert.Equal("Hallo Welt", (await extractor.FromFileAsync(srt))[0].Text);

        var ass = Path.Combine(_base, "b.ass");
        File.WriteAllText(ass, """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,Guten Tag
            """);
        var cues = await extractor.FromFileAsync(ass);
        Assert.Equal("Guten Tag", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
    }

    [Fact]
    public async Task Extractor_reads_a_text_stream_from_a_video_and_fails_loud_for_a_missing_stream()
    {
        var srt = Path.Combine(_base, "in.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nIm Video\n");
        var video = Path.Combine(_base, "v.mkv");
        await Run(Ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=d=2:s=160x90:r=10", "-i", srt,
            "-map", "0:v", "-map", "1:0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:s", "srt", video);

        var extractor = new SubtitleExtractor(Ffmpeg);
        var cues = await extractor.FromStreamAsync(video, 1);
        Assert.Equal("Im Video", cues[0].Text);

        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.FromStreamAsync(video, 5));
    }

    [Theory]
    [InlineData("subrip", true)]
    [InlineData("ass", true)]
    [InlineData("hdmv_pgs_subtitle", false)]
    [InlineData("dvd_subtitle", false)]
    [InlineData(null, false)]
    public void Only_text_codecs_can_be_extracted(string? codec, bool expected) => Assert.Equal(expected, SubtitleExtractor.IsText(codec));

    private static async Task Run(string tool, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} schlug fehl: {error}");
    }
}
