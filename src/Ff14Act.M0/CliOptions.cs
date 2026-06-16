namespace Ff14Act.M0;

internal sealed class CliOptions
{
    public required string PcapPath { get; init; }
    public required string OodleDir { get; init; }
    public required string TablesDir { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        string? pcap = null, oodle = null, tables = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pcap": pcap = args[i + 1]; break;
                case "--oodle": oodle = args[i + 1]; break;
                case "--tables": tables = args[i + 1]; break;
            }
        }
        if (pcap is null || oodle is null || tables is null) return null;
        return new CliOptions { PcapPath = pcap, OodleDir = oodle, TablesDir = tables };
    }

    public static void PrintUsage() =>
        Console.Error.WriteLine(
            "用法: Ff14Act.M0.exe --pcap <capture.pcapng> --oodle <oodle dll 目录> --tables <去混淆六表目录>\n" +
            "三样准备见 docs/m0-runbook.md §0。");
}
