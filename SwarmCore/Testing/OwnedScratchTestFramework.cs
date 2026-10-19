using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SwarmCore.Testing;

public sealed class OwnedScratchTestFramework(IMessageSink sink) : XunitTestFramework(sink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new ScratchExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class ScratchExecutor(AssemblyName name, ISourceInformationProvider source, IMessageSink diagnostics)
        : XunitTestFrameworkExecutor(name, source, diagnostics)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> cases, IMessageSink sink, ITestFrameworkExecutionOptions options)
        {
            using var runner = new ScratchRunner(TestAssembly, cases, DiagnosticMessageSink, sink, options);
            await runner.RunAsync();
        }
    }

    private sealed class ScratchRunner(ITestAssembly assembly, IEnumerable<IXunitTestCase> cases,
        IMessageSink diagnostics, IMessageSink execution, ITestFrameworkExecutionOptions options)
        : XunitTestAssemblyRunner(assembly, cases, diagnostics, execution, options)
    {
        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            await base.BeforeTestAssemblyFinishedAsync();
            // xUnit reports aggregator errors as TestAssemblyCleanupFailure before TestAssemblyFinished.
            Aggregator.Run(OwnedTestScratch.FinishRun);
        }
    }
}
