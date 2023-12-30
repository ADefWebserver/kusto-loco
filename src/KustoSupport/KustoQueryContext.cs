﻿using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using BabyKusto.Core;
using BabyKusto.Core.Evaluation;
using Extensions;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using NLog;

#pragma warning disable CS8603 // Possible null reference return.
#pragma warning disable CS8604 // Possible null reference argument.

namespace KustoSupport;

/// <summary>
///     Provides a context for complex queries or persistent tables
/// </summary>
public class KustoQueryContext
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly BabyKustoEngine _engine = new();
    private readonly List<BaseKustoTable> _tables = new();

    private bool _fullDebug;

    private IKustoQueryContextTableLoader _lazyTableLoader = new NullTableLoader();


    public IEnumerable<string> TableNames => Tables().Select(t => t.Name);

    public void AddTable(BaseKustoTable table)
    {
        if (_tables.Any(t => t.Name == table.Name))
            throw new ArgumentException($"Context already contains a table named '{table.Name}'");
        _tables.Add(table);
        _engine.AddGlobalTable(table);
    }

    public BaseKustoTable GetTable(string name)
    {
        return _tables.Single(t => UnescapeTableName(t.Name) == name);
    }

    public void AddTableFromRecords<T>(string tableName, IReadOnlyCollection<T> records)
    {
        var table = TableBuilder.CreateFromRows(tableName, records);
        AddTable(table);
    }


    public void AddChunkedTableFromRecords<T>(string tableName, IReadOnlyCollection<T> records, int chunkSize)
    {
        var table = TableBuilder.CreateFromRows(tableName, records);
        var chunked = ChunkedKustoTable.FromTable(table, chunkSize);
        AddTable(chunked);
    }

    public int BenchmarkQuery(string query)
    {
        var res = _engine.Evaluate(query, _fullDebug, _fullDebug);
        return Materialise(res as TabularResult);
    }

    public (VisualizationState, IReadOnlyCollection<OrderedDictionary>) RunTabularQueryToDictionarySet(string query)
    {
        //handling for "special" commands
        if (query.Trim() == ".tables")
        {
            return (VisualizationState.Empty, _tables.Select(table => new OrderedDictionary
                    {
                        ["Name"] = table.Name,
                        ["Length"] = table.Length,
                    }
                )
                .ToArray());
        }

        var result =
            _engine.Evaluate(query,
                _fullDebug, _fullDebug
            );
        if (result is TabularResult res)
        {
            return (res.VisualizationState, GetDictionarySet(res));
        }

        if (result is ScalarResult s)
        {
            var o = new OrderedDictionary
            {
                ["value"] = s.Value
            };
            return (VisualizationState.Empty, new[]
            {
                o
            });
        }

        return (VisualizationState.Empty, GetDictionarySet(TabularResult.Empty));
    }

    //temporary until we plumb lazy table load back in
    public Task<KustoQueryResult> RunTabularQueryAsync(string query) => Task.FromResult(RunTabularQuery(query));

    public KustoQueryResult RunTabularQuery(string query)
    {
        var watch = Stopwatch.StartNew();
        //handling for "special" commands
        if (query.Trim() == ".tables")
        {
            //TODO
        }

        try
        {
            var result =
                _engine.Evaluate(query,
                    _fullDebug, _fullDebug
                );
            return new KustoQueryResult(query, result, (int)watch.ElapsedMilliseconds,
                string.Empty);
        }
        catch (Exception ex)
        {
            return new KustoQueryResult(query, EvaluationResult.Null, 0, ex.Message);
        }
    }


    public void AddLazyTableLoader(IKustoQueryContextTableLoader loader) => _lazyTableLoader = loader;

    private async Task<KustoQueryResult> RunQuery(string query)
    {
        try
        {
            // Get tables referenced in query
            var requiredTables = GetTableList(query);

            // Ensure that tables are loaded into the query context
            await _lazyTableLoader.LoadTablesAsync(this, requiredTables);

            // Note: Hold the lock until the query is complete to ensure that tables don't change
            // in the middle of execution.
            return await RunTabularQueryAsync(query);
        }
        catch (Exception ex)
        {
            return new KustoQueryResult(query, EvaluationResult.Null, 0, ex.Message);
        }
    }


    public static IReadOnlyCollection<OrderedDictionary> GetDictionarySet(TabularResult tabularResult)
    {
        var items = new List<OrderedDictionary>();

        var table = tabularResult.Value;
        foreach (var chunk in table.GetData())
        {
            for (var i = 0; i < chunk.RowCount; i++)
            {
                var d = new OrderedDictionary();
                for (var c = 0; c < chunk.Columns.Length; c++)
                {
                    var dataValue = chunk.Columns[c].GetRawDataValue(i);
                    var columnName = table.Type.Columns[c].Name;
                    d[columnName] = dataValue;
                }

                items.Add(d);
            }
        }


        return items;
    }

    public static int Materialise(TabularResult tabularResult)
    {
        var count = 0;

        var table = tabularResult.Value;
        foreach (var chunk in table.GetData())
        {
            for (var i = 0; i < chunk.RowCount; i++)
            {
                for (var c = 0; c < chunk.Columns.Length; c++)
                {
                    var v = chunk.Columns[c].GetRawDataValue(i);
                    count++;
                }
            }
        }


        return count;
    }


    /// <summary>
    ///     Deserialises a Dictionary-based result to objects
    /// </summary>
    public static IReadOnlyCollection<T> DeserialiseTo<T>(KustoQueryResult results)
    {
        //this is horrible but I don't have time to research how to do it ourselves and the bottom line
        //is that we are expecting results sets to be small to running through the JsonSerializer is
        //"good enough" for now...


        var json = JsonSerializer.Serialize(results.AsOrderedDictionarySet());
        return JsonSerializer.Deserialize<T[]>(json);
    }

    /// <summary>
    ///     Gets a list of all table references within a Kusto query
    /// </summary>
    /// <remarks>
    ///     Taken from
    ///     https://stackoverflow.com/questions/73322172/putting-all-table-names-that-a-kql-query-uses-into-a-list-in-c-sharp
    /// </remarks>
    public static IReadOnlyCollection<string> GetTableList(string query)
    {
        var tables = new List<string>();
        var code = KustoCode.Parse(query).Analyze();

        SyntaxElement.WalkNodes(code.Syntax,
            @operator =>
            {
                if (@operator is Expression e && e.RawResultType is TableSymbol &&
                    @operator.Kind.ToString() == "NameReference")
                    tables.Add(e.RawResultType.Name);
            });
        // //special case handling for when query is _only_ a table name without any operators
        if (!tables.Any() && query.Tokenise().Length == 1)
        {
            tables.Add(query.Trim());
        }

        return tables.Select(UnescapeTableName).Distinct().ToArray();
    }

    public IEnumerable<BaseKustoTable> Tables() => _tables;

    public static string UnescapeTableName(string tableName)
    {
        if ((tableName.StartsWith("['") && tableName.EndsWith("']")) ||
            (tableName.StartsWith("[\"") && tableName.EndsWith("\"]"))
           )
            return tableName.Substring(2, tableName.Length - 4);
        return tableName;
    }

    public static string EnsureEscapedTableName(string tableName) => $"['{UnescapeTableName(tableName)}']";

    public static KustoQueryContext WithFullDebug()
    {
        var context = new KustoQueryContext
        {
            _fullDebug = true
        };
        return context;
    }
}