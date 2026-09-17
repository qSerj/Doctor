using PsDoctor.Core.Scenarios;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class ScenarioParserTests
{
    [Fact]
    public void Незнакомый_шаг_отвергается_с_номером_строки()
    {
        var result = ScenarioParser.Parse("launch \"C:\\lab\\p\\1.psh\"\n\nclick 100 200\nclose");

        Assert.Null(result.Scenario);
        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.Line);
        Assert.Equal(ScenarioErrorKind.UnknownStep, error.Kind);
        Assert.Equal("click", error.Token);
    }

    [Fact]
    public void Незнакомое_ожидание_отвергается_с_номером_строки()
    {
        var error = Assert.Single(ScenarioParser.Parse("close\nwait window 30").Errors);

        Assert.Equal(new ScenarioError(2, ScenarioErrorKind.UnknownStep, "wait window"), error);
    }

    [Fact]
    public void Сценарий_из_плана_разбирается_целиком()
    {
        const string text = """
            launch "C:\lab\p\1.psh"
            wait dialog 120
            press "ОК"
            wait dialog 30
            press "Ok"
            wait title "1.psh" 600
            close
            wait exit 60
            """;

        var result = ScenarioParser.Parse(text.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Empty(result.Errors);
        var steps = result.Scenario!.Steps;
        Assert.Equal(8, steps.Count);
        Assert.Equal(new LaunchStep(1, "launch \"C:\\lab\\p\\1.psh\"", "C:\\lab\\p\\1.psh"), steps[0]);
        Assert.Equal(TimeSpan.FromSeconds(120), Assert.IsType<WaitDialogStep>(steps[1]).Timeout);
        Assert.Equal("ОК", Assert.IsType<PressStep>(steps[2]).Button);
        var title = Assert.IsType<WaitTitleStep>(steps[5]);
        Assert.Equal(("1.psh", TimeSpan.FromSeconds(600)), (title.Substring, title.Timeout));
        Assert.IsType<CloseStep>(steps[6]);
        Assert.Equal(8, Assert.IsType<WaitExitStep>(steps[7]).Line);
    }

    [Fact]
    public void Ожидание_покоя_берёт_секунды_покоя_и_таймаут()
    {
        var step = Assert.IsType<WaitIdleStep>(Assert.Single(ScenarioParser.Parse("wait idle 5 300").Scenario!.Steps));

        Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(300)), (step.Quiet, step.Timeout));
    }

    [Fact]
    public void Объявленные_но_не_умеющие_шаги_разбираются()
    {
        var steps = ScenarioParser.Parse("render\nwait render-done 3600").Scenario!.Steps;

        Assert.IsType<RenderStep>(steps[0]);
        Assert.IsType<WaitRenderDoneStep>(steps[1]);
    }

    [Theory]
    [InlineData("launch", ScenarioErrorKind.MissingArgument, null)]
    [InlineData("press \"\"", ScenarioErrorKind.MissingArgument, null)]
    [InlineData("wait", ScenarioErrorKind.MissingArgument, "wait")]
    [InlineData("wait dialog", ScenarioErrorKind.MissingArgument, null)]
    [InlineData("wait title \"1.psh\"", ScenarioErrorKind.MissingArgument, null)]
    [InlineData("wait dialog долго", ScenarioErrorKind.BadSeconds, "долго")]
    [InlineData("wait dialog 0", ScenarioErrorKind.BadSeconds, "0")]
    [InlineData("wait dialog -5", ScenarioErrorKind.BadSeconds, "-5")]
    [InlineData("wait exit 1.5", ScenarioErrorKind.BadSeconds, "1.5")]
    [InlineData("close now", ScenarioErrorKind.ExtraArgument, "now")]
    [InlineData("wait exit 60 70", ScenarioErrorKind.ExtraArgument, "70")]
    [InlineData("press \"Ok to All", ScenarioErrorKind.UnterminatedQuote, null)]
    public void Кривой_шаг_отвергается_с_причиной(string line, ScenarioErrorKind kind, string? token)
    {
        var result = ScenarioParser.Parse("close\n" + line);

        Assert.Null(result.Scenario);
        Assert.Equal(new ScenarioError(2, kind, token), Assert.Single(result.Errors));
    }

    [Fact]
    public void Все_ошибки_собираются_сразу()
    {
        var result = ScenarioParser.Parse("jump\nclose\nwait dialog x\npress");

        Assert.Equal([1, 3, 4], result.Errors.Select(error => error.Line));
    }

    [Fact]
    public void Кнопка_с_пробелами_в_кавычках_одно_слово()
    {
        var step = Assert.IsType<PressStep>(Assert.Single(ScenarioParser.Parse("  press   \"Ok to All\"  ").Scenario!.Steps));

        Assert.Equal("Ok to All", step.Button);
        Assert.Equal("press   \"Ok to All\"", step.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n  \n\r\n")]
    public void Пустой_сценарий_отвергается(string text)
    {
        var result = ScenarioParser.Parse(text);

        Assert.Null(result.Scenario);
        Assert.Equal(ScenarioErrorKind.Empty, Assert.Single(result.Errors).Kind);
    }
}
