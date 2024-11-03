using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;

namespace DragonFruit.OnionFruit.Deploy;

public class ProcessAgeEnricher : ILogEventEnricher
{
    private static readonly Stopwatch _processAgeStopwatch = Stopwatch.StartNew();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var processAge = _processAgeStopwatch.Elapsed;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ProcessAge", ((int)processAge.TotalMilliseconds).ToString().PadLeft(6)));
    }
}