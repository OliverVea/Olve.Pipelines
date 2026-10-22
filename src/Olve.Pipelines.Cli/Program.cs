using Olve.Pipelines.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    // ReSharper disable once AccessToDisposedClosure
    cts.Cancel();
};

try
{
    var dispatcher = new CliDispatcher(new ProcessRunner());
    return await dispatcher.RunAsync(args, cts.Token);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    return 130;
}
catch (Exception ex) when (!args.Contains("--verbose") && !args.Contains("-v"))
{
    Console.Error.WriteLine($"error: {ex.Message} (--verbose for details)");
    return 1;
}
