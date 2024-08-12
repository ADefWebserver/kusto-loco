﻿using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using KustoLoco.Core;
using KustoLoco.Core.Console;
using KustoLoco.Core.Settings;
using KustoLoco.Core.Util;
#pragma warning disable CS8604 // Possible null reference argument.

namespace KustoLoco.FileFormats;

public class CsvSerializer : ITableSerializer
{
    private readonly CsvConfiguration _config;
    private readonly IKustoConsole _console;
    private readonly KustoSettingsProvider _settings;

    public CsvSerializer(CsvConfiguration config, KustoSettingsProvider settings, IKustoConsole console)
    {
        _config = config;
        _settings = settings;
        _console = console;
        settings.Register(CsvSerializerSettings.SkipTypeInference,
            CsvSerializerSettings.TrimCells,
            CsvSerializerSettings.SkipHeaderOnSave,
            CsvSerializerSettings.InferColumnNames);
    }

    public async Task<TableSaveResult> SaveTable(string path, KustoQueryResult result)
    {
        await using var stream = File.Create(path);
        return await SaveTable(stream, result);
    }


    public Task<TableLoadResult> LoadTable(Stream stream, string tableName)
    {
        var keys = Array.Empty<string>();
        var builders = Array.Empty<ColumnBuilder<string>>();
        var inferColumnNames = _settings.GetBool(CsvSerializerSettings.InferColumnNames);
        using var reader = new StreamReader(stream);
        var config = _config with { HasHeaderRecord = !inferColumnNames };
        var csv = new CsvReader(reader, config);
       
        if (!inferColumnNames)
        {
            csv.Read();
            csv.ReadHeader();
            keys = csv.Context.Reader?.HeaderRecord;
            builders= keys
                .Select(_ => new ColumnBuilder<string>())
                .ToArray();
        }

        var rowCount = 0;
        while (csv.Read())
        {
            if (!builders.Any())
            {
                keys = Enumerable.Range(0, csv.ColumnCount).Select(c => $"Column{c}").ToArray();
                builders = keys
                    .Select(_ => new ColumnBuilder<string>())
                    .ToArray();
            }

            var isTrimRequired = _settings.GetBool(CsvSerializerSettings.TrimCells);

            string TrimIfRequired(string s)
            {
                return isTrimRequired ? s.Trim() : s;
            }
           
            for (var i = 0; i < builders.Length; i++) builders[i].Add(TrimIfRequired(csv.GetField<string>(i)));

            rowCount++;
            if (rowCount % 100_000 == 0)
                _console.ShowProgress($"loaded {rowCount} records");
        }

        var tableBuilder = TableBuilder.CreateEmpty(tableName, rowCount);
        for (var i = 0; i < keys.Length; i++) tableBuilder.WithColumn(keys[i], builders[i].ToColumn());

        var tableSource = tableBuilder.ToTableSource();
        _console.CompleteProgress($"loaded {rowCount} records");

        if (!_settings.GetBool(CsvSerializerSettings.SkipTypeInference))
            tableSource = TableBuilder.AutoInferColumnTypes(tableSource, _console);
        return Task.FromResult(TableLoadResult.Success(tableSource));
    }


    public async Task<TableLoadResult> LoadTable(string filename, string tableName)
    {
        try
        {
            await using var fileReader = File.OpenRead(filename);
            return await LoadTable(fileReader, tableName);
        }
        catch (Exception e)
        {
            var message = e.Message;
            //add helpful hint fo common error
            if (message.Contains("because it is being used by another process"))
                message += " Is the file open in Excel or another program?";
            return TableLoadResult.Failure(message);
        }
    }


    public Task<TableSaveResult> SaveTable(Stream stream, KustoQueryResult result)
    {
        if (result.ColumnCount == 0)
        {
            _console.Warn("No columns in result - empty file/stream written");
            return Task.FromResult(TableSaveResult.Success());
        }

        using var writer = new StreamWriter(stream);
        using var csv = new CsvWriter(writer, _config);
        if (!_settings.GetBool(CsvSerializerSettings.SkipHeaderOnSave))
        {
            foreach (var heading in result.ColumnNames())
                csv.WriteField(heading);
            csv.NextRecord();
        }

        var rowCount = 0;
        foreach (var r in result.EnumerateRows())
        {
            rowCount++;
            if (rowCount % 100_000 == 0)
                _console.ShowProgress($"wrote {rowCount} records");
            foreach (var cell in r)
            {
                var toPrint = cell is DateTime dt
                    ? dt.ToString("o", CultureInfo.InvariantCulture)
                    : Convert.ToString(cell, CultureInfo.InvariantCulture);
                csv.WriteField(toPrint);
            }

            csv.NextRecord();
        }

        _console.CompleteProgress($"wrote {rowCount} records");
        return Task.FromResult(TableSaveResult.Success());
    }


    public static CsvSerializer Default(KustoSettingsProvider settings, IKustoConsole console)
    {
        return new CsvSerializer(new CsvConfiguration(CultureInfo.InvariantCulture), settings, console);
    }

    public static CsvSerializer Tsv(KustoSettingsProvider settings, IKustoConsole console)
    {
        return new CsvSerializer(new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = "\t"
            }
            , settings, console);
    }


    public ITableSource LoadFromString(string csv, string tableName)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv.Trim()));
        var result = LoadTable(stream, tableName).Result;
        return result.Table;
    }


    private static class CsvSerializerSettings
    {
        private const string Prefix = "csv";

        public static readonly KustoSettingDefinition SkipTypeInference = new(
            Setting("SkipTypeInference"), "prevents conversion of string columns to types",
            "off",
            nameof(Boolean));


        public static readonly KustoSettingDefinition TrimCells = new(Setting("TrimCells"),
            "Removes leading and trailing whitespace from string values", "true", nameof(Boolean));

        public static readonly KustoSettingDefinition SkipHeaderOnSave = new(Setting("SkipHeaderOnSave"),
            "Don't write header row when saving CSV files", "false", nameof(Boolean));



        public static readonly KustoSettingDefinition InferColumnNames = new(Setting("InferColumnNames"),
            "Infer column names", "false", nameof(Boolean));

        private static string Setting(string setting)
        {
            return $"{Prefix}.{setting}";
        }
    }
}
