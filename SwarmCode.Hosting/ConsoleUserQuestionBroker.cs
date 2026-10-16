using System.Text;
using SwarmCore.Coding.Interaction;

namespace SwarmCode.Hosting;

/// <summary>
/// Question broker for the console. Interactive mode prints the question with numbered options and reads one line:
/// a number picks an option (comma-separated numbers when several are allowed), anything else is a free-text answer
/// when the question allows one, and an empty line skips. In <c>-p</c> mode or with redirected input nobody can
/// answer, so the model gets <see cref="UserAnswer.NoOne"/> at once. With <c>--json</c> the prompt goes to stderr so
/// stdout stays machine-readable.
/// </summary>
public sealed class ConsoleUserQuestionBroker : IUserQuestionBroker
{
    private readonly bool _nonInteractive;
    private readonly bool _promptToStderr;
    private readonly TextReader? _input;
    private readonly TextWriter? _output;
    private readonly bool? _inputRedirected;
    private readonly object _promptGate = new();

    public ConsoleUserQuestionBroker(bool nonInteractive, bool promptToStderr = false)
        : this(nonInteractive, promptToStderr, null, null, null) { }

    /// <summary>Test seam: explicit streams and redirection state instead of the process console.</summary>
    public ConsoleUserQuestionBroker(bool nonInteractive, bool promptToStderr, TextReader? input, TextWriter? output, bool? inputRedirected)
    {
        _nonInteractive = nonInteractive;
        _promptToStderr = promptToStderr;
        _input = input;
        _output = output;
        _inputRedirected = inputRedirected;
    }

    public Task<UserAnswer> AskAsync(UserQuestionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_nonInteractive || (_inputRedirected ?? Console.IsInputRedirected)) return Task.FromResult(UserAnswer.NoOne);
        string? line;
        lock (_promptGate)
        {
            var output = _output ?? (_promptToStderr ? Console.Error : Console.Out);
            output.Write(Render(request));
            output.Flush();
            line = (_input ?? Console.In).ReadLine();
        }
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Parse(line, request));
    }

    /// <summary>The prompt as printed: question, context, numbered options and a hint about how to answer.</summary>
    public static string Render(UserQuestionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.Append("Question: ").AppendLine(request.Question);
        if (!string.IsNullOrWhiteSpace(request.Context)) sb.Append("  ").AppendLine(request.Context);
        for (var i = 0; i < request.Options.Count; i++)
        {
            var option = request.Options[i];
            sb.Append("  ").Append(i + 1).Append(") ").Append(option.Label);
            if (!string.IsNullOrWhiteSpace(option.Description)) sb.Append(" - ").Append(option.Description);
            sb.AppendLine();
        }
        var hints = new List<string>();
        if (request.Options.Count > 0) hints.Add(request.AllowMultiple ? "numbers separated by commas" : "a number");
        if (request.AllowFreeText) hints.Add("your own answer");
        hints.Add("empty to skip");
        sb.Append("  answer (").Append(string.Join(", ", hints)).Append("): ");
        return sb.ToString();
    }

    /// <summary>Turns one typed line into an answer under the question's rules.</summary>
    public static UserAnswer Parse(string? line, UserQuestionRequest request)
    {
        var text = line?.Trim();
        if (string.IsNullOrEmpty(text)) return UserAnswer.Declined;

        if (request.Options.Count > 0)
        {
            var tokens = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var numbers = new List<int>();
            var allNumbers = tokens.Length > 0;
            foreach (var token in tokens)
            {
                if (int.TryParse(token, out var n) && n >= 1 && n <= request.Options.Count) numbers.Add(n);
                else { allNumbers = false; break; }
            }
            if (allNumbers)
            {
                if (!request.AllowMultiple) numbers = [numbers[0]];
                return UserAnswer.From(null, numbers.Distinct().Select(n => request.Options[n - 1].Label).ToList());
            }
            // A label typed out counts as choosing it.
            var byLabel = tokens.Select(t => request.Options.FirstOrDefault(o => string.Equals(o.Label, t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (byLabel.Count > 0 && byLabel.All(o => o is not null))
            {
                var labels = byLabel.Select(o => o!.Label).ToList();
                return UserAnswer.From(null, request.AllowMultiple ? labels : [labels[0]]);
            }
        }

        if (request.AllowFreeText) return UserAnswer.From(text, null);
        return new UserAnswer(false, null, [], "The answer did not match an option and the question does not allow free text. Decide yourself, state your assumption, and continue.");
    }
}
