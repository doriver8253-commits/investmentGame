using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BlackWhale.Shared;
using BlackWhale.Network;
using BlackWhale.Tests;

internal sealed class TestJson : ITestJson, IWireCodec
{
    private readonly JsonSerializerOptions options = new JsonSerializerOptions { IncludeFields = true };
    public T Decode<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, options); }
        catch (JsonException ex) { throw new FormatException("Invalid test JSON", ex); }
    }
    public string Encode<T>(T value) { return JsonSerializer.Serialize(value, options); }
    public string EncodeClient(ClientMessage message) { return Encode(message); }
    public ClientMessage DecodeClient(string json) { RequestJsonValidator.Validate(json); return Decode<ClientMessage>(json); }
    public string EncodeServer(ServerMessage message) { return Encode(message); }
    public ServerMessage DecodeServer(string json) { return Decode<ServerMessage>(json); }
}
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--swap-only"))
            {
                foreach (string check in SwapValidation.Run()) Console.WriteLine("PASS " + check);
                return 0;
            }
            if (args.Contains("--news-only"))
            {
                foreach (string check in NewsValidation.Run()) Console.WriteLine("PASS " + check);
                return 0;
            }
            if (args.Contains("--patterns-only"))
            {
                foreach (string check in StockPatternValidation.Run().Concat(StockSystemValidation.Run())) Console.WriteLine("PASS " + check);
                return 0;
            }
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
            var codec = new TestJson();
            var suite = new RoomValidation(root, codec);
            string[] core = suite.RunAll();
            string[] network = await TransportValidation.Run(suite, codec);
            string[] realBC = await RealInformationValidation.Run(suite, codec);
            string[] nextA = await NextAValidation.Run(codec);
            string[] stock = StockPatternValidation.Run();
            stock = stock.Concat(StockSystemValidation.Run()).Concat(SwapValidation.Run()).Concat(NewsValidation.Run()).ToArray();
            foreach (string check in core.Concat(network).Concat(realBC)) Console.WriteLine("PASS " + check);
            foreach (string check in nextA) Console.WriteLine("PASS " + check);
            foreach (string check in stock) Console.WriteLine("PASS " + check);
            string output = Path.Combine(root, "Logs", "AValidation-results.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, JsonSerializer.Serialize(new { runtime = ".NET 10", serviceMode = "A_FIXTURE_MOCKS_AND_REAL_BC",
                core, network, realBC, nextA, stock, botCommandEvidence = NextAValidation.BotCommandEvidence,
                notVerified = new[] { "Four physical PCs", "Unity player UI", "Full B/C item and ability effects (N08-N10)" } },
                new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(output, Path.Combine(root, "Logs", "Next-A-results.json"), true);
            Console.WriteLine("PASS " + (core.Length + network.Length + realBC.Length + nextA.Length + stock.Length) + " checks. Evidence: " + output);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
