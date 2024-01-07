﻿using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text;
using BabyKusto.Core.Evaluation;
using CommandLine;
using Extensions;
using KustoSupport;
using NLog;
using ParquetSupport;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

#pragma warning disable CS8604 // Possible null reference argument.

internal class ReportExplorer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly KustoQueryContext _context = new();

    private readonly FolderContext _folders;
    private readonly OurTableLoader _loader;
    private readonly StringBuilder commandBuffer = new();

    private DisplayOptions _currentDisplayOptions = new(FormatTypes.Ascii, 10);
    private KustoQueryResult _prevResult;

    public ReportExplorer(FolderContext folders)
    {
        _loader = new OurTableLoader(folders.OutputFolder);
        _context.AddLazyTableLoader(_loader);
        _folders = folders;
    }

    private KustoQueryContext GetCurrentContext() => _context;

    private void DisplayResults(KustoQueryResult result)
    {
        if (result.Error.IsNotBlank())
            ShowError(result.Error);
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            if (result.Height == 0)
            {
                Console.WriteLine("No results");
            }
            else
            {
                var max = _currentDisplayOptions.MaxToDisplay;

                Console.WriteLine(KustoFormatter.Tabulate(result, max));

                if (_currentDisplayOptions.MaxToDisplay < result.Height)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(
                        $"Display was truncated to first {max} of {result.Height}.  Use '.display --max' to change this behaviour");
                }

                //auto render
                if (result.Visualization != VisualizationState.Empty)
                {
                    RenderCommand.Run(this, new RenderCommand.Options());
                }
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Query took {result.QueryDuration}ms");
        }
    }

    public async Task RunInteractive(string initialScript)
    {
        Console.WriteLine("Use '.help' to list commands");

        if (initialScript.IsNotBlank())
        {
            await RunInput($".run {initialScript}");
        }


        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write("KQL> ");
            var query = Console.ReadLine();
            await RunInput(query);
        }
    }


    private async Task ExecuteAsync(string line)
    {
        Console.ForegroundColor = ConsoleColor.Blue;

        Console.WriteLine($"KQL> {line}");
        await RunInput(line);
    }

    private async Task RunInput(string query)
    {
        query = query.Trim();
        //support comments
        if (query.StartsWith("#") | query.IsBlank()) return;
        if (query.EndsWith("\\"))
        {
            commandBuffer.Append(query.Substring(0, query.Length - 1) + " ");
            return;
        }

        if (query.EndsWith("|"))
        {
            commandBuffer.Append(query);
            return;
        }

        commandBuffer.Append(query);
        query = commandBuffer.ToString().Trim();
        commandBuffer.Clear();
        try
        {
            if (query.StartsWith("."))
            {
                var splitter = CommandLineStringSplitter.Instance;
                var tokens = splitter.Split(query.Substring(1)).ToArray();

                switch (tokens[0])
                {
                    case "tables":
                        break;
                    default:
                        await RunInternalCommand(tokens);
                        return;
                }
            }


            var result = await GetCurrentContext().RunTabularQueryAsync(query);
            _prevResult = result;
            DisplayResults(result);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }

        Console.WriteLine();
    }


    private static async Task ToParquet(string path, KustoQueryResult result)
    {
        await ParquetFileOps.Save(path, result);
    }

    private static void ShowError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Error:");
        Console.WriteLine(message);
    }


    private static string ToFullPath(string file, string folder, string extension)
    {
        var path = Path.IsPathRooted(file)
            ? file
            : Path.Combine(folder, file);
        if (!Path.HasExtension(path))
            path = Path.ChangeExtension(path, extension);
        return path;
    }

    internal readonly record struct FolderContext(
        string OutputFolder,
        string ScriptFolder,
        string QueryFolder);

    internal enum FormatTypes
    {
        Ascii,
        Json,
        Csv,
        Txt,
        Unspecified,
        Parquet
    }

    internal readonly record struct DisplayOptions(FormatTypes Format, int MaxToDisplay);

    #region internal commands

    private Task RunInternalCommand(string[] args)
    {
        return StandardParsers.Default.ParseArguments(args,
                    typeof(ExitCommand.Options),
                    typeof(SaveCommand.Options),
                    typeof(RenderCommand.Options),
                    typeof(LoadCommand.Options),
                    typeof(FormatCommand.Options),
                    typeof(RunScriptCommand.Options),
                    typeof(QueryCommand.Options),
                    typeof(SaveQueryCommand.Options),
                    typeof(MaterializeCommand.Options),
                    typeof(SynTableCommand.Options),
                    typeof(AllTablesCommand.Options)
                )
                .WithParsed<MaterializeCommand.Options>(o => MaterializeCommand.Run(this, o))
                .WithParsed<RenderCommand.Options>(o => RenderCommand.Run(this, o))
                .WithParsed<AllTablesCommand.Options>(o => AllTablesCommand.Run(this, o))
                .WithParsed<ExitCommand.Options>(o => ExitCommand.Run(this, o))
                .WithParsed<FormatCommand.Options>(o => FormatCommand.Run(this, o))
                .WithParsed<SynTableCommand.Options>(o => SynTableCommand.Run(this, o))
                .WithParsedAsync<RunScriptCommand.Options>(o => RunScriptCommand.RunAsync(this, o))
                .WithParsedAsync<SaveQueryCommand.Options>(o => SaveQueryCommand.RunAsync(this, o))
                .WithParsedAsync<LoadCommand.Options>(o => LoadCommand.RunAsync(this, o))
                .WithParsedAsync<SaveCommand.Options>(o => SaveCommand.RunAsync(this, o))
                .WithParsedAsync<QueryCommand.Options>(o => QueryCommand.RunAsync(this, o))
            ;
    }


    public static class RunScriptCommand
    {
        internal static async Task RunAsync(ReportExplorer exp, Options o)
        {
            var filename = ToFullPath(o.File, exp._folders.ScriptFolder, ".dfr");
            Logger.Info($"Loading '{filename}'..");
            var lines = await File.ReadAllLinesAsync(filename);
            foreach (var line in lines)
            {
                await exp.ExecuteAsync(line);
            }
        }

        [Verb("run", aliases: ["script", "r"],
            HelpText = "run a script")]
        internal class Options
        {
            [Value(0, HelpText = "Name of script", Required = true)]
            public string File { get; set; }
        }
    }

    public static class QueryCommand
    {
        internal static async Task RunAsync(ReportExplorer exp, Options o)
        {
            var filename = ToFullPath(o.File, exp._folders.QueryFolder, ".csl");
            Logger.Info($"Fetching query '{filename}'");
            var query = await File.ReadAllTextAsync(filename);
            await exp.ExecuteAsync($"{o.Prefix}{query}");
        }

        [Verb("query", aliases: ["q"],
            HelpText = "run a multi-line query from a file")]
        internal class Options
        {
            [Value(0, HelpText = "Name of queryFile", Required = true)]
            public string File { get; set; } = string.Empty;

            [Option('p', HelpText = "prefix to add to script before running")]
            public string Prefix { get; set; } = string.Empty;
        }
    }

    public static class SaveQueryCommand
    {
        internal static async Task RunAsync(ReportExplorer exp, Options o)
        {
            var filename = ToFullPath(o.File, exp._folders.QueryFolder, ".csl");
            Logger.Info($"Saving query to '{filename}'");
            var q = exp._prevResult.Query;
            if (!o.NoSplit)
                q = q
                        .Tokenise("|")
                        .JoinString($"{Environment.NewLine}| ")
                        .Tokenise(";")
                        .JoinString($";{Environment.NewLine}")
                    ;

            var text = $@"//{o.Comment}
{q}
";
            await File.WriteAllTextAsync(filename, text);
        }

        [Verb("savequery", aliases: ["sq"],
            HelpText = "save a query to a file so you can use it again")]
        internal class Options
        {
            [Value(0, HelpText = "Name of queryFile", Required = true)]
            public string File { get; set; }

            [Option('c', HelpText = "Adds a comment")]
            public string Comment { get; set; } = string.Empty;

            [Option(HelpText = "Prevents query being split into multiple lines at '|' and ';'")]
            public bool NoSplit { get; set; }
        }
    }

    public static class SaveCommand
    {
        internal static async Task RunAsync(ReportExplorer exp, Options o)
        {
            await exp._loader.SaveResult(exp._prevResult, o.File);
        }

        [Verb("save", aliases: ["sv"], HelpText = "save last results to file")]
        internal class Options
        {
            [Value(0, HelpText = "Name of file", Required = true)]
            public string File { get; set; } = string.Empty;


            [Option('b', "bare", HelpText = "Skips header for csv (useful when generating id lists)")]
            public bool SkipHeader { get; set; }
        }
    }


    public static class RenderCommand
    {
        internal static void Run(ReportExplorer exp, Options o)
        {
            var fileName = Path.ChangeExtension(o.File.OrWhenBlank(Path.GetTempFileName()), "html");
            var result = exp._prevResult;
            // var text = KustoResultRenderer.RenderToTable(result);
            var text = KustoResultRenderer.RenderToHtml(result.Query, result);
            File.WriteAllText(fileName, text);
            Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
        }

        [Verb("render", aliases: ["ren"], HelpText = "render last results as html")]
        internal class Options
        {
            [Value(0, HelpText = "Name of file")] public string File { get; set; } = string.Empty;

            [Value(1, HelpText = "Format: ascii/json/csv (default is csv)")]
            public FormatTypes Format { get; set; } = FormatTypes.Csv;
        }
    }

    public static class MaterializeCommand
    {
        internal static void Run(ReportExplorer exp, Options o)
        {
            exp.GetCurrentContext()
                .AddTable(TableBuilder
                    .FromOrderedDictionarySet(o.As,
                        exp._prevResult.AsOrderedDictionarySet()));
            Logger.Info($"Table '{o.As}' now available");
        }

        [Verb("materialize", aliases: ["materialise", "mat"],
            HelpText = "save last results back into context as a table")]
        internal class Options
        {
            [Value(0, Required = true, HelpText = "Name of table")]
            public string As { get; set; } = string.Empty;
        }
    }


    public static class AllTablesCommand
    {
        internal static void Run(ReportExplorer exp, Options o)
        {
            var context = exp._context;
            var tableNames = context.TableNames
                .Select(t => $"['{t}']")
                .JoinAsLines();
            Console.WriteLine(tableNames);
        }

        [Verb("listtables", aliases: ["ls", "alltables", "at"],
            HelpText = "Lists all available tables in the global context")]
        internal class Options
        {
        }
    }

    public static class LoadCommand
    {
        internal static async Task RunAsync(ReportExplorer exp, Options o)
        {
            var tableName = o.As.OrWhenBlank(o.File);
            await exp._loader.LoadTable(exp._context, o.File, tableName);
        }

        [Verb("load", aliases: ["ld"],
            HelpText = "loads a data file")]
        internal class Options
        {
            [Value(0, HelpText = "Name of file", Required = true)]
            public string File { get; set; } = string.Empty;

            [Value(1, HelpText = "Name of table (defaults to name of file)")]
            public string As { get; set; } = string.Empty;
        }
    }


    public static class SynTableCommand
    {
        internal static void Run(ReportExplorer exp, Options o)
        {
            Logger.Info($"Creating synonym for table {o.CurrentName} as {o.As} ...");
            //TODO - hide until a better way of sharing tables is found
            throw new NotImplementedException();
            /*
            var table = exp.GetCurrentContext().GetTable(o.CurrentName) as TableBuilder;
            exp.GetCurrentContext().AddTable(table.ShareAs(KustoQueryContext.UnescapeTableName(o.As)));
            */
        }

        [Verb("synomym", aliases: ["syn", "alias"], HelpText = "provides a synonym for a table")]
        internal class Options
        {
            [Value(0, HelpText = "table name", Required = true)]
            public string CurrentName { get; set; } = string.Empty;

            [Value(1, HelpText = "Synonym", Required = true)]
            public string As { get; set; } = string.Empty;
        }
    }

    private static class FormatCommand
    {
        internal static void Run(ReportExplorer exp, Options o)
        {
            if (o.Format != FormatTypes.Unspecified)
                exp._currentDisplayOptions = exp._currentDisplayOptions with { Format = o.Format };
            if (o.Max > 0)
                exp._currentDisplayOptions = exp._currentDisplayOptions with { MaxToDisplay = o.Max };
            Logger.Info($"Set: {exp._currentDisplayOptions}");
        }

        [Verb("display", aliases: ["d"], HelpText = "change the output format")]
        internal class Options
        {
            [Option('f', HelpText = "Format: ascii/json/csv")]
            public FormatTypes Format { get; } = FormatTypes.Unspecified;


            [Option('m', HelpText = "Maximum number of items to display in console")]
            public int Max { get; } = -1;
        }
    }


    private static class ExitCommand
    {
        internal static void Run(ReportExplorer exp, Options o)
        {
            Logger.Info("Exiting...");
            Environment.Exit(0);
        }

        [Verb("exit", aliases: ["quit"], HelpText = "Exit application")]
        internal class Options;
    }

    #endregion
}