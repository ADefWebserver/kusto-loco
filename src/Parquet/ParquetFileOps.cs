﻿using KustoLoco.Core;
using KustoLoco.Core.Util;
using KustoSupport;
using NLog;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace ParquetSupport;

public class ParquetFileOps : ITableLoader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task Save(string path, KustoQueryResult result)
    {
        await using Stream fs = File.OpenWrite(path);
        await SaveToStream(fs, result);
    }

    private static Array CreateArrayFromRawObjects(ColumnResult r, KustoQueryResult res)
    {
        Logger.Info("using builder");
        var builder = ColumnHelpers.CreateBuilder(r.UnderlyingType);
        foreach (var o in res.EnumerateColumnData(r))
            builder.Add(o);
        return builder.GetDataAsArray();
    }

    public static async Task SaveToStream(Stream fs, KustoQueryResult result)
    {
        Logger.Info("writing to stream");
        var datafields = result.ColumnDefinitions()
            .Select(col =>
                new DataField(col.Name, col.UnderlyingType, true))
            .ToArray();
        Logger.Info("created datafields");
        var schema = new ParquetSchema(datafields.Cast<Field>().ToArray());
        Logger.Info("Created schema");
        var columns = result
            .ColumnDefinitions()
            .Select(col =>
                {
                    Logger.Info($"creating datacol {col.Name} {col.UnderlyingType}");
                    return new DataColumn(
                        datafields[col.Index],
                        CreateArrayFromRawObjects(col, result));
                }
            )
            .ToArray();


        using var writer = await ParquetWriter.CreateAsync(schema, fs);
        using var groupWriter = writer.CreateRowGroup();

        foreach (var c in columns)
            await groupWriter.WriteColumnAsync(c);
    }

    public static async Task<ITableSource> LoadFromFile(string path, string tableName)
    {
        using var fs = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(fs);
        var rg = await reader.ReadEntireRowGroupAsync();
        var tableBuilder = TableBuilder.CreateEmpty(tableName, rg.GetLength(0));
        foreach (var c in rg)
        {
            var type = c.Field.ClrType;
            Logger.Debug($"reading column {c.Field.Name} of type {c.Field.Name}");
            var colBuilder = ColumnHelpers.CreateBuilder(type);
            foreach (var o in c.Data)
            {
                colBuilder.Add(o);
            }

            tableBuilder.WithColumn(c.Field.Name, colBuilder.ToColumn());
        }

        return tableBuilder.ToTableSource();
    }

    public async Task<TableLoadResult> LoadTable(string path, string tableName, IProgress<string> progressReporter)
    {
        var table = await LoadFromFile(path, tableName);
        return new TableLoadResult(table,string.Empty);
    }

    public bool RequiresTypeInference { get; } = false;
}