using System.Collections.Immutable;
using FluentAssertions;
using KustoLoco.FileFormats;
using KustoSupport;

namespace BasicTests
{
    [TestClass]
    public class CsvLoadTester
    {
        private static KustoQueryContext CreateContext() => KustoQueryContext.WithFullDebug();

        [TestMethod]
        public async Task TestMethod1()
        {
            var context = CreateContext();
            var csv = @"
Name,Count
acd,100
def,30
";
            CsvLoader.LoadFromString(csv, "data", context);
            var nameResult = (await context.RunTabularQueryAsync("data | where Name contains 'a'"));
            nameResult.Error.Should().BeEmpty();
            nameResult.Height.Should().Be(1);

            var countResult = await context.RunTabularQueryAsync("data | where Count > 50");
            countResult.Error.Should().BeEmpty();
            countResult.Height.Should().Be(1);
        }


        [TestMethod]
        public async Task Count()
        {
            var context = new KustoQueryContext();
            var rows = Enumerable.Range(0, 50000).Select(i => new Row(i.ToString(), i)).ToImmutableArray();

            context.AddTableFromRecords("data", rows);
            var result = (await context.RunTabularQueryAsync("data | count"));
            KustoFormatter.Tabulate(result).Should().Contain("50000");
        }


        [TestMethod]
        public async Task Where()
        {
            var context = new KustoQueryContext();
            var rows = Enumerable.Range(0, 50000).Select(i => new Row(i.ToString(), i)).ToImmutableArray();

            context.AddTableFromRecords("data", rows);
            var result = (await context.RunTabularQueryAsync("data | where Value < 10 | count"));
            KustoFormatter.Tabulate(result).Should().Contain("10");
        }
    }

    public readonly record struct Row(string Name, int Value);
}
