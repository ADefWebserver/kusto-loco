﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kusto.Language.Symbols;

namespace BabyKusto.Core.Extensions
{
    public static class TableSourceExtensions
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void Dump(this ITableSource table, TextWriter writer, int indent = 0)
        {
            WriteIndent(writer, indent);

            for (int i = 0; i < table.Type.Columns.Count; i++)
            {
                if (i > 0)
                {
                    writer.Write("; ");
                }

                writer.Write(table.Type.Columns[i].Name);
                writer.Write(":");
                writer.Write(SchemaDisplay.GetText(table.Type.Columns[i].Type));
            }
            writer.WriteLine();

            WriteIndent(writer, indent);
            writer.WriteLine("------------------");

            foreach (var chunk in table.GetData())
            {
                for (int i = 0; i < chunk.RowCount; i++)
                {
                    WriteIndent(writer, indent);
                    for (int j = 0; j < chunk.Columns.Length; j++)
                    {
                        if (j > 0)
                        {
                            writer.Write("; ");
                        }

                        var v = chunk.Columns[j].RawData.GetValue(i);
                        if (chunk.Columns[j].Type == ScalarTypes.String)
                        {
                            writer.Write((string?)v ?? string.Empty);
                        }
                        else
                        {
                            writer.Write(
                                v switch
                                {
                                    DateTime dateTime => dateTime.ToString("O"),
                                    JsonNode jsonNode => jsonNode.ToJsonString(JsonOptions),
                                    null => "(null)",
                                    _ => v,
                                });
                        }
                    }
                    writer.WriteLine();
                }
            }
        }

        public static string DumpToString(this ITableSource table, int indent = 0)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            table.Dump(writer, indent);
            return writer.ToString();
        }

        private static void WriteIndent(TextWriter writer, int indent)
        {
            if (indent > 0)
            {
                writer.Write(new string(' ', indent));
            }
        }
    }
}