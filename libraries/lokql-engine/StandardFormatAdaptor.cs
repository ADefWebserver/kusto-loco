﻿
using KustoLoco.FileFormats;
using KustoLoco.Core;
namespace Lokql.Engine;

/// <summary>
/// Table IO adaptor that can load and save tables for in a variety of standard formats
/// </summary>
/// <remarks>
/// This class is used by the interactive shell to choose the appropriate adaptor based on
/// the file extension.  For example, if the user tries to load a file called "mydata.csv",
/// the CsvTableAdaptor will be used to load the file.
///
/// The adaptor will try to load the file from a list of paths, in order
/// unless the path is fully qualified.
/// </remarks>
public class StandardFormatAdaptor : ITableAdaptor
{
    private readonly IReadOnlyCollection<IFileBasedTableAccess> _loaders;
    private readonly string[] _paths;
    private readonly KustoSettings _settings;

    public StandardFormatAdaptor(KustoSettings settings,params string[] paths)
    {
        _paths = paths;
        _settings = settings;
        _loaders =
        [
            new CsvTableAdaptor(),
            new TsvTableAdaptor(),
            new ParquetTableAdaptor(),
            new TextTableAdaptor(),
            new JsonArrayTableAdaptor()
        ];
    }

    public async Task LoadTablesAsync(KustoQueryContext context, IReadOnlyCollection<string> tableNames)
    {
        foreach (var path in tableNames)
        {
            if (await LoadTable(context, path, path,new NullProgressReporter()))
                continue;
            break;
        }
    }

    public async Task SaveResult(KustoQueryResult result, string path)
    {
        var loader = GetFileLoaderForExtension(path);


        var filePaths = Path.IsPathRooted(path)
            ? [path]
            : _paths.Select(p => Path.Combine(p, path));
        foreach (var filepath in filePaths)
        {
            await loader.TrySave(filepath, result);
            //todo -here -  quick hack to ensure we only save to one place!
            break;
        }
    }

    public IEnumerable<TableAdaptorDescription> GetSupportedAdaptors()
    {
       return _loaders.Select(l => l.GetDescription()).ToArray();
    }

    public async Task<bool> LoadTable(KustoQueryContext context, string path, string tableName,IProgress<string> progressReporter)
    {
        var alreadyPresent = context.HasTable(tableName);
        if (alreadyPresent)
            return true;

        var loader = GetFileLoaderForExtension(path);


        var filePaths = Path.IsPathRooted(path)
            ? [path]
            : _paths.Select(p => Path.Combine(p, path));
        foreach (var filepath in filePaths)
        {
            if (!Path.Exists(filepath)) break;

            var success = await loader.TryLoad(filepath, context, tableName,progressReporter,_settings);
            if (success)
            {
                progressReporter.Report($"Loaded table '{tableName}' from {filepath}");
                return true;
            }
        }

        return false;
    }

    public IFileBasedTableAccess GetFileLoaderForExtension(string oFile)
    {
        var ext = Path.GetExtension(oFile);
        foreach (var loader in _loaders)
        {
            if (loader.SupportedFileExtensions().Contains(ext))
                return loader;
        }

        return new NullFileLoader();
    }
}
