namespace Eryri.WriteAheadLog.Tests.Domain;

internal class CalculatorV2(string directory) : Calculator(directory)
{
    protected override ulong FormatVersion => 2;
}
